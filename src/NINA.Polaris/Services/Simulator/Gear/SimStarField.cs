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

// Ported to C# from PHD2 (OpenPHDGuiding) src/gear_simulator.cpp.
//
// PHD2 is Copyright (c) Craig Stark, Bret McKee, Dad Dog Development Ltd.
// Licensed under the BSD 3-Clause License. See licenses/PHD2-LICENSE.txt.
//
// Synthetic star field: deterministic star list (SimCamState::Initialize) and
// per-frame renderer (SimCamState::FillImage + render_star). Stars are placed
// in RA/Dec-aligned coordinates, shifted by the error model, rotated into
// camera coordinates, then splatted with PHD2's 5x5 PSF kernel.

namespace NINA.Polaris.Services.Simulator.Gear;

/// <summary>Generates and renders the simulated star field.</summary>
public sealed class SimStarField {
    private readonly SimGearParams _p;

    private readonly record struct SimStar(double X, double Y, double Inten);
    private readonly SimStar[] _stars;
    private readonly (int x, int y)[] _hot;

    // PHD2's pre-rendered 5x5 PSF (an Airy-disk approximation, not a true Gaussian).
    private static readonly double[,] Psf = {
        { 0.0,  0.8,   2.2,  0.8, 0.0 },
        { 0.8, 16.6,  46.1, 16.6, 0.8 },
        { 2.2, 46.1, 128.0, 46.1, 2.2 },
        { 0.8, 16.6,  46.1, 16.6, 0.8 },
        { 0.0,  0.8,   2.2,  0.8, 0.0 },
    };
    private const int PsfWidth = 5;

    public SimStarField(SimGearParams p) {
        _p = p;
        int w = p.Width, h = p.Height, border = p.Border;

        // Deterministic generation: PHD2 seeds rand() with 2 so the same stars
        // appear every run. We mirror that with a fixed-seed RNG.
        var rng = new Random(2);
        int n = Math.Max(1, p.Stars);
        _stars = new SimStar[n];
        for (int i = 0; i < n; i++) {
            double x = rng.Next(w - 2 * border) - 0.5 * w;
            double y = rng.Next(h - 2 * border) - 0.5 * h;
            double r = rng.Next(90) / 3.0; // 0..30
            double inten = i == 10 ? 30.1 : 0.1 + r * r * r / 9000.0;
            // Force a close pair (PHD2's AutoFind test).
            if (i == 3 && i > 0) {
                x = _stars[i - 1].X + 8;
                y = _stars[i - 1].Y + 8;
                inten = _stars[i - 1].Inten;
            }
            _stars[i] = new SimStar(x, y, inten);
        }

        int nh = Math.Max(0, p.HotPixels);
        _hot = new (int, int)[nh];
        for (int i = 0; i < nh; i++)
            _hot[i] = (rng.Next(w), rng.Next(h));
    }

    /// <summary>Number of stars in the field (for tests).</summary>
    public int StarCount => _stars.Length;

    /// <summary>
    /// Render one frame into <paramref name="buf"/> (length outW*outH), applying
    /// the total error shift (<paramref name="shiftX"/>, <paramref name="shiftY"/>
    /// in full-res pixels) and camera rotation. Output dimensions are the sensor
    /// size divided by <paramref name="binning"/>.
    /// </summary>
    public void FillImage(ushort[] buf, int outW, int outH, int binning,
                          double shiftX, double shiftY, bool pierWest,
                          double exptimeSec, double gain, Random rng) {
        Array.Clear(buf, 0, buf.Length);

        double angle = _p.CameraAngleDeg * Math.PI / 180.0;
        if (pierWest) angle += Math.PI;
        double cosT = Math.Cos(angle), sinT = Math.Sin(angle);

        double halfW = _p.Width / 2.0, halfH = _p.Height / 2.0;

        // Exposure/gain envelope for star brightness. Faithful in spirit to
        // PHD2 (brightness scales with intensity * gain); absolute scaling is
        // tuned for a 16-bit frame and does not affect centroiding.
        double expFactor = Math.Clamp(exptimeSec <= 0 ? 1.0 : exptimeSec, 0.05, 8.0);

        foreach (var s in _stars) {
            double px = s.X + shiftX;
            double py = s.Y + shiftY;
            double cx = px * cosT - py * sinT + halfW;
            double cy = px * sinT + py * cosT + halfH;

            // Star brightness only; the background pedestal is added globally
            // below so it isn't double-counted under the PSF.
            double noise = (rng.NextDouble() - 0.5) * 2.0 * _p.NoiseSigma * _p.NoiseMultiplier;
            double inten = s.Inten * gain * expFactor + noise;
            if (inten < 0) inten = 0;

            RenderStar(buf, outW, outH, binning, cx, cy, inten);
        }

        // Per-pixel background noise everywhere (light pedestal so the field
        // isn't pure black between stars).
        double bg = _p.Background;
        for (int i = 0; i < buf.Length; i++) {
            double v = buf[i] + bg + (rng.NextDouble() - 0.5) * 2.0 * _p.NoiseSigma;
            buf[i] = (ushort)Math.Clamp(v, 0, 65535);
        }

        // Hot pixels (saturated).
        foreach (var (hx, hy) in _hot) {
            int x = hx / binning, y = hy / binning;
            if (x >= 0 && x < outW && y >= 0 && y < outH)
                buf[y * outW + x] = 65535;
        }
    }

    // Port of render_star: bilinear sub-pixel splat of the 5x5 PSF.
    private static void RenderStar(ushort[] buf, int outW, int outH, int binning,
                                   double pxFull, double pyFull, double inten) {
        double bx = pxFull / binning;
        double by = pyFull / binning;
        double ix = Math.Floor(bx), iy = Math.Floor(by);
        double fx = bx - ix, fy = by - iy;
        double f00 = (1.0 - fx) * (1.0 - fy);
        double f01 = (1.0 - fx) * fy;
        double f10 = fx * (1.0 - fy);
        double f11 = fx * fy;

        var d = new double[PsfWidth + 1, PsfWidth + 1];
        for (int i = 0; i < PsfWidth; i++) {
            for (int j = 0; j < PsfWidth; j++) {
                double sval = Psf[i, j];
                if (sval <= 0.0) continue;
                sval *= inten / 256.0;
                d[i, j] += f00 * sval;
                d[i + 1, j] += f10 * sval;
                d[i, j + 1] += f01 * sval;
                d[i + 1, j + 1] += f11 * sval;
            }
        }

        int cx = (int)ix - (PsfWidth - 1) / 2;
        int cy = (int)iy - (PsfWidth - 1) / 2;
        for (int i = 0; i < PsfWidth + 1; i++) {
            int x = cx + i;
            if (x < 0 || x >= outW) continue;
            for (int j = 0; j < PsfWidth + 1; j++) {
                int y = cy + j;
                if (y < 0 || y >= outH) continue;
                int idx = y * outW + x;
                double v = buf[idx] + d[i, j];
                buf[idx] = (ushort)Math.Clamp(v, 0, 65535);
            }
        }
    }
}