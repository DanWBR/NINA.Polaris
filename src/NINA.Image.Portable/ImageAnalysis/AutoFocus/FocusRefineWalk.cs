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
using System.Collections.Generic;
using System.Linq;

namespace NINA.Image.ImageAnalysis.AutoFocus;

/// <summary>
/// The fine pass of autofocus, which also measures the focuser's backlash.
///
/// <para>The coarse sweep reaches each point moving one way (inward, unless
/// the sweep is reversed). The fine pass starts a little before the coarse
/// vertex and reaches every point moving the OTHER way, one fine step at a
/// time, until the HFR has gone through its minimum and risen clearly again.
/// If the focuser has backlash, the first part of every reversal turns the
/// motor without moving the drawtube, so the fine curve comes out shifted
/// along the walk by exactly that lost motion. The shift between the fine
/// vertex and the coarse vertex is therefore the backlash, measured as a side
/// effect of a normal autofocus.</para>
///
/// <para>The walk doesn't stop at the first point whose HFR matches the
/// coarse minimum. The bottom of the curve is flat: with a 5 % tolerance, a
/// typical focuser "matches" the minimum some 60 steps before reaching it,
/// which is as large as the backlash being measured. It keeps going until the
/// curve rises on the far side and then fits all the fine points, so that the
/// two sides of the flat bottom cancel out. It also doesn't compare against
/// the minimum HFR the coarse fit predicts, because a fit can predict a
/// sharper focus than the sky allows and the walk would never meet it.</para>
///
/// <para>The refined vertex is in the coordinates of the walk direction, so
/// the caller has to reach the final position moving the same way, which
/// cancels the backlash whatever its size.</para>
/// </summary>
public sealed class FocusRefineWalk {
    /// <summary>+1 when every fine point is reached moving out (position
    /// increasing), -1 moving in.</summary>
    public int Direction { get; }
    public double CoarseVertex { get; }
    public int Step { get; }
    /// <summary>The first fine target, one coarse step before the coarse vertex.</summary>
    public int StartPosition { get; }
    /// <summary>The farthest target the walk may reach: a backlash larger
    /// than this distance from the coarse vertex can't be measured.</summary>
    public int Limit { get; }

    private readonly int _minPoints;
    private readonly int _maxPoints;
    private readonly double _riseMargin;
    private readonly List<FocusPoint> _points = new();
    private int _issued;

    public FocusRefineWalk(double coarseVertex, int coarseStep, int fineStep, int direction,
            int minPoints = 5, int maxPoints = 24, double riseMargin = 0.06) {
        Direction = direction >= 0 ? 1 : -1;
        CoarseVertex = coarseVertex;
        Step = Math.Max(1, fineStep);
        _minPoints = Math.Max(3, minPoints);
        _maxPoints = Math.Max(_minPoints, maxPoints);
        _riseMargin = Math.Max(0.01, riseMargin);
        // Backlash only ever moves the fine minimum FURTHER along the walk, so
        // the start only has to cover the coarse fit's own error, plus enough
        // of the near arm for the fit to see the bowl fall before it rises:
        // within a fine step or two of the vertex the curve is so flat that
        // seeing alone decides which point is lowest. One coarse step back,
        // the HFR is typically 10 % above the minimum.
        StartPosition = (int)Math.Round(coarseVertex - Direction * Math.Max(2 * Step, Math.Max(1, coarseStep)));
        Limit = (int)Math.Round(coarseVertex + Direction * Math.Max(3 * Math.Max(1, coarseStep), 8 * Step));
    }

    /// <summary>The fine points measured so far, in walk order.</summary>
    public IReadOnlyList<FocusPoint> Points => _points;

    /// <summary>The next position to measure, or null when the walk is done.</summary>
    public int? Next() {
        if (_issued > _points.Count) throw new InvalidOperationException("Add the previous point first.");
        if (_points.Count >= _maxPoints || IsPastMinimum()) return null;
        int target = StartPosition + Direction * _issued * Step;
        if (Direction * (target - Limit) > 0) return null;
        _issued++;
        return target;
    }

    /// <summary>Record the measurement at the position the focuser reported.
    /// A point with no stars has <paramref name="hfr"/> 0 and only counts
    /// towards the point limit.</summary>
    public void Add(double position, double hfr, double error) {
        _points.Add(new FocusPoint(position, hfr, Math.Max(0.001, error)));
    }

    /// <summary>True once the lowest HFR has at least three valid points
    /// after it, all above it by the rise margin and the last by twice the
    /// margin. Seeing can make one frame look like a minimum and the next
    /// two look like a rise, and a walk that stopped there would miss the
    /// real minimum a step or two further on; a steady three-point rise
    /// doesn't come from seeing.</summary>
    private bool IsPastMinimum() {
        if (_points.Count < _minPoints) return false;
        var valid = _points.Where(p => p.Y > 0).ToList();
        if (valid.Count < 4) return false;
        int best = 0;
        for (int i = 1; i < valid.Count; i++) if (valid[i].Y < valid[best].Y) best = i;
        if (valid.Count - best < 4) return false;
        double floor = valid[best].Y;
        bool Above(FocusPoint p, double margin) => p.Y > floor * (1 + margin) && p.Y > floor + 2 * p.ErrorY;
        return Above(valid[^1], 2 * _riseMargin) && Above(valid[^2], _riseMargin) && Above(valid[^3], _riseMargin);
    }

    /// <summary>
    /// Fit the fine points. The vertex is in walk coordinates; the backlash
    /// is how far it sits from the coarse vertex along the walk (positive
    /// when the reversal lost motion, about 0 when compensation already works).
    /// </summary>
    public FocusRefineOutcome Finish() {
        var valid = _points.Where(p => p.Y > 0).ToList();
        if (valid.Count < 3)
            return FocusRefineOutcome.Failed($"only {valid.Count} fine point(s) had stars");
        double lo = valid.Min(p => p.X), hi = valid.Max(p => p.X);
        double bestY = valid.Min(p => p.Y);
        var bestPoint = valid.First(p => p.Y == bestY);
        if (bestPoint.X == valid[^1].X && !IsPastMinimum())
            return FocusRefineOutcome.Failed(
                $"the HFR was still falling at {bestPoint.X:F0}, the end of the fine walk (backlash larger than the walk?)");

        double? vertex = null;
        double vertexY = bestY;
        if (valid.Count >= 5) {
            var h = new HyperbolicFitting().Calculate(valid);
            if (h.HasFit && h.Minimum.X >= lo && h.Minimum.X <= hi) { vertex = h.Minimum.X; vertexY = h.Minimum.Y; }
        }
        if (vertex == null) {
            // Too few points for the hyperbola, or it failed: a parabola through
            // the bottom of the bowl only, where a parabola is a fair shape.
            var bowl = valid.Where(p => p.Y <= bestY * (1 + 3 * _riseMargin)).ToList();
            if (bowl.Count < 3) bowl = valid;
            var q = new QuadraticFitting().Calculate(bowl);
            if (q.HasFit && q.A2 > 0 && q.Minimum.X >= lo && q.Minimum.X <= hi) { vertex = q.Minimum.X; vertexY = q.Minimum.Y; }
        }
        if (vertex == null)
            return FocusRefineOutcome.Failed("the fine points don't form a bowl");

        double backlash = Direction * (vertex.Value - CoarseVertex);
        return new FocusRefineOutcome(true, vertex.Value, backlash, bestY, Math.Max(vertexY, 0), null);
    }
}

/// <param name="Vertex">Refined focus position, in walk-direction coordinates.</param>
/// <param name="BacklashSteps">Fine vertex minus coarse vertex along the walk.</param>
/// <param name="BestMeasuredHfr">Lowest HFR measured on the walk, the level a
/// confirmation frame at the vertex should reach.</param>
public sealed record FocusRefineOutcome(
        bool Ok, double Vertex, double BacklashSteps, double BestMeasuredHfr, double FitMinimumHfr, string? Reason) {
    public static FocusRefineOutcome Failed(string reason) => new(false, 0, 0, 0, 0, reason);
}
