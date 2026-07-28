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
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Per-frame guiding statistics stamped into the saved header (GUIDRMS &amp;c).
/// The point of these numbers is to correlate tracking with star shape, so the
/// window scoping and the RMS convention are what matter: a session-wide
/// average would smear one bad gust across every frame of the night.
/// </summary>
[TestFixture]
public class GuidingStatsCollectorTests {

    private static readonly DateTime T0 = new(2026, 7, 28, 22, 0, 0, DateTimeKind.Utc);

    private static GuideStep Step(double sec, double raArcsec, double decArcsec) =>
        new() { Timestamp = T0.AddSeconds(sec), RaArcsec = raArcsec, DecArcsec = decArcsec };

    [Test]
    public void NoSteps_ReportsNothing() {
        var info = GuidingStatsCollector.Summarise(
            new List<GuideStep>(), T0, T0.AddSeconds(60));
        Assert.That(info.SampleCount, Is.Zero);
        Assert.That(info.RmsTotalArcsec, Is.Zero, "no data must not read as perfect tracking");
    }

    [Test]
    public void NullSteps_AreSafe() {
        var info = GuidingStatsCollector.Summarise(null, T0, T0.AddSeconds(60));
        Assert.That(info.SampleCount, Is.Zero);
    }

    [Test]
    public void OnlyStepsInsideTheWindowCount() {
        var steps = new List<GuideStep> {
            Step(-30, 10, 10),   // before the exposure opened
            Step(10, 1, 0),
            Step(20, 1, 0),
            Step(90, 10, 10),    // after it closed
        };
        var info = GuidingStatsCollector.Summarise(steps, T0, T0.AddSeconds(60));
        Assert.That(info.SampleCount, Is.EqualTo(2), "the 10-arcsec excursions are outside");
        Assert.That(info.RmsRaArcsec, Is.EqualTo(1).Within(1e-9));
    }

    /// <summary>RMS is taken about ZERO, not about the sample mean: the lock
    /// position is the target, so a steady offset is a real tracking error and
    /// must not be subtracted away as a "mean".</summary>
    [Test]
    public void ConstantOffset_CountsAsError() {
        var steps = new List<GuideStep> { Step(1, 2, 0), Step(2, 2, 0), Step(3, 2, 0) };
        var info = GuidingStatsCollector.Summarise(steps, T0, T0.AddSeconds(10));
        Assert.That(info.RmsRaArcsec, Is.EqualTo(2).Within(1e-9),
            "a 2-arcsec standing offset is 2 arcsec of error, not zero");
    }

    [Test]
    public void TotalCombinesBothAxes() {
        // RA 3, Dec 4 on every sample -> total 5 (3-4-5 triangle).
        var steps = new List<GuideStep> { Step(1, 3, 4), Step(2, 3, 4) };
        var info = GuidingStatsCollector.Summarise(steps, T0, T0.AddSeconds(10));
        Assert.Multiple(() => {
            Assert.That(info.RmsRaArcsec, Is.EqualTo(3).Within(1e-9));
            Assert.That(info.RmsDecArcsec, Is.EqualTo(4).Within(1e-9));
            Assert.That(info.RmsTotalArcsec, Is.EqualTo(5).Within(1e-9));
        });
    }

    [Test]
    public void RmsIsQuadratic_NotAnAverageOfMagnitudes() {
        // 0 and 2 -> RMS sqrt((0+4)/2) = 1.414..., NOT the arithmetic mean 1.
        var steps = new List<GuideStep> { Step(1, 0, 0), Step(2, 2, 0) };
        var info = GuidingStatsCollector.Summarise(steps, T0, T0.AddSeconds(10));
        Assert.That(info.RmsRaArcsec, Is.EqualTo(Math.Sqrt(2)).Within(1e-9));
    }

    [Test]
    public void PeakIsTheWorstSingleExcursion() {
        var steps = new List<GuideStep> {
            Step(1, 0.5, 0.5), Step(2, 3, 4), Step(3, 0.2, 0.1)
        };
        var info = GuidingStatsCollector.Summarise(steps, T0, T0.AddSeconds(10));
        Assert.That(info.PeakArcsec, Is.EqualTo(5).Within(1e-9),
            "the gust that smeared the frame is the number worth keeping");
    }

    [Test]
    public void WindowBoundsAreInclusive() {
        var steps = new List<GuideStep> { Step(0, 1, 0), Step(60, 1, 0) };
        var info = GuidingStatsCollector.Summarise(steps, T0, T0.AddSeconds(60));
        Assert.That(info.SampleCount, Is.EqualTo(2));
    }

    [Test]
    public void NaNSamples_AreSkipped() {
        var steps = new List<GuideStep> {
            Step(1, double.NaN, 0), Step(2, 2, 0)
        };
        var info = GuidingStatsCollector.Summarise(steps, T0, T0.AddSeconds(10));
        Assert.Multiple(() => {
            Assert.That(info.SampleCount, Is.EqualTo(1));
            Assert.That(info.RmsRaArcsec, Is.EqualTo(2).Within(1e-9));
        });
    }

    [Test]
    public void BackendIsCarriedThrough() {
        var steps = new List<GuideStep> { Step(1, 1, 1) };
        var info = GuidingStatsCollector.Summarise(steps, T0, T0.AddSeconds(10), "native");
        Assert.That(info.Backend, Is.EqualTo("native"));
    }
}
