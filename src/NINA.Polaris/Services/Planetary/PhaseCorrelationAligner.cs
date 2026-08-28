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

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Translational alignment by phase correlation, for planetary/lunar targets
/// that FILL the frame — a close-up of the Moon or Sun where the whole frame is
/// surface, with no dark sky and no limb. The centroid aligner is useless there:
/// with ~all pixels above its threshold the intensity-weighted centroid sits at
/// the frame centre on every frame and never moves, so the stack gets no
/// alignment and comes out soft/doubled. Measured on a real 1920² lunar SER:
/// 99% of pixels above threshold, centroid pinned within ~2 px across 173 frames.
///
/// Phase correlation registers two images by the shift of their cross-power
/// spectrum's inverse transform (Kuglin &amp; Hines, "The phase correlation image
/// alignment method", IEEE Conf. on Cybernetics and Society, 1975): for
/// f₂(x)=f₁(x−d), R(k)=F₁(k)·conj(F₂(k))/|…| = e^{i2πk·d/N}, whose inverse
/// transform is a delta at the shift. It keys on surface detail (craters, the
/// terminator), which is exactly what a filled lunar frame has and a centroid
/// cannot use.
///
/// Integer-pixel precision, matching the stacker's nearest-neighbour shift
/// (sub-pixel resampling is deferred there too). The reference FFT is computed
/// once; each frame costs one forward + one inverse 2-D FFT over a central
/// power-of-two ROI (capped at 512 to bound cost on an SBC — a global
/// translation is the same everywhere, so a representative central crop suffices).
/// </summary>
public sealed class PhaseCorrelationAligner {
    private readonly int _w;
    private readonly int _n;            // ROI side (power of two)
    private readonly int _ox, _oy;      // ROI top-left in the full frame
    private readonly double[] _win1d;   // separable Hann window
    private readonly double[] _refRe, _refIm;   // FFT of the windowed reference
    private readonly double[] _curRe, _curIm;   // reused per-frame work buffers

    public int RoiSize => _n;

    public PhaseCorrelationAligner(ushort[] reference, int width, int height) {
        if (reference == null || reference.Length != width * height)
            throw new ArgumentException("reference size mismatch");
        _w = width;
        int m = Math.Min(width, height);
        int n = 512;
        while (n > m) n >>= 1;           // largest power of two ≤ min(w,h), capped 512
        if (n < 8) n = 8;
        _n = n;
        _ox = (width - _n) / 2;
        _oy = (height - _n) / 2;
        _win1d = Hann(_n);
        _refRe = new double[_n * _n]; _refIm = new double[_n * _n];
        _curRe = new double[_n * _n]; _curIm = new double[_n * _n];
        ExtractInto(reference, _refRe, _refIm);
        Fft2(_refRe, _refIm, inverse: false);
    }

    /// <summary>Integer shift (dx, dy) to apply to <paramref name="frame"/> so it
    /// aligns to the reference, in the same convention as the centroid path
    /// (destination = source + (dx, dy); the stacker samples at x−dx).</summary>
    public (int dx, int dy) Align(ushort[] frame) {
        var r = AlignWithConfidence(frame);
        return (r.dx, r.dy);
    }

    /// <summary>As <see cref="Align"/>, plus a peak-to-sidelobe ratio (PSR) that
    /// says how trustworthy the shift is: a sharp, isolated correlation peak
    /// (well-textured frames related by a pure shift) gives a high PSR; a diffuse
    /// surface (low contrast / low SNR, e.g. a Moon near eclipse totality where
    /// there's little detail to lock onto) gives a low one. Callers use it to
    /// coast through frames whose shift can't be measured instead of jumping.</summary>
    public (int dx, int dy, double confidence) AlignWithConfidence(ushort[] frame) {
        if (frame == null || frame.Length < (_oy + _n) * _w) return (0, 0, 0);
        ExtractInto(frame, _curRe, _curIm);
        Fft2(_curRe, _curIm, inverse: false);
        // Cross-power spectrum R = F_ref · conj(F_cur) / |F_ref · conj(F_cur)|.
        int len = _n * _n;
        for (int i = 0; i < len; i++) {
            double ar = _refRe[i], ai = _refIm[i];
            double br = _curRe[i], bi = _curIm[i];
            double cr = ar * br + ai * bi;   // real of a·conj(b)
            double ci = ai * br - ar * bi;   // imag of a·conj(b)
            double mag = Math.Sqrt(cr * cr + ci * ci) + 1e-12;
            _curRe[i] = cr / mag; _curIm[i] = ci / mag;
        }
        Fft2(_curRe, _curIm, inverse: true);   // unnormalised: only the peak location matters
        int peak = 0; double best = double.NegativeInfinity;
        for (int i = 0; i < len; i++) { double v = _curRe[i]; if (v > best) { best = v; peak = i; } }
        int py = peak / _n, px = peak % _n;

        // PSR = (peak - mean) / std over the surface, excluding a small window
        // around the peak. Scale-invariant, so it doesn't depend on frame energy.
        double sum = 0, sum2 = 0; long cnt = 0; const int excl = 5;
        for (int y = 0; y < _n; y++) {
            int dyw = Math.Abs(y - py); if (dyw > _n / 2) dyw = _n - dyw;
            for (int x = 0; x < _n; x++) {
                int dxw = Math.Abs(x - px); if (dxw > _n / 2) dxw = _n - dxw;
                if (dyw <= excl && dxw <= excl) continue;   // skip the peak lobe
                double v = _curRe[y * _n + x];
                sum += v; sum2 += v * v; cnt++;
            }
        }
        double mean = sum / Math.Max(1, cnt);
        double var = sum2 / Math.Max(1, cnt) - mean * mean;
        double std = Math.Sqrt(Math.Max(0, var));
        double psr = (best - mean) / (std + 1e-12);

        // Wrap the delta into [-n/2, n/2). The peak sits at the shift itself,
        // which in this convention is the stacker's dst = src + (dx, dy).
        int dx = px <= _n / 2 ? px : px - _n;
        int dy = py <= _n / 2 ? py : py - _n;
        return (dx, dy, psr);
    }

    // Copy the central ROI, remove the DC (subtract the ROI mean so the flat
    // lunar brightness doesn't produce a trivial peak at 0,0) and apply a Hann
    // window (kills the wrap-around edge that a hard crop would otherwise fake
    // as high-frequency content).
    private void ExtractInto(ushort[] frame, double[] re, double[] im) {
        int n = _n;
        double sum = 0;
        for (int y = 0; y < n; y++) {
            int fr = (_oy + y) * _w + _ox;
            for (int x = 0; x < n; x++) sum += frame[fr + x];
        }
        double mean = sum / ((double)n * n);
        for (int y = 0; y < n; y++) {
            int fr = (_oy + y) * _w + _ox;
            int wr = y * n;
            double wy = _win1d[y];
            for (int x = 0; x < n; x++) {
                re[wr + x] = (frame[fr + x] - mean) * wy * _win1d[x];
                im[wr + x] = 0;
            }
        }
    }

    private static double[] Hann(int n) {
        var w = new double[n];
        if (n == 1) { w[0] = 1; return w; }
        for (int i = 0; i < n; i++)
            w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
        return w;
    }

    // Row-column 2-D FFT over the n×n buffer (row-major), in place.
    private void Fft2(double[] re, double[] im, bool inverse) {
        int n = _n;
        for (int y = 0; y < n; y++) Fft1(re, im, y * n, 1, n, inverse);   // rows (contiguous)
        for (int x = 0; x < n; x++) Fft1(re, im, x, n, n, inverse);       // columns (stride n)
    }

    // Iterative radix-2 Cooley-Tukey on the sequence re/im[offset + i*stride],
    // i = 0..n-1. n must be a power of two. Not normalised (the inverse pass
    // omits the 1/n, which does not move the correlation peak).
    private static void Fft1(double[] re, double[] im, int offset, int stride, int n, bool inverse) {
        for (int i = 1, j = 0; i < n; i++) {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) {
                int ai = offset + i * stride, aj = offset + j * stride;
                (re[ai], re[aj]) = (re[aj], re[ai]);
                (im[ai], im[aj]) = (im[aj], im[ai]);
            }
        }
        for (int len = 2; len <= n; len <<= 1) {
            double ang = 2.0 * Math.PI / len * (inverse ? 1 : -1);
            double wlenR = Math.Cos(ang), wlenI = Math.Sin(ang);
            for (int i = 0; i < n; i += len) {
                double wR = 1, wI = 0;
                int half = len >> 1;
                for (int k = 0; k < half; k++) {
                    int a = offset + (i + k) * stride;
                    int b = offset + (i + k + half) * stride;
                    double ur = re[a], ui = im[a];
                    double vr = re[b] * wR - im[b] * wI;
                    double vi = re[b] * wI + im[b] * wR;
                    re[a] = ur + vr; im[a] = ui + vi;
                    re[b] = ur - vr; im[b] = ui - vi;
                    double nwR = wR * wlenR - wI * wlenI;
                    wI = wR * wlenI + wI * wlenR; wR = nwR;
                }
            }
        }
    }
}
