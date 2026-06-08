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

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// PERF #366: a uniform-cell spatial hash for 2D nearest-neighbor queries,
/// replacing O(n*m) brute-force star-to-star matching (PCC catalog match,
/// batch-stack alignment residual) with an expanding-ring search that
/// touches only the cells near the query point.
///
/// The result is the exact nearest point (same answer as brute force): the
/// ring search stops only once the closest possible point in any farther
/// ring is provably beyond the best distance found so far. Build once over
/// the reference set, then query per candidate.
/// </summary>
public sealed class SpatialGrid<T> {
    private readonly double _cell;
    private readonly Dictionary<long, List<(double x, double y, T item)>> _cells = new();
    private int _minCx = int.MaxValue, _minCy = int.MaxValue;
    private int _maxCx = int.MinValue, _maxCy = int.MinValue;

    /// <param name="cellSize">Cell edge in the same units as the points.
    /// A value near the typical match radius is a good default.</param>
    public SpatialGrid(double cellSize) {
        _cell = cellSize > 0 ? cellSize : 1.0;
    }

    private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    public void Add(double x, double y, T item) {
        int cx = (int)Math.Floor(x / _cell);
        int cy = (int)Math.Floor(y / _cell);
        var k = Key(cx, cy);
        if (!_cells.TryGetValue(k, out var list)) {
            list = new List<(double, double, T)>();
            _cells[k] = list;
        }
        list.Add((x, y, item));
        if (cx < _minCx) _minCx = cx;
        if (cy < _minCy) _minCy = cy;
        if (cx > _maxCx) _maxCx = cx;
        if (cy > _maxCy) _maxCy = cy;
    }

    public int Count => _cells.Count;

    /// <summary>
    /// Find the nearest stored point to (<paramref name="x"/>,
    /// <paramref name="y"/>). Pass <paramref name="maxRadius"/> =
    /// double.PositiveInfinity for an unbounded global-nearest search, or a
    /// finite radius to reject anything farther (matching a brute-force
    /// loop seeded with bestDist2 = radius^2). Returns false if nothing is
    /// within range.
    /// </summary>
    public bool TryNearest(double x, double y, double maxRadius, out T nearest, out double bestDist2) {
        nearest = default!;
        bool bounded = !double.IsPositiveInfinity(maxRadius);
        bestDist2 = bounded ? maxRadius * maxRadius : double.PositiveInfinity;
        bool found = false;
        if (_cells.Count == 0) return false;

        int cx = (int)Math.Floor(x / _cell);
        int cy = (int)Math.Floor(y / _cell);

        // Bound the expansion by the occupied extent so an unbounded search
        // over a sparse grid still terminates.
        int extent = Math.Max(Math.Max(cx - _minCx, _maxCx - cx),
                              Math.Max(cy - _minCy, _maxCy - cy));
        if (extent < 0) extent = 0;

        for (int span = 0; span <= extent; span++) {
            ScanRing(cx, cy, span, x, y, ref bestDist2, ref nearest, ref found);
            // The closest a point in ring (span+1) can be is span*_cell
            // (the inner edge of the next ring). Once that floor exceeds
            // the best distance found, no farther ring can improve it.
            double nextRingMin = span * _cell;
            if (found && nextRingMin * nextRingMin > bestDist2) break;
            if (bounded && nextRingMin > maxRadius) break;
        }
        return found;
    }

    private void ScanRing(int cx, int cy, int span, double x, double y,
                          ref double bestDist2, ref T nearest, ref bool found) {
        if (span == 0) {
            ScanCell(cx, cy, x, y, ref bestDist2, ref nearest, ref found);
            return;
        }
        // Cells whose Chebyshev distance from (cx,cy) is exactly `span`.
        for (int gx = cx - span; gx <= cx + span; gx++) {
            ScanCell(gx, cy - span, x, y, ref bestDist2, ref nearest, ref found);
            ScanCell(gx, cy + span, x, y, ref bestDist2, ref nearest, ref found);
        }
        for (int gy = cy - span + 1; gy <= cy + span - 1; gy++) {
            ScanCell(cx - span, gy, x, y, ref bestDist2, ref nearest, ref found);
            ScanCell(cx + span, gy, x, y, ref bestDist2, ref nearest, ref found);
        }
    }

    private void ScanCell(int gx, int gy, double x, double y,
                          ref double bestDist2, ref T nearest, ref bool found) {
        if (!_cells.TryGetValue(Key(gx, gy), out var list)) return;
        foreach (var (px, py, item) in list) {
            double dx = px - x, dy = py - y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= bestDist2) {
                bestDist2 = d2;
                nearest = item;
                found = true;
            }
        }
    }
}