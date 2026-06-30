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
using System.Collections.Generic;
using System.Linq;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Measures the empirical point-spread function of a frame by stacking its own
/// stars. The result drives measured-PSF deconvolution, which is mathematically
/// better posed than deconvolving with a single guessed FWHM.
///
/// Pipeline (all classical, traceable):
///   1. detect stars (<see cref="StarDetector"/>);
///   2. keep only clean PSF probes — high SNR, NOT saturated, round, isolated,
///      away from the border;
///   3. cut an odd stamp around each, subtract its local background, recenter
///      to sub-pixel accuracy (flux-weighted centroid + bilinear resample) and
///      normalize;
///   4. combine with a per-pixel sigma-clipped mean (robust to cosmic rays /
///      faint neighbours);
///   5. describe the shape from flux-weighted second moments (FWHM, ellipse).
/// </summary>
public class PsfExtractor {
    /// <summary>Upper bound on stars combined (brightest-first).</summary>
    public int MaxStars { get; set; } = 120;

    /// <summary>Minimum peak-over-noise for a star to be a PSF probe.</summary>
    public double MinSnr { get; set; } = 20.0;

    /// <summary>Reject stars whose peak exceeds this fraction of full scale
    /// (saturated/non-linear cores would corrupt the PSF).</summary>
    public double SaturationFraction { get; set; } = 0.9;

    /// <summary>Full-scale value for the saturation test (16-bit default).</summary>
    public double FullScale { get; set; } = 65535.0;

    /// <summary>Reject elongated stars (trailing/coma) above this eccentricity.</summary>
    public double MaxEccentricity { get; set; } = 0.5;

    /// <summary>Stamp radius = ceil(this × median HFR), clamped to
    /// [<see cref="MinStampRadius"/>, <see cref="MaxStampRadius"/>].</summary>
    public double StampHfrMultiplier { get; set; } = 4.0;

    public int MinStampRadius { get; set; } = 8;
    public int MaxStampRadius { get; set; } = 40;

    /// <summary>A star is "isolated" if no other detected star lies within
    /// this multiple of the stamp radius.</summary>
    public double IsolationMultiplier { get; set; } = 1.6;

    /// <summary>Sigma-clip threshold for the per-pixel robust mean combine.</summary>
    public double CombineSigma { get; set; } = 3.0;

    /// <summary>Minimum probes required; below this returns null (caller should
    /// fall back to an analytic PSF).</summary>
    public int MinStars { get; set; } = 8;

    private readonly StarDetector _detector;

    public PsfExtractor(StarDetector detector = null) {
        _detector = detector ?? new StarDetector();
    }

    /// <summary>
    /// Extract the PSF from <paramref name="data"/>. Pass <paramref name="stars"/>
    /// to reuse a detection you already ran; otherwise stars are detected here.
    /// Returns null when fewer than <see cref="MinStars"/> clean probes exist.
    /// </summary>
    public PsfModel Extract(ushort[] data, int width, int height, IList<DetectedStar> stars = null) {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length != (long)width * height)
            throw new ArgumentException("data length != width*height", nameof(data));

        stars ??= _detector.Detect(data, width, height);
        if (stars == null || stars.Count == 0) return null;

        // Background + noise from a robust global estimate (median + MAD→σ).
        (double bg, double noise) = EstimateBackgroundNoise(data);
        if (noise <= 0) noise = 1;

        // Stamp size from the typical star size in THIS frame.
        double medHfr = Median(stars.Select(s => s.HFR).Where(h => h > 0).ToArray());
        if (double.IsNaN(medHfr) || medHfr <= 0) medHfr = 2.0;
        int r = (int)Math.Ceiling(StampHfrMultiplier * medHfr);
        r = Math.Max(MinStampRadius, Math.Min(MaxStampRadius, r));
        int size = 2 * r + 1;
        double satLevel = SaturationFraction * FullScale;
        double isoDist = IsolationMultiplier * r;

        // Candidate filter: SNR, saturation, roundness, border, isolation.
        var candidates = new List<DetectedStar>();
        foreach (var s in stars) {
            if (s.X < r + 1 || s.Y < r + 1 || s.X >= width - r - 1 || s.Y >= height - r - 1) continue;
            if (s.Peak >= satLevel) continue;
            if ((s.Peak - bg) / noise < MinSnr) continue;
            if (s.Eccentricity > MaxEccentricity) continue;
            candidates.Add(s);
        }
        // Isolation: drop probes with a neighbour inside isoDist (any detected star).
        var probes = candidates
            .Where(c => !stars.Any(o => !ReferenceEquals(o, c) && c.DistanceTo(o) < isoDist))
            .OrderByDescending(c => c.Peak)
            .Take(MaxStars)
            .ToList();

        if (probes.Count < MinStars) return null;

        // Build aligned, background-subtracted, normalized stamps.
        var stamps = new List<float[]>(probes.Count);
        foreach (var p in probes) {
            var stamp = BuildAlignedStamp(data, width, height, p, r, size);
            if (stamp != null) stamps.Add(stamp);
        }
        if (stamps.Count < MinStars) return null;

        // Per-pixel sigma-clipped mean -> empirical PSF.
        var kernel = SigmaClippedMean(stamps, size * size, CombineSigma);

        // Clamp negatives (background over-subtraction wings) and renormalize.
        double sum = 0;
        for (int i = 0; i < kernel.Length; i++) { if (kernel[i] < 0) kernel[i] = 0; sum += kernel[i]; }
        if (sum <= 0) return null;
        for (int i = 0; i < kernel.Length; i++) kernel[i] = (float)(kernel[i] / sum);

        var model = new PsfModel(size, kernel) { StarsUsed = stamps.Count };
        ComputeShape(model);
        return model;
    }

    // ── stamp extraction + sub-pixel recenter ───────────────────────────────
    // Cuts a stamp around the star, subtracts the local background (median of
    // the stamp border ring), finds the flux-weighted centroid and bilinearly
    // resamples so the centroid lands on the stamp centre, then normalizes.
    private static float[] BuildAlignedStamp(ushort[] data, int width, int height,
                                             DetectedStar star, int r, int size) {
        int sx = (int)Math.Round(star.X), sy = (int)Math.Round(star.Y);
        var raw = new float[size * size];
        for (int y = 0; y < size; y++) {
            int iy = sy - r + y;
            for (int x = 0; x < size; x++) {
                int ix = sx - r + x;
                raw[y * size + x] = data[iy * width + ix];
            }
        }
        // Local background = median of the outer ring (1px border).
        var ring = new List<float>(4 * size);
        for (int x = 0; x < size; x++) { ring.Add(raw[x]); ring.Add(raw[(size - 1) * size + x]); }
        for (int y = 1; y < size - 1; y++) { ring.Add(raw[y * size]); ring.Add(raw[y * size + size - 1]); }
        float bg = MedianInPlace(ring.ToArray());
        for (int i = 0; i < raw.Length; i++) { raw[i] -= bg; if (raw[i] < 0) raw[i] = 0; }

        // Flux-weighted centroid within the stamp.
        double sumv = 0, cx = 0, cy = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                float v = raw[y * size + x];
                sumv += v; cx += v * x; cy += v * y;
            }
        if (sumv <= 0) return null;
        cx /= sumv; cy /= sumv;

        // Shift so the centroid moves to the centre (r,r): out(x,y)=in(x-dx,y-dy).
        double dx = r - cx, dy = r - cy;
        var outp = new float[size * size];
        double outSum = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                double v = BilinearSample(raw, size, size, x - dx, y - dy);
                outp[y * size + x] = (float)v; outSum += v;
            }
        if (outSum <= 0) return null;
        for (int i = 0; i < outp.Length; i++) outp[i] = (float)(outp[i] / outSum);  // ∑ = 1
        return outp;
    }

    private static double BilinearSample(float[] img, int w, int h, double x, double y) {
        if (x < 0 || y < 0 || x > w - 1 || y > h - 1) return 0;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double fx = x - x0, fy = y - y0;
        double a = img[y0 * w + x0], b = img[y0 * w + x1];
        double c = img[y1 * w + x0], d = img[y1 * w + x1];
        return a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy;
    }

    // Per-pixel sigma-clipped mean across all stamps (robust to outliers).
    private static float[] SigmaClippedMean(List<float[]> stamps, int n, double kSigma) {
        var outp = new float[n];
        int m = stamps.Count;
        var col = new double[m];
        for (int i = 0; i < n; i++) {
            for (int j = 0; j < m; j++) col[j] = stamps[j][i];
            double mean = col.Average();
            double sd = Math.Sqrt(col.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, m));
            double s = 0; int c = 0;
            for (int j = 0; j < m; j++)
                if (sd <= 0 || Math.Abs(col[j] - mean) <= kSigma * sd) { s += col[j]; c++; }
            outp[i] = (float)(c > 0 ? s / c : mean);
        }
        return outp;
    }

    // Flux-weighted second moments -> equivalent FWHM + ellipse descriptors.
    private static void ComputeShape(PsfModel m) {
        int size = m.Size; var k = m.Kernel;
        double sum = 0, cx = 0, cy = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                double v = k[y * size + x]; sum += v; cx += v * x; cy += v * y;
            }
        if (sum <= 0) return;
        cx /= sum; cy /= sum;
        double mxx = 0, myy = 0, mxy = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                double v = k[y * size + x]; double ddx = x - cx, ddy = y - cy;
                mxx += v * ddx * ddx; myy += v * ddy * ddy; mxy += v * ddx * ddy;
            }
        mxx /= sum; myy /= sum; mxy /= sum;
        double half = (mxx + myy) / 2.0;
        double disc = Math.Sqrt(Math.Max(0, ((mxx - myy) / 2.0) * ((mxx - myy) / 2.0) + mxy * mxy));
        double lMax = half + disc, lMin = Math.Max(0, half - disc);
        m.SigmaMajorPx = Math.Sqrt(lMax);
        m.SigmaMinorPx = Math.Sqrt(lMin);
        m.Eccentricity = lMax > 0 ? Math.Sqrt(Math.Max(0, 1 - lMin / lMax)) : 0;
        m.OrientationRad = 0.5 * Math.Atan2(2 * mxy, mxx - myy);
        m.FwhmPx = 2.3548200450309493 * Math.Sqrt(Math.Max(0, half));   // 2√(2ln2)·σ
    }

    // ── small robust-stat helpers ───────────────────────────────────────────
    /// <summary>Robust background (median) and noise (1.4826·MAD) from a
    /// sampled subset. Public so callers (e.g. deconvolution support masks)
    /// reuse the exact same estimate the extractor used.</summary>
    public static (double bg, double noise) EstimateBackgroundNoise(ushort[] data) {
        // Sample (cap cost on huge frames) then median + MAD→σ (1.4826·MAD).
        int step = Math.Max(1, data.Length / 200_000);
        var sample = new List<float>(data.Length / step + 1);
        for (int i = 0; i < data.Length; i += step) sample.Add(data[i]);
        var arr = sample.ToArray();
        double med = MedianInPlace(arr);
        for (int i = 0; i < arr.Length; i++) arr[i] = (float)Math.Abs(arr[i] - med);
        double mad = MedianInPlace(arr);
        return (med, 1.4826 * mad);
    }

    private static double Median(double[] v) {
        if (v == null || v.Length == 0) return double.NaN;
        var a = (double[])v.Clone(); Array.Sort(a);
        int n = a.Length; return n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
    }

    private static float MedianInPlace(float[] a) {
        if (a == null || a.Length == 0) return 0;
        Array.Sort(a);
        int n = a.Length; return n % 2 == 1 ? a[n / 2] : 0.5f * (a[n / 2 - 1] + a[n / 2]);
    }
}
