// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

// Ported to C# from PHD2 (OpenPHDGuiding) src/star.cpp.
//
// PHD2 is Copyright (c) Craig Stark, Bret McKee, Dad Dog Development Ltd.
// Licensed under the BSD 3-Clause License. See licenses/PHD2-LICENSE.txt.
//
// This file reimplements Star::Find (single-star weighted centroid + SNR +
// HFD) for the native Polaris autoguider. Algorithm and constants follow the
// original; the surrounding wxWidgets/usImage plumbing is replaced by a plain
// ushort[] frame.

namespace NINA.Guider.Portable;

/// <summary>Single-star sub-pixel centroid tracker (port of PHD2 Star::Find).</summary>
public static class GuideStar {
    private readonly record struct R2M(int X, int Y, double M) {
        public double R2 { get; init; }
    }

    /// <summary>
    /// Locate and centroid the guide star near (baseX, baseY) within +/- searchRegion.
    /// </summary>
    /// <param name="img">16-bit frame, row-major, length = width*height.</param>
    /// <param name="gain">Electrons-per-ADU for the SNR term; ~0.5 nominal,
    /// or 1.0 when unknown (SNR stays monotonic for star-lost detection).</param>
    public static GuideStarResult Find(ushort[] img, int width, int height,
                                       double baseX, double baseY, int searchRegion = 15,
                                       double minHfd = 1.5, double maxHfd = 25.0, double gain = 0.5) {
        if (img == null || img.Length < (long)width * height) return GuideStarResult.Failed(GuideStarStatus.Error);

        int bx = (int)Math.Round(baseX);
        int by = (int)Math.Round(baseY);

        int minx = 0, miny = 0, maxx = width - 1, maxy = height - 1;
        int startX = Math.Max(bx - searchRegion, minx);
        int endX = Math.Min(bx + searchRegion, maxx);
        int startY = Math.Max(by - searchRegion, miny);
        int endY = Math.Min(by + searchRegion, maxy);
        if (endX <= startX || endY <= startY) return GuideStarResult.Failed(GuideStarStatus.Error);

        // --- find smoothed peak + raw top-3 (saturation) within search region ---
        int peakX = 0, peakY = 0;
        uint peakVal = 0;
        ushort m0 = 0, m1 = 0, m2 = 0; // top three raw values
        for (int y = startY + 1; y <= endY - 1; y++) {
            for (int x = startX + 1; x <= endX - 1; x++) {
                ushort p = img[y * width + x];
                uint val = 4u * p
                    + img[(y - 1) * width + (x - 1)] + img[(y - 1) * width + (x + 1)]
                    + img[(y + 1) * width + (x - 1)] + img[(y + 1) * width + (x + 1)]
                    + 2u * img[(y - 1) * width + x] + 2u * img[y * width + (x - 1)]
                    + 2u * img[y * width + (x + 1)] + 2u * img[(y + 1) * width + x];
                if (val > peakVal) { peakVal = val; peakX = x; peakY = y; }
                ushort q = p;
                if (q > m0) (q, m0) = (m0, q);
                if (q > m1) (q, m1) = (m1, q);
                if (q > m2) (q, m2) = (m2, q);
            }
        }
        ushort rawPeak = m0;
        peakVal /= 16; // smoothed peak

        // --- background mean/sigma in annulus [A,B] around peak, iterative clip ---
        const int A = 7, B = 12, A2 = A * A, B2 = B * B;
        startX = Math.Max(peakX - B, minx); endX = Math.Min(peakX + B, maxx);
        startY = Math.Max(peakY - B, miny); endY = Math.Min(peakY + B, maxy);

        uint nbg = 0;
        double meanBg = 0, prevMeanBg, sigma2Bg = 0, sigmaBg = 0;
        for (int iter = 0; iter < 9; iter++) {
            double sum = 0, a = 0, q = 0;
            nbg = 0;
            for (int y = startY; y <= endY; y++) {
                int dy = y - peakY, dy2 = dy * dy;
                int rowoff = y * width;
                for (int x = startX; x <= endX; x++) {
                    int dx = x - peakX, r2 = dx * dx + dy2;
                    if (r2 <= A2 || r2 > B2) continue;
                    double val = img[rowoff + x];
                    if (iter > 0 && (val < meanBg - 2.0 * sigmaBg || val > meanBg + 2.0 * sigmaBg)) continue;
                    sum += val; ++nbg;
                    double k = nbg, a0 = a;
                    a += (val - a) / k;
                    q += (val - a0) * (val - a);
                }
            }
            if (nbg < 10) break;
            prevMeanBg = meanBg;
            meanBg = sum / nbg;
            sigma2Bg = q / (nbg - 1);
            sigmaBg = Math.Sqrt(sigma2Bg);
            if (iter > 0 && Math.Abs(meanBg - prevMeanBg) < 0.5) break;
        }

        // --- weighted centroid over aperture r<=A, pixels above threshold ---
        ushort thresh = (ushort)(meanBg + 3.0 * sigmaBg + 0.5);
        double cx = 0, cy = 0, mass = 0;
        uint n = 0;
        var hfrvec = new List<R2M>();
        startX = Math.Max(peakX - A, minx); endX = Math.Min(peakX + A, maxx);
        startY = Math.Max(peakY - A, miny); endY = Math.Min(peakY + A, maxy);
        for (int y = startY; y <= endY; y++) {
            int dy = y - peakY, dy2 = dy * dy;
            if (dy2 > A2) continue;
            int rowoff = y * width;
            for (int x = startX; x <= endX; x++) {
                int dx = x - peakX;
                if (dx * dx + dy2 > A2) continue;
                ushort val = img[rowoff + x];
                if (val < thresh) continue;
                double d = val - meanBg;
                cx += dx * d; cy += dy * d; mass += d; ++n;
                hfrvec.Add(new R2M(x, y, d));
            }
        }

        double snr = n > 0 ? mass / Math.Sqrt(mass / gain + sigma2Bg * n * (1.0 + 1.0 / nbg)) : 0.0;
        const double lowSnr = 3.0;
        if (peakVal <= thresh && snr >= lowSnr) snr = lowSnr - 0.1; // false positive guard

        if (mass < 10.0) return new GuideStarResult(0, 0, mass, snr, 0, rawPeak, GuideStarStatus.LowMass);
        if (snr < lowSnr) return new GuideStarResult(0, 0, mass, snr, 0, rawPeak, GuideStarStatus.LowSnr);

        double newX = peakX + cx / mass;
        double newY = peakY + cy / mass;
        double hfd = 2.0 * Hfr(hfrvec, newX, newY, mass);

        var status = GuideStarStatus.Ok;
        if (hfd < minHfd) status = GuideStarStatus.LowHfd;
        else if (hfd > maxHfd) status = GuideStarStatus.HighHfd;
        else {
            // flat-top saturation heuristic (16-bit): top three within 32/65535 of max
            uint dd = (uint)(m0 - m2);
            if (dd * 65535U < 32U * m0) status = GuideStarStatus.Saturated;
        }

        return new GuideStarResult(newX, newY, mass, snr, hfd, rawPeak, status);
    }

    private static double Hfr(List<R2M> vec, double cx, double cy, double mass) {
        if (vec.Count == 1) return 0.25;
        for (int i = 0; i < vec.Count; i++) {
            double dx = vec[i].X - cx, dy = vec[i].Y - cy;
            vec[i] = vec[i] with { R2 = dx * dx + dy * dy };
        }
        vec.Sort((p, q) => p.R2.CompareTo(q.R2));
        double r20 = 0, r21 = 0, mAcc0 = 0, mAcc1 = 0;
        double halfm = 0.5 * mass;
        foreach (var rm in vec) {
            r20 = r21; mAcc0 = mAcc1;
            r21 = rm.R2; mAcc1 += rm.M;
            if (mAcc1 > halfm) break;
        }
        if (mAcc1 > mAcc0) {
            double r0 = Math.Sqrt(r20), r1 = Math.Sqrt(r21);
            double s = (r1 - r0) / (mAcc1 - mAcc0);
            return r0 + s * (halfm - mAcc0);
        }
        return 0.25;
    }
}