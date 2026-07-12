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

using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Covers <see cref="AutoFocusService.RobustMeanHfr"/> — the per-frame
/// sigma-clip that stops spurious large-HFR detections (merged donuts, nebula
/// structure, hot regions) from spiking an autofocus sweep point and shattering
/// the V-curve.
/// </summary>
[TestFixture]
public class AutoFocusRobustHfrTests {

    [Test]
    public void TightCluster_KeepsAllStars_MeanIsClusterCentre() {
        var hfrs = new[] { 2.90, 2.95, 3.00, 3.05, 3.10 }.ToList();
        var (mean, stdev, kept) = AutoFocusService.RobustMeanHfr(hfrs);
        Assert.That(mean, Is.EqualTo(3.00).Within(1e-9));
        Assert.That(kept, Is.EqualTo(5));
        Assert.That(stdev, Is.GreaterThan(0));
    }

    [Test]
    public void HugeOutliers_AreClipped_MeanTracksTheRealStars() {
        // 20 real stars at HFR 3.0 + three spurious blobs. A plain mean would
        // be pulled to ~18.7; the robust mean must stay on the real cluster.
        var hfrs = Enumerable.Repeat(3.0, 20)
            .Concat(new[] { 50.0, 120.0, 200.0 })
            .ToList();

        double plainMean = hfrs.Average();
        var (mean, _, kept) = AutoFocusService.RobustMeanHfr(hfrs);

        Assert.That(plainMean, Is.GreaterThan(15));      // the bug it fixes
        Assert.That(mean, Is.EqualTo(3.0).Within(1e-9)); // the robust result
        Assert.That(kept, Is.EqualTo(20));
    }

    [Test]
    public void LargeButUniformDonuts_AreAllKept() {
        // Defocused shoulder: every star is a big donut of ~the same size.
        // High absolute HFR must NOT be clipped — only within-frame outliers.
        var hfrs = new[] { 24.0, 25.0, 26.0, 25.5, 24.5, 25.2 }.ToList();
        var (mean, _, kept) = AutoFocusService.RobustMeanHfr(hfrs);
        Assert.That(kept, Is.EqualTo(6));
        Assert.That(mean, Is.EqualTo(25.033).Within(0.05));
    }

    [Test]
    public void Empty_ReturnsZero() {
        var (mean, stdev, kept) = AutoFocusService.RobustMeanHfr(new List<double>());
        Assert.That(mean, Is.EqualTo(0));
        Assert.That(stdev, Is.EqualTo(0));
        Assert.That(kept, Is.EqualTo(0));
    }

    [Test]
    public void NonPositiveAndNaN_AreIgnored() {
        var hfrs = new List<double> { 0, -1, double.NaN, 3.0, 3.0, 3.0 };
        var (mean, _, kept) = AutoFocusService.RobustMeanHfr(hfrs);
        Assert.That(mean, Is.EqualTo(3.0).Within(1e-9));
        Assert.That(kept, Is.EqualTo(3));
    }
}
