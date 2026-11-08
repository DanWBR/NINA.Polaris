// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Services;

/// <summary>
/// Per-frame background matching and weighting for the live stack.
///
/// <para>The live stack summed every frame as it came. Two things went wrong
/// with that on a real night (Abell 370, 2026-11-07): thin cloud for the first
/// twenty minutes pushed the sky from ~1300 to as much as 4400 ADU, and the
/// field drifted ~70 px through the session. Where the drift left only the
/// early frames covering the edge of the stack, the mean there was a cloudy
/// sky's; everywhere else it was mostly clear frames. The result was bright
/// bands along three edges of the stack, and twenty noisy frames counted the
/// same as a hundred clean ones.</para>
///
/// <para>So each frame's background is shifted to the reference frame's,
/// per colour channel (or per CFA site), before it is integrated, the
/// additive half of Siril's <c>addscale</c> normalisation. And each frame is
/// weighted by its signal-to-noise against the reference,
/// <c>(S/S_ref)² · (σ_ref/σ)²</c>, with σ the background noise and S the
/// brightness of its brightest stars, so cloud that brightens the sky or dims
/// the stars costs the frame its share instead of the stack its quality.</para>
///
/// Pure functions over the frame buffers; the stacker owns the state.
/// </summary>
public static class LiveStackFrameNorm {
    /// <summary>Background (per plane or CFA site) and noise of one frame.</summary>
    public sealed record Stats(double[] Background, double Noise);

    /// <summary>Weights are clamped to this range so a frame can neither take
    /// over the stack nor vanish from it entirely because of one bad
    /// measurement.</summary>
    public const double MinWeight = 0.05, MaxWeight = 4.0;

    // Sample every Nth pixel in each direction: 3008² → ~180k samples, which
    // pins a median to well under an ADU and costs a few ms per plane.
    private const int Stride = 7;

    /// <summary>Stats of debayered colour planes; noise is measured on
    /// <paramref name="noisePlane"/> (green).</summary>
    public static Stats ForPlanes(IReadOnlyList<ushort[]> planes, int width, int height, int noisePlane = 1) {
        var bg = new double[planes.Count];
        double noise = 0;
        var buf = Rent(width, height, 1);
        try {
            for (int p = 0; p < planes.Count; p++) {
                int n = Sample(planes[p], width, height, 0, 0, 1, buf);
                bg[p] = MedianInPlace(buf, n);
                if (p == noisePlane) noise = MadInPlace(buf, n, bg[p]) * 1.4826;
            }
        } finally { System.Buffers.ArrayPool<float>.Shared.Return(buf); }
        return new Stats(bg, noise);
    }

    /// <summary>Stats of a mono frame, or of a CFA mosaic per site (index
    /// <c>(y&amp;1)*2 + (x&amp;1)</c>). Noise is the median of the sites'.</summary>
    public static Stats ForMosaic(ushort[] data, int width, int height, bool cfa) {
        var buf = Rent(width, height, cfa ? 2 : 1);
        try {
            if (!cfa) {
                int n = Sample(data, width, height, 0, 0, 1, buf);
                double m = MedianInPlace(buf, n);
                return new Stats(new[] { m }, MadInPlace(buf, n, m) * 1.4826);
            }
            var bg = new double[4];
            var nz = new double[4];
            for (int site = 0; site < 4; site++) {
                int n = Sample(data, width, height, site & 1, site >> 1, 2, buf);
                bg[site] = MedianInPlace(buf, n);
                nz[site] = MadInPlace(buf, n, bg[site]) * 1.4826;
            }
            Array.Sort(nz);
            return new Stats(bg, (nz[1] + nz[2]) / 2);
        } finally { System.Buffers.ArrayPool<float>.Shared.Return(buf); }
    }

    /// <summary>Median flux of the brightest stars (the top third, at most 30):
    /// a transparency measure that ignores the faint end, which comes and goes
    /// with the detection threshold.</summary>
    public static double StarSignal(IReadOnlyList<DetectedStar>? stars) {
        if (stars == null || stars.Count == 0) return 0;
        int k = Math.Clamp(stars.Count / 3, 1, 30);
        var top = stars.Select(s => s.Flux).Where(f => f > 0).OrderByDescending(f => f).Take(k).ToArray();
        if (top.Length == 0) return 0;
        Array.Sort(top);
        return top[top.Length / 2];
    }

    /// <summary>The frame's weight against the reference. 1 when nothing can
    /// be measured.</summary>
    public static double Weight(double refNoise, double noise, double refSignal, double signal) {
        double w = 1;
        if (refNoise > 0 && noise > 0) w *= (refNoise / noise) * (refNoise / noise);
        if (refSignal > 0 && signal > 0) {
            double t = Math.Clamp(signal / refSignal, 0.2, 1.5);
            w *= t * t;
        }
        return double.IsFinite(w) ? Math.Clamp(w, MinWeight, MaxWeight) : 1;
    }

    /// <summary>Shift a plane's background by <paramref name="offset"/>
    /// (frame minus reference). Pixels with no data (0, off-canvas after the
    /// warp) stay 0, and data never becomes 0, so coverage is preserved.</summary>
    public static void Subtract(ushort[] plane, double offset) {
        if (Math.Abs(offset) < 0.5) return;
        Parallel.For(0, Partitions(plane.Length), part => {
            var (a, b) = Range(part, plane.Length);
            for (int i = a; i < b; i++) {
                ushort v = plane[i];
                if (v == 0) continue;
                plane[i] = (ushort)Math.Clamp(v - offset, 1, 65535);
            }
        });
    }

    /// <summary>Per-site shift of a CFA mosaic (or a single offset for mono).</summary>
    public static void SubtractMosaic(ushort[] data, int width, int height, double[] offsets) {
        if (offsets.Length == 1) { Subtract(data, offsets[0]); return; }
        if (offsets.All(o => Math.Abs(o) < 0.5)) return;
        Parallel.For(0, height, y => {
            int row = y * width;
            double o0 = offsets[(y & 1) * 2], o1 = offsets[(y & 1) * 2 + 1];
            for (int x = 0; x < width; x++) {
                ushort v = data[row + x];
                if (v == 0) continue;
                double o = (x & 1) == 0 ? o0 : o1;
                data[row + x] = (ushort)Math.Clamp(v - o, 1, 65535);
            }
        });
    }

    // ---- helpers ----

    // The samples go into a pooled buffer and are sorted in place: this runs
    // on every live-stack frame, whose per-frame allocations are guarded
    // (MemoryOptimizationTests), so nothing here may allocate per call.
    private static float[] Rent(int width, int height, int step) {
        int stride = Stride * step;
        return System.Buffers.ArrayPool<float>.Shared.Rent((width / stride + 1) * (height / stride + 1));
    }

    private static int Sample(ushort[] data, int width, int height, int x0, int y0, int step, float[] buf) {
        int stride = Stride * step;
        int n = 0;
        for (int y = y0; y < height; y += stride) {
            int row = y * width;
            for (int x = x0; x < width; x += stride) {
                ushort v = data[row + x];
                if (v != 0 && n < buf.Length) buf[n++] = v;
            }
        }
        return n;
    }

    private static double MedianInPlace(float[] buf, int n) {
        if (n == 0) return 0;
        Array.Sort(buf, 0, n);
        return n % 2 == 1 ? buf[n / 2] : (buf[n / 2 - 1] + buf[n / 2]) / 2.0;
    }

    private static double MadInPlace(float[] buf, int n, double median) {
        for (int i = 0; i < n; i++) buf[i] = (float)Math.Abs(buf[i] - median);
        return MedianInPlace(buf, n);
    }

    private static int Partitions(int n) => Math.Max(1, Math.Min(Environment.ProcessorCount * 4, n / 65536 + 1));

    private static (int, int) Range(int part, int n) {
        int parts = Partitions(n);
        long a = (long)n * part / parts, b = (long)n * (part + 1) / parts;
        return ((int)a, (int)b);
    }
}
