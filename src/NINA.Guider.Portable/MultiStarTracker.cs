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

// Multi-star guiding. Concept ported from PHD2 (OpenPHDGuiding) Guider /
// MultiStar (guider.cpp, star.cpp), BSD-3-Clause. See licenses/PHD2-LICENSE.txt.
//
// Tracks a primary guide star plus a set of secondaries. Each frame every
// star is recentred with the single-star GuideStar.Find around its predicted
// position; the per-star displacements from their reference positions are
// combined into one robust field offset (median + outlier rejection +
// SNR-weighted mean). Averaging N stars lowers the centroid noise (~1/sqrt(N))
// and survives the loss of any single star, including the primary.

namespace NINA.Guider.Portable;

/// <summary>Combined field offset from all tracked stars this frame.</summary>
public readonly record struct MultiStarResult(
    bool Found, double OffsetX, double OffsetY,
    double Snr, double Hfd, int UsedCount, int TotalCount);

/// <summary>
/// Tracks several guide stars and reduces their per-star displacements to a
/// single robust field offset. The primary star (index 0) defines the lock;
/// its reference position is the guider lock position, so the returned offset
/// has the same meaning as the single-star <c>cur - lock</c> vector and can be
/// fed to the existing mount-coordinate transform unchanged.
/// </summary>
public sealed class MultiStarTracker {
    /// <summary>One tracked star and its bookkeeping.</summary>
    public sealed class TrackedStar {
        public double RefX, RefY;   // reference (lock) position in full-frame px
        public double CurX, CurY;   // last found centroid
        public double Snr;
        public bool Found;
        public int MissCount;
        public bool IsPrimary;
    }

    private readonly List<TrackedStar> _stars = new();
    private readonly int _searchRegion;
    private readonly int _maxMiss;
    private readonly double _outlierPx;
    private double _lastDx, _lastDy;

    /// <param name="searchRegion">Half-window (px) for each per-star centroid search.</param>
    /// <param name="maxMiss">Drop a non-primary star after this many consecutive misses.</param>
    /// <param name="outlierPx">Reject a star whose offset deviates more than this
    /// from the median offset before averaging.</param>
    public MultiStarTracker(int searchRegion = 15, int maxMiss = 10, double outlierPx = 4.0) {
        _searchRegion = Math.Max(5, searchRegion);
        _maxMiss = Math.Max(1, maxMiss);
        _outlierPx = Math.Max(1.0, outlierPx);
    }

    public int Count => _stars.Count;
    public bool HasStars => _stars.Count > 0;
    public IReadOnlyList<TrackedStar> Stars => _stars;

    /// <summary>Reset the tracker. The first reference is the primary (the lock
    /// position); the rest are secondaries. Positions are full-frame pixels.</summary>
    public void Reset(IEnumerable<(double x, double y)> refs) {
        _stars.Clear();
        _lastDx = _lastDy = 0;
        bool first = true;
        foreach (var (x, y) in refs) {
            _stars.Add(new TrackedStar {
                RefX = x, RefY = y, CurX = x, CurY = y,
                Snr = 0, Found = false, MissCount = 0, IsPrimary = first
            });
            first = false;
        }
    }

    public void Clear() { _stars.Clear(); _lastDx = _lastDy = 0; }

    /// <summary>Shift every reference position by the same vector (used by
    /// dither so all stars stay consistent with the new lock point).</summary>
    public void OffsetReferences(double dx, double dy) {
        foreach (var s in _stars) { s.RefX += dx; s.RefY += dy; }
    }

    /// <summary>Recentre every star on the frame and reduce to one field offset.</summary>
    public MultiStarResult Update(ushort[] img, int width, int height) {
        if (_stars.Count == 0) return new MultiStarResult(false, 0, 0, 0, 0, 0, 0);

        double primHfd = 0;

        // 1. Recentre each star near its predicted position (ref + last offset).
        foreach (var s in _stars) {
            double predX = s.RefX + _lastDx;
            double predY = s.RefY + _lastDy;
            var r = GuideStar.Find(img, width, height, predX, predY, _searchRegion);
            if (r.Found) {
                s.CurX = r.X; s.CurY = r.Y; s.Snr = r.Snr;
                s.Found = true; s.MissCount = 0;
                if (s.IsPrimary) primHfd = r.Hfd;
            } else {
                s.Found = false; s.MissCount++;
            }
        }

        // 2. Collect per-star offsets from the stars that were found.
        var offX = new List<double>();
        var offY = new List<double>();
        var snrs = new List<double>();
        TrackedStar primary = _stars[0];
        double primSnr = 0;
        foreach (var s in _stars) {
            if (!s.Found) continue;
            offX.Add(s.CurX - s.RefX);
            offY.Add(s.CurY - s.RefY);
            snrs.Add(s.Snr);
        }
        // Track the primary's quality for the step display (fall back later).
        if (primary.Found) { primSnr = primary.Snr; }

        // 3. Drop secondaries that have been missing too long (keep the primary).
        for (int i = _stars.Count - 1; i >= 1; i--) {
            if (_stars[i].MissCount > _maxMiss) _stars.RemoveAt(i);
        }

        if (offX.Count == 0) {
            return new MultiStarResult(false, _lastDx, _lastDy, 0, 0, 0, _stars.Count);
        }

        // 4. Robust combine: reject offsets far from the median, then take an
        //    SNR-weighted mean of the survivors.
        double medX = Median(offX), medY = Median(offY);
        double sumX = 0, sumY = 0, sumW = 0, sumSnr = 0;
        int used = 0;
        for (int i = 0; i < offX.Count; i++) {
            double dev = Math.Sqrt((offX[i] - medX) * (offX[i] - medX) +
                                   (offY[i] - medY) * (offY[i] - medY));
            if (dev > _outlierPx) continue;
            double w = snrs[i] > 0 ? snrs[i] : 1.0;
            sumX += offX[i] * w; sumY += offY[i] * w; sumW += w;
            sumSnr += snrs[i]; used++;
        }
        if (used == 0) {
            // All survivors rejected as outliers: fall back to the median.
            sumX = medX; sumY = medY; sumW = 1.0; used = offX.Count;
            sumSnr = snrs.Count > 0 ? snrs[0] : 0;
        }

        double dx = sumX / sumW, dy = sumY / sumW;
        _lastDx = dx; _lastDy = dy;

        double snrOut = primary.Found ? primSnr : (used > 0 ? sumSnr / used : 0);
        // HFD is not tracked per star here; the caller can read the primary's
        // HFD separately if needed. Report 0 (PHD2's combined step has no HFD).
        return new MultiStarResult(true, dx, dy, snrOut, primHfd, used, _stars.Count);
    }

    private static double Median(List<double> v) {
        if (v.Count == 0) return 0;
        var s = new List<double>(v);
        s.Sort();
        int n = s.Count;
        return (n % 2 == 1) ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
    }
}