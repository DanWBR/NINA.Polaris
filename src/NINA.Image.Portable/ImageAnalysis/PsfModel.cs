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
/// An empirically measured point-spread function: a normalized, odd-sized
/// square kernel (sum = 1) plus second-moment shape descriptors. Produced by
/// <see cref="PsfExtractor"/> from the stars in an image and consumed by the
/// (classical, measured-PSF) Richardson-Lucy deconvolution.
///
/// Unlike the AI deconvolution — which only ever sees a single global FWHM
/// guess — this kernel is the *actual* PSF of the optical train + seeing for
/// that frame, which makes the deconvolution mathematically well-posed
/// (Richardson 1972, Lucy 1974).
/// </summary>
public class PsfModel {
    /// <summary>Odd kernel side length in pixels.</summary>
    public int Size { get; }

    /// <summary>Row-major kernel of length <see cref="Size"/>², normalized to
    /// sum to 1 so convolution conserves total flux.</summary>
    public float[] Kernel { get; }

    public int Radius => Size / 2;

    /// <summary>Equivalent FWHM in pixels (2.3548·σ from the second-moment σ).</summary>
    public double FwhmPx { get; set; }

    /// <summary>σ of the major axis (px), from the second-moment ellipse.</summary>
    public double SigmaMajorPx { get; set; }

    /// <summary>σ of the minor axis (px).</summary>
    public double SigmaMinorPx { get; set; }

    /// <summary>0 = round, →1 = elongated: sqrt(1 − (σ_minor/σ_major)²).</summary>
    public double Eccentricity { get; set; }

    /// <summary>Major-axis angle in radians (image coords).</summary>
    public double OrientationRad { get; set; }

    /// <summary>How many stars were combined to build this PSF.</summary>
    public int StarsUsed { get; set; }

    public PsfModel(int size, float[] kernel) {
        if (size <= 0 || (size & 1) == 0)
            throw new ArgumentException("PSF size must be a positive odd number", nameof(size));
        if (kernel == null || kernel.Length != size * size)
            throw new ArgumentException("kernel length must equal size*size", nameof(kernel));
        Size = size;
        Kernel = kernel;
    }

    /// <summary>Analytic round-Gaussian PSF — used as a fallback when too few
    /// stars are available, and as ground truth in tests. σ in pixels.</summary>
    public static PsfModel Gaussian(int size, double sigma) {
        if (size <= 0 || (size & 1) == 0)
            throw new ArgumentException("size must be a positive odd number", nameof(size));
        int r = size / 2;
        var k = new float[size * size];
        double s2 = 2.0 * sigma * sigma, sum = 0;
        for (int y = -r; y <= r; y++) {
            for (int x = -r; x <= r; x++) {
                double v = Math.Exp(-(x * x + y * y) / s2);
                k[(y + r) * size + (x + r)] = (float)v;
                sum += v;
            }
        }
        if (sum > 0) for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);
        return new PsfModel(size, k) {
            FwhmPx = 2.3548200450309493 * sigma,   // 2√(2ln2)·σ
            SigmaMajorPx = sigma,
            SigmaMinorPx = sigma,
            Eccentricity = 0,
            OrientationRad = 0,
        };
    }
}
