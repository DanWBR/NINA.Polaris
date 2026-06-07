using System;
using System.Collections.Generic;
using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// PERF #366: the SpatialGrid must return the exact same nearest neighbor
/// as a brute-force scan (it replaces O(n*m) star matching in PCC and the
/// batch-stack alignment residual). These tests pin that equivalence for
/// both the bounded (radius-limited) and unbounded (global-nearest) modes.
/// </summary>
[TestFixture]
public class SpatialGridTests {

    private static (double x, double y)[] RandomPoints(int n, int seed, double extent) {
        var rng = new Random(seed);
        var pts = new (double, double)[n];
        for (int i = 0; i < n; i++)
            pts[i] = (rng.NextDouble() * extent, rng.NextDouble() * extent);
        return pts;
    }

    private static (int idx, double d2) BruteNearest((double x, double y)[] pts,
                                                     double qx, double qy, double maxRadius) {
        int best = -1;
        double bestD2 = double.IsPositiveInfinity(maxRadius)
            ? double.PositiveInfinity : maxRadius * maxRadius;
        for (int i = 0; i < pts.Length; i++) {
            double dx = pts[i].x - qx, dy = pts[i].y - qy;
            double d2 = dx * dx + dy * dy;
            if (d2 <= bestD2) { bestD2 = d2; best = i; }
        }
        return (best, bestD2);
    }

    [Test]
    public void TryNearest_Unbounded_MatchesBruteForceDistance() {
        var pts = RandomPoints(400, seed: 12345, extent: 2000);
        var grid = new SpatialGrid<int>(8.0);
        for (int i = 0; i < pts.Length; i++) grid.Add(pts[i].x, pts[i].y, i);

        var queries = RandomPoints(200, seed: 999, extent: 2000);
        foreach (var (qx, qy) in queries) {
            var (_, bruteD2) = BruteNearest(pts, qx, qy, double.PositiveInfinity);
            bool found = grid.TryNearest(qx, qy, double.PositiveInfinity, out _, out double gridD2);
            Assert.That(found, Is.True);
            // Distance to the nearest must match exactly (the matched index
            // can differ only on exact ties, which random doubles avoid).
            Assert.That(gridD2, Is.EqualTo(bruteD2).Within(1e-9));
        }
    }

    [Test]
    public void TryNearest_Bounded_MatchesBruteForceWithinRadius() {
        var pts = RandomPoints(300, seed: 7, extent: 500);
        var grid = new SpatialGrid<int>(3.0);
        for (int i = 0; i < pts.Length; i++) grid.Add(pts[i].x, pts[i].y, i);

        const double radius = 3.0;
        var queries = RandomPoints(300, seed: 42, extent: 500);
        foreach (var (qx, qy) in queries) {
            var (bruteIdx, bruteD2) = BruteNearest(pts, qx, qy, radius);
            bool found = grid.TryNearest(qx, qy, radius, out int gridItem, out double gridD2);
            Assert.That(found, Is.EqualTo(bruteIdx >= 0));
            if (found) {
                Assert.That(gridD2, Is.EqualTo(bruteD2).Within(1e-9));
                // Within radius the closest point is unambiguous for random
                // data, so the returned item should be the brute index too.
                Assert.That(gridItem, Is.EqualTo(bruteIdx));
            }
        }
    }

    [Test]
    public void TryNearest_EmptyGrid_ReturnsFalse() {
        var grid = new SpatialGrid<int>(4.0);
        bool found = grid.TryNearest(10, 10, double.PositiveInfinity, out _, out _);
        Assert.That(found, Is.False);
    }

    [Test]
    public void TryNearest_OutsideRadius_ReturnsFalse() {
        var grid = new SpatialGrid<int>(4.0);
        grid.Add(0, 0, 1);
        bool found = grid.TryNearest(100, 100, 5.0, out _, out _);
        Assert.That(found, Is.False);
    }
}
