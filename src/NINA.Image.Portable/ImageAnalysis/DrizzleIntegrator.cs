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

using System;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Drizzle (variable-pixel linear reconstruction, Fruchter &amp; Hook 2002) for
/// one image plane. Each input pixel is a "drop" shrunk by <c>pixfrac</c> and
/// forward-projected (via its cur→ref <see cref="AffineTransform"/>) onto an
/// output grid that is <c>scale</c>× finer. The drop's flux is distributed to
/// the output pixels it overlaps, weighted by geometric overlap area; the final
/// image is <c>Σ(value·overlap) / Σ(overlap)</c>, which preserves surface
/// brightness and, when the inputs are sub-pixel dithered, reconstructs detail
/// lost to undersampling and reduces aliasing.
///
/// Only worthwhile for undersampled data (star FWHM &lt; ~2 px) with many
/// well-dithered subs; on well/over-sampled data scale &gt; 1 just amplifies
/// noise and enlarges the file. Drops are treated as axis-aligned squares
/// (rotation of the drop shape is ignored — a standard simplification that is
/// accurate for the small field rotations typical between subs).
///
/// One instance accumulates one plane; the caller creates one per colour plane
/// and feeds each frame once. Reimplemented from the published drizzle method;
/// not copied from any tool.
/// </summary>
public sealed class DrizzleIntegrator {
    private readonly int _inW, _inH;
    public int Scale { get; }
    public int OutW { get; }
    public int OutH { get; }
    private readonly double _half;      // half drop size in OUTPUT pixels
    private readonly float[] _data;     // Σ value·overlap
    private readonly float[] _weight;   // Σ overlap

    /// <param name="scale">Output upscale factor (1, 2, 3…).</param>
    /// <param name="pixfrac">Drop shrink in (0,1]; 1 = full pixel, smaller =
    /// sharper but needs more subs for full coverage. Typical 0.5–1.0.</param>
    public DrizzleIntegrator(int inWidth, int inHeight, int scale, double pixfrac) {
        if (scale < 1) scale = 1;
        pixfrac = Math.Clamp(pixfrac, 0.01, 1.0);
        _inW = inWidth;
        _inH = inHeight;
        Scale = scale;
        OutW = inWidth * scale;
        OutH = inHeight * scale;
        // An input pixel spans `scale` output pixels; the drop is that shrunk
        // by pixfrac, so its half-side in output pixels is pixfrac*scale/2.
        _half = pixfrac * scale / 2.0;
        _data = new float[(long)OutW * OutH];
        _weight = new float[(long)OutW * OutH];
    }

    /// <summary>
    /// Deposit one frame's plane. <paramref name="curToRef"/> is the transform
    /// that maps this frame's pixel coordinates onto the reference grid
    /// (identity for the reference frame itself), exactly as returned by
    /// <see cref="StarMatcher.Match"/>.
    /// </summary>
    public void AddFrame(ushort[] plane, AffineTransform curToRef) {
        double m00 = curToRef.M00, m01 = curToRef.M01, tx = curToRef.Tx;
        double m10 = curToRef.M10, m11 = curToRef.M11, ty = curToRef.Ty;
        double h = _half;
        for (int y = 0; y < _inH; y++) {
            int rowOff = y * _inW;
            for (int x = 0; x < _inW; x++) {
                ushort v = plane[rowOff + x];
                if (v == 0) continue; // treat 0 as no-signal (off-canvas after a prior warp, or true black)
                // Forward-project the pixel centre onto the reference grid,
                // then to the finer output grid.
                double refX = m00 * x + m01 * y + tx;
                double refY = m10 * x + m11 * y + ty;
                double ocx = (refX + 0.5) * Scale;   // +0.5: pixel index -> centre
                double ocy = (refY + 0.5) * Scale;

                double x0 = ocx - h, x1 = ocx + h;
                double y0 = ocy - h, y1 = ocy + h;
                int px0 = (int)Math.Floor(x0), px1 = (int)Math.Floor(x1 - 1e-9);
                int py0 = (int)Math.Floor(y0), py1 = (int)Math.Floor(y1 - 1e-9);
                if (px1 < 0 || py1 < 0 || px0 >= OutW || py0 >= OutH) continue;
                if (px0 < 0) px0 = 0;
                if (py0 < 0) py0 = 0;
                if (px1 >= OutW) px1 = OutW - 1;
                if (py1 >= OutH) py1 = OutH - 1;

                for (int py = py0; py <= py1; py++) {
                    double oy = OverlapLen(y0, y1, py);
                    if (oy <= 0) continue;
                    int outRow = py * OutW;
                    for (int px = px0; px <= px1; px++) {
                        double ox = OverlapLen(x0, x1, px);
                        if (ox <= 0) continue;
                        double wgt = ox * oy;
                        int idx = outRow + px;
                        _data[idx] += (float)(v * wgt);
                        _weight[idx] += (float)wgt;
                    }
                }
            }
        }
    }

    // Overlap of the drop span [a,b] with output pixel [p, p+1].
    private static double OverlapLen(double a, double b, int p) {
        double lo = Math.Max(a, p);
        double hi = Math.Min(b, p + 1);
        double d = hi - lo;
        return d > 0 ? d : 0;
    }

    /// <summary>
    /// Reconstruct the output plane: <c>data/weight</c> per pixel, clamped to
    /// ushort. Output pixels no drop reached (weight 0) are 0.
    /// </summary>
    public ushort[] Result() {
        var outp = new ushort[_data.Length];
        for (int i = 0; i < outp.Length; i++) {
            float w = _weight[i];
            if (w > 0) outp[i] = (ushort)Math.Clamp(_data[i] / w, 0, 65535);
        }
        return outp;
    }

    /// <summary>Fraction of output pixels that received no drop (coverage
    /// holes). High values (a few %+) indicate too few / poorly dithered subs
    /// for this scale — a hint to lower the scale or raise pixfrac.</summary>
    public double EmptyFraction() {
        long empty = 0;
        for (int i = 0; i < _weight.Length; i++) if (_weight[i] <= 0) empty++;
        return (double)empty / _weight.Length;
    }
}
