// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Field-varying Richardson-Lucy deconvolution: each grid cell of a
/// <see cref="PsfField"/> is deconvolved with its OWN measured PSF, then the
/// cells are stitched with a cosine feather (overlapping weighted accumulation)
/// so there is no seam where the kernel changes. This corrects corners (coma,
/// field curvature, tilt) with their local shape instead of a single global
/// FWHM — the headline differentiator over single-PSF tools.
///
/// A global flux-conservation rescale and an optional support mask are applied
/// once at the end (the per-tile RL runs without them so the feather can't
/// fight per-tile normalization).
/// </summary>
public class FieldDeconvolution {
    public int Iterations { get; set; } = 15;
    public double TvLambda { get; set; } = 0.002;
    public bool ConserveFlux { get; set; } = true;

    /// <summary>Damped-RL threshold (σ units) forwarded to each tile; see
    /// <see cref="RichardsonLucyDeconvolution.DampingThreshold"/>.</summary>
    public double DampingThreshold { get; set; } = 0;

    /// <summary>Forward FFT convolution to each tile's RL (kernel-size
    /// independent cost); see <see cref="RichardsonLucyDeconvolution.UseFft"/>.</summary>
    public bool UseFft { get; set; } = false;

    public float[] Deconvolve(float[] image, int width, int height, PsfField field,
                              float[] supportMask = null, float[] noiseSigma = null) {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (field == null) throw new ArgumentNullException(nameof(field));
        if (image.Length != (long)width * height)
            throw new ArgumentException("image length != width*height", nameof(image));
        if (noiseSigma != null && noiseSigma.Length != image.Length)
            throw new ArgumentException("sigma length != image length", nameof(noiseSigma));
        if (Iterations <= 0) return (float[])image.Clone();

        int n = image.Length;
        var acc = new float[n];
        var wsum = new float[n];

        var rl = new RichardsonLucyDeconvolution {
            Iterations = Iterations, TvLambda = TvLambda, ConserveFlux = false,
            DampingThreshold = DampingThreshold, UseFft = UseFft
        };

        double cellW = (double)width / field.GridX;
        double cellH = (double)height / field.GridY;

        for (int gy = 0; gy < field.GridY; gy++) {
            for (int gx = 0; gx < field.GridX; gx++) {
                var psf = field.Cell(gx, gy);
                int kr = psf.Radius;

                // Core cell bounds (output region this tile is responsible for).
                int cx0 = (int)Math.Round(gx * cellW), cx1 = (int)Math.Round((gx + 1) * cellW);
                int cy0 = (int)Math.Round(gy * cellH), cy1 = (int)Math.Round((gy + 1) * cellH);

                // Feather width: a quarter of the cell, capped, but no feather on
                // a side that touches the frame border (nothing to blend into).
                int fx = (int)Math.Min(cellW, cellH) / 4; fx = Math.Max(4, fx);
                int halo = kr + fx;

                int tx0 = Math.Max(0, cx0 - halo), tx1 = Math.Min(width, cx1 + halo);
                int ty0 = Math.Max(0, cy0 - halo), ty1 = Math.Min(height, cy1 + halo);
                int tw = tx1 - tx0, th = ty1 - ty0;
                if (tw <= 0 || th <= 0) continue;

                // Extract tile, deconvolve with this cell's PSF.
                var tile = new float[tw * th];
                float[] sigTile = noiseSigma != null ? new float[tw * th] : null;
                for (int y = 0; y < th; y++) {
                    Array.Copy(image, (long)(ty0 + y) * width + tx0, tile, (long)y * tw, tw);
                    if (sigTile != null)
                        Array.Copy(noiseSigma, (long)(ty0 + y) * width + tx0, sigTile, (long)y * tw, tw);
                }
                var dec = rl.Deconvolve(tile, tw, th, psf, null, sigTile);

                bool featherL = cx0 > 0, featherR = cx1 < width;
                bool featherT = cy0 > 0, featherB = cy1 < height;

                for (int y = 0; y < th; y++) {
                    int Y = ty0 + y;
                    double wy = AxisWeight(Y, cy0, cy1, fx, featherT, featherB);
                    if (wy <= 0) continue;
                    for (int x = 0; x < tw; x++) {
                        int X = tx0 + x;
                        double wx = AxisWeight(X, cx0, cx1, fx, featherL, featherR);
                        double w = wx * wy;
                        if (w <= 0) continue;
                        long gi = (long)Y * width + X;
                        acc[gi] += (float)(w * dec[y * tw + x]);
                        wsum[gi] += (float)w;
                    }
                }
            }
        }

        // Composite: acc / wsum (every pixel is covered by ≥1 cell core).
        var est = new float[n];
        for (int i = 0; i < n; i++) est[i] = wsum[i] > 0 ? acc[i] / wsum[i] : image[i];

        if (ConserveFlux) {
            double si = 0, se = 0;
            for (int i = 0; i < n; i++) { si += image[i] > 0 ? image[i] : 0; se += est[i]; }
            if (se > 0) { float k = (float)(si / se); for (int i = 0; i < n; i++) est[i] *= k; }
        }

        if (supportMask != null) {
            if (supportMask.Length != n)
                throw new ArgumentException("mask length != image length", nameof(supportMask));
            for (int i = 0; i < n; i++) est[i] = image[i] + supportMask[i] * (est[i] - image[i]);
        }
        return est;
    }

    // Cosine-feather weight along one axis: 1 inside [c0,c1), smoothstep ramp
    // out over `f` px into the halo on sides that have a neighbour, 0 beyond.
    private static double AxisWeight(int p, int c0, int c1, int f, bool featherLow, bool featherHigh) {
        if (p >= c0 && p < c1) return 1.0;
        if (p < c0) {
            if (!featherLow) return 1.0;          // border side: hold full weight
            double t = (p - (c0 - f)) / (double)f; // 0 at halo edge → 1 at core
            return Smooth(t);
        }
        if (!featherHigh) return 1.0;
        double u = ((c1 + f) - 1 - p) / (double)f;
        return Smooth(u);
    }

    private static double Smooth(double t) {
        if (t <= 0) return 0; if (t >= 1) return 1;
        return t * t * (3 - 2 * t);
    }
}
