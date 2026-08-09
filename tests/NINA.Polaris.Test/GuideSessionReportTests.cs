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

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// What an unattended run reports when it finishes.
///
/// The operator chose "keep shooting, just mark them": sky time cannot be
/// recovered, and STUDIO's grading can throw out the bad subs later on
/// evidence. That makes two things load-bearing: the mark on the frame, and a
/// report at the end so the night is not silently different from what it looks
/// like.
/// </summary>
[TestFixture]
public class GuideSessionReportTests {

    private static GuideRunawayGuard Guard() =>
        new(guiders: null!, profiles: null!,
            logger: NullLogger<GuideRunawayGuard>.Instance);

    private static void Set(GuideRunawayGuard g, string prop, object value) =>
        typeof(GuideRunawayGuard).GetProperty(prop)!
            .SetValue(g, value, BindingFlags.NonPublic | BindingFlags.Instance
                                | BindingFlags.Public | BindingFlags.SetProperty,
                      binder: null, index: null, culture: null);

    /// <summary>A clean night says nothing. A report that always fires is one
    /// nobody reads, and then the one that matters is missed too.</summary>
    [Test]
    public void ACleanRunReportsNothing() {
        Assert.That(Guard().SummariseSession(), Is.Null);
    }

    [Test]
    public void RoughTimeAndFrameCountAreBothReported() {
        var g = Guard();
        Set(g, nameof(g.DegradedTotal), TimeSpan.FromMinutes(82));
        Set(g, nameof(g.DegradedFrames), 8);

        var s = g.SummariseSession();

        Assert.That(s, Does.Contain("82"));
        Assert.That(s, Does.Contain("8 frame"),
            "minutes alone do not say how many subs fell inside them, which is "
            + "the number that decides whether the night is worth restacking");
    }

    [Test]
    public void RestartsAreReported() {
        var g = Guard();
        Set(g, nameof(g.RestartsThisSession), 4);
        Set(g, nameof(g.BudgetExhausted), true);

        var s = g.SummariseSession();

        Assert.That(s, Does.Contain("4 automatic restart"));
        Assert.That(s, Does.Contain("budget"),
            "an exhausted budget means nothing was watching after that point, "
            + "which the operator has to know");
    }

    [Test]
    public void RestartsAloneStillReport() {
        var g = Guard();
        Set(g, nameof(g.RestartsThisSession), 1);

        Assert.That(g.SummariseSession(), Is.Not.Null);
    }

    [Test]
    public void BeginSessionClearsTheLastRun() {
        var g = Guard();
        Set(g, nameof(g.DegradedTotal), TimeSpan.FromMinutes(30));
        Set(g, nameof(g.DegradedFrames), 5);
        Set(g, nameof(g.RestartsThisSession), 2);
        Assert.That(g.SummariseSession(), Is.Not.Null, "precondition");

        g.BeginSession();

        Assert.That(g.SummariseSession(), Is.Null,
            "the report describes THIS run, not everything since the host booted");
    }

    // ── the mark that survives to disk ──────────────────────────────────

    /// <summary>The flag only appears when it is true, so an ordinary frame's
    /// header does not grow a card saying nothing happened.</summary>
    [Test]
    public void AnUndegradedFrameGetsNoMarker() {
        var m = new ImageMetaData();
        m.Guiding.SampleCount = 40;
        m.Guiding.RmsTotalArcsec = 0.42;

        Assert.That(m.Guiding.Degraded, Is.False);
        Assert.That(m.Guiding.BaselineArcsec, Is.EqualTo(0));
    }

    /// <summary>Both halves of the mark matter: the flag says a frame is
    /// suspect, and the baseline beside the RMS says by how much. A bare flag
    /// cannot tell 2x from 30x, and those are different decisions.</summary>
    [Test]
    public void TheMarkCarriesTheSessionNormalNotJustAFlag() {
        var m = new ImageMetaData();
        m.Guiding.SampleCount = 40;
        m.Guiding.RmsTotalArcsec = 1.53;
        m.Guiding.Degraded = true;
        m.Guiding.BaselineArcsec = 0.35;

        Assert.That(m.Guiding.Degraded, Is.True);
        Assert.That(m.Guiding.RmsTotalArcsec / m.Guiding.BaselineArcsec,
            Is.EqualTo(4.4).Within(0.1),
            "the numbers in the header must reconstruct the ratio the operator "
            + "saw at the time (this is the real 06:13 frame from the SV503 night)");
    }
}
