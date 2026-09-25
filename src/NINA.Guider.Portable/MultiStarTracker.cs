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

// Multi-star guiding, ported from PHD2 (OpenPHDGuiding) guider_multistar.cpp,
// BSD-3-Clause. See licenses/PHD2-LICENSE.txt.
//
// The primary star is measured by the ordinary single-star path and is the
// baseline. Secondary stars only REFINE that measurement, exactly as PHD2
// does: the sum is seeded with the primary at weight 1, each secondary
// contributes its displacement weighted by SNR_secondary / SNR_primary, and
// the average replaces the primary's offset only when it is smaller than the
// primary's own.
//
// This file used to combine every star with a median plus outlier rejection
// and an SNR-weighted mean, and it used that result unconditionally. That is
// not what PHD2 does, and the difference showed up as worse guiding.

namespace NINA.Guider.Portable;

/// <summary>Combined field offset for this frame. <c>Refined</c> says whether
/// the secondaries actually changed the primary's own measurement.</summary>
public readonly record struct MultiStarResult(
    bool Found, double OffsetX, double OffsetY,
    double Snr, double Hfd, int UsedCount, int TotalCount, bool Refined);

/// <summary>
/// PHD2's multi-star refinement. Tracks the secondary stars around a primary
/// measured elsewhere, and averages their displacements into the primary's
/// offset when that lowers the measured error.
/// </summary>
public sealed class MultiStarTracker {
    /// <summary>PHD2 MAX_LIST_SIZE.</summary>
    public const int MaxListSize = 12;
    /// <summary>PHD2 DEFAULT_STABILITY_SIGMAX: how many sigma of primary
    /// excursion suspend averaging.</summary>
    public const double StabilitySigmaX = 5.0;
    /// <summary>PHD2: a secondary further than this many sigma from its
    /// reference is a miss, not data.</summary>
    private const double MissSigma = 2.5;
    private const int MaxMissCount = 10;
    private const int ZeroCountLimit = 5;
    /// <summary>PHD2 waits for more than five primary samples before it trusts
    /// the sigma computed from them.</summary>
    private const int MinStatsCount = 5;

    /// <summary>One tracked star and its bookkeeping (PHD2 GuideStar).</summary>
    public sealed class TrackedStar {
        public double RefX, RefY;        // referencePoint
        public double CurX, CurY;        // last found centroid
        public double OffsetFromPrimaryX, OffsetFromPrimaryY;
        public double Snr;
        public bool Found;
        public bool WasLost;
        public int MissCount;
        public int ZeroCount;
        public bool IsPrimary;
    }

    private readonly List<TrackedStar> _stars = new();
    private readonly int _searchRegion;

    // Running statistics of the primary star's displacement (PHD2
    // DescriptiveStats, Welford), sample sigma with n-1 as PHD2 uses.
    private int _statCount;
    private double _statMean, _statS;
    private bool _stabilizing = true;
    private bool _lockPositionMoved;

    public MultiStarTracker(int searchRegion = 15) {
        _searchRegion = Math.Max(5, searchRegion);
    }

    public int Count => _stars.Count;
    public bool HasStars => _stars.Count > 0;
    public IReadOnlyList<TrackedStar> Stars => _stars;
    public bool Stabilizing => _stabilizing;

    /// <summary>Seed the tracker. The first reference is the primary (the lock
    /// position); the rest are secondaries. Full-frame pixels.</summary>
    public void Reset(IEnumerable<(double x, double y)> refs) {
        _stars.Clear();
        _statCount = 0; _statMean = 0; _statS = 0;
        _stabilizing = true;          // PHD2 stabilizes until it has data
        _lockPositionMoved = false;
        bool first = true;
        double px = 0, py = 0;
        foreach (var (x, y) in refs) {
            if (first) { px = x; py = y; }
            if (_stars.Count >= MaxListSize) break;
            _stars.Add(new TrackedStar {
                RefX = x, RefY = y, CurX = x, CurY = y,
                OffsetFromPrimaryX = x - px, OffsetFromPrimaryY = y - py,
                Snr = 0, Found = false, WasLost = false,
                MissCount = 0, ZeroCount = 0, IsPrimary = first
            });
            first = false;
        }
    }

    public void Clear() {
        _stars.Clear();
        _statCount = 0; _statMean = 0; _statS = 0;
        _stabilizing = true;
        _lockPositionMoved = false;
    }

    /// <summary>The lock position moved (a dither). PHD2 does not shift the
    /// secondary references by that vector: it re-reads each secondary where it
    /// actually is once the primary has settled again, so a dither cannot smear
    /// the references by a displacement nobody verified.</summary>
    public void NoteLockPositionMoved() {
        _lockPositionMoved = true;
        _stabilizing = true;
    }

    private double Sigma => _statCount > 1 ? Math.Sqrt(_statS / (_statCount - 1)) : 0.0;

    private void AddPrimaryDistance(double v) {
        _statCount++;
        if (_statCount == 1) { _statMean = v; _statS = 0; return; }
        double newMean = _statMean + (v - _statMean) / _statCount;
        _statS += (v - _statMean) * (v - newMean);
        _statMean = newMean;
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>
    /// Refine the primary star's offset with the secondaries. <paramref name="offsetX"/>
    /// and <paramref name="offsetY"/> are the primary's own displacement from the
    /// lock position in full-frame pixels, as the single-star path measured it;
    /// <paramref name="primaryX"/> / <paramref name="primaryY"/> are where it was
    /// found.
    /// </summary>
    /// <param name="allowRefine">False while the guider is settling or not
    /// guiding, which is when PHD2 leaves the primary alone.</param>
    public MultiStarResult Refine(ushort[] img, int width, int height,
                                  double primaryX, double primaryY, double primarySnr,
                                  double primaryHfd, double offsetX, double offsetY,
                                  bool allowRefine) {
        if (_stars.Count > 0) {
            var p = _stars[0];
            p.CurX = primaryX; p.CurY = primaryY; p.Snr = primarySnr; p.Found = true;
        }

        double primaryDistance = Hypot(offsetX, offsetY);
        var plain = new MultiStarResult(true, offsetX, offsetY, primarySnr, primaryHfd,
                                        1, _stars.Count, false);

        if (!allowRefine || _stars.Count <= 1 || primarySnr <= 0) return plain;

        AddPrimaryDistance(primaryDistance);

        // Stabilization window: PHD2 will not average while the primary is
        // making a large excursion, because the secondaries are then measuring
        // the tail of a correction rather than the seeing.
        if (_statCount > MinStatsCount) {
            double sigma = Sigma;
            if (!_stabilizing && primaryDistance > StabilitySigmaX * sigma) {
                _stabilizing = true;
            } else if (_stabilizing && primaryDistance <= 2.0 * sigma) {
                _stabilizing = false;
                if (_lockPositionMoved) {
                    _lockPositionMoved = false;
                    ReReferenceSecondaries(img, width, height, primaryX, primaryY);
                    return plain;   // the references now describe this frame
                }
            }
        } else {
            _stabilizing = true;
        }

        if (_stabilizing || (offsetX == 0.0 && offsetY == 0.0)) return plain;

        double sumX = offsetX, sumY = offsetY, sumWeights = 1.0;
        int validStars = 0;
        bool averaged = false;
        double sigmaNow = Sigma;

        for (int i = 1; i < _stars.Count; i++) {
            var s = _stars[i];
            double searchX = s.WasLost ? primaryX + s.OffsetFromPrimaryX : s.CurX;
            double searchY = s.WasLost ? primaryY + s.OffsetFromPrimaryY : s.CurY;
            var r = GuideStar.Find(img, width, height, searchX, searchY, _searchRegion);
            if (!r.Found) {
                s.Found = false;
                s.WasLost = true;
                continue;
            }

            s.CurX = r.X; s.CurY = r.Y; s.Snr = r.Snr; s.Found = true; s.WasLost = false;

            double dX = r.X - s.RefX;
            double dY = r.Y - s.RefY;

            if (dX == 0.0 && dY == 0.0) {
                // Exactly zero on both axes: a hot pixel, not a star.
                _stars.RemoveAt(i--);
                continue;
            }
            if (dX == 0.0 || dY == 0.0) {
                if (++s.ZeroCount == ZeroCountLimit) { _stars.RemoveAt(i--); continue; }
            } else if (s.ZeroCount > 0) {
                s.ZeroCount--;
            }

            if (Hypot(dX, dY) > MissSigma * sigmaNow) {
                if (++s.MissCount > MaxMissCount) {
                    // Give up on the old reference and adopt where it is now.
                    s.RefX = r.X; s.RefY = r.Y; s.MissCount = 0;
                }
                continue;
            }
            if (s.MissCount > 0) s.MissCount--;

            double wt = r.Snr / primarySnr;
            sumX += wt * dX;
            sumY += wt * dY;
            sumWeights += wt;
            validStars++;
            averaged = true;
        }

        if (!averaged) return plain;

        double avgX = sumX / sumWeights;
        double avgY = sumY / sumWeights;
        // PHD2: take the average only when it is smaller than the single-star
        // delta. Averaging must reduce the measured error, never enlarge it.
        if (Hypot(avgX, avgY) >= primaryDistance) return plain;

        return new MultiStarResult(true, avgX, avgY, primarySnr, primaryHfd,
                                   validStars + 1, _stars.Count, true);
    }

    /// <summary>After a lock change has settled, re-read every secondary where
    /// it actually is and make that its new reference (PHD2).</summary>
    private void ReReferenceSecondaries(ushort[] img, int width, int height,
                                        double primaryX, double primaryY) {
        for (int i = 1; i < _stars.Count; i++) {
            var s = _stars[i];
            double ex = primaryX + s.OffsetFromPrimaryX;
            double ey = primaryY + s.OffsetFromPrimaryY;
            var r = GuideStar.Find(img, width, height, ex, ey, _searchRegion);
            if (r.Found) {
                s.CurX = r.X; s.CurY = r.Y; s.Snr = r.Snr;
                s.RefX = r.X; s.RefY = r.Y;
                s.Found = true; s.WasLost = false; s.MissCount = 0;
            } else {
                s.Found = false; s.WasLost = true;
            }
        }
    }
}
