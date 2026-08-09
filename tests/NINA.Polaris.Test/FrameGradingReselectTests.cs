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
using NINA.Polaris.Services.Studio;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Moving the keep threshold after a grading run.
///
/// Measuring frames costs a star-detection pass per FITS; deciding where to cut
/// is arithmetic on numbers already in hand. Reselect exists so the operator can
/// drag a threshold and watch the keep set move without re-reading a night of
/// subs, and so the rule stays defined in exactly one place instead of being
/// copied into the browser.
/// </summary>
[TestFixture]
public class FrameGradingReselectTests {

    /// <summary>Plant a finished job straight into the service's store. The
    /// alternative is writing real FITS to disk, which would test the star
    /// detector rather than the selection rule.</summary>
    private static FrameGradingService WithJob(string jobId, params GradedFrameDto[] results) {
        var svc = (FrameGradingService)Activator.CreateInstance(
            typeof(FrameGradingService),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { null, NullLogger<FrameGradingService>.Instance },
            culture: null)!;

        var jobs = typeof(FrameGradingService)
            .GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc)!;
        var progress = new GradeProgress {
            JobId = jobId,
            InProgress = false,
            Stage = "done",
            Done = results.Length,
            Total = results.Length,
            Results = results.ToList(),
            Selected = results.Where(r => r.Keep).Select(r => r.Path).ToList(),
            SelectedCount = results.Count(r => r.Keep),
        };
        jobs.GetType().GetProperty("Item")!.SetValue(jobs, progress, new object[] { jobId });
        return svc;
    }

    private static GradedFrameDto F(string name, int stars, double hfr, bool keep = true) =>
        new($"/data/{name}", name, stars, hfr, 0.3, 0.5, keep);

    [Test]
    public void KeepBestTakesTheSharpestN() {
        var svc = WithJob("j1",
            F("a.fits", 100, 2.0), F("b.fits", 100, 3.0),
            F("c.fits", 100, 4.0), F("d.fits", 100, 5.0));

        var p = svc.Reselect("j1", keepBest: 2, hfrTolerancePct: null)!;

        Assert.That(p.SelectedCount, Is.EqualTo(2));
        Assert.That(p.Selected, Is.EquivalentTo(new[] { "/data/a.fits", "/data/b.fits" }));
    }

    [Test]
    public void TheHfrBandKeepsEverythingWithinTheTolerance() {
        var svc = WithJob("j1",
            F("a.fits", 100, 2.0),    // best
            F("b.fits", 100, 2.2),    // +10%
            F("c.fits", 100, 2.6));   // +30%

        var p = svc.Reselect("j1", keepBest: null, hfrTolerancePct: 15)!;

        Assert.That(p.Selected, Is.EquivalentTo(new[] { "/data/a.fits", "/data/b.fits" }),
            "2.2 is inside 15% of 2.0 and 2.6 is not");
    }

    /// <summary>Same precedence the grading job itself uses, so a UI that has
    /// both controls filled behaves the way the original run did.</summary>
    [Test]
    public void AFixedCountWinsOverTheBand() {
        var svc = WithJob("j1",
            F("a.fits", 100, 2.0), F("b.fits", 100, 2.05), F("c.fits", 100, 2.1));

        var p = svc.Reselect("j1", keepBest: 1, hfrTolerancePct: 50)!;

        Assert.That(p.Selected, Is.EqualTo(new[] { "/data/a.fits" }));
    }

    /// <summary>The keep flags on the rows have to move with the selection:
    /// the table reads them, not the paths list.</summary>
    [Test]
    public void TheRowFlagsFollowTheNewSelection() {
        var svc = WithJob("j1",
            F("a.fits", 100, 2.0, keep: true),
            F("b.fits", 100, 9.0, keep: true));

        var p = svc.Reselect("j1", keepBest: 1, hfrTolerancePct: null)!;

        Assert.That(p.Results.Single(r => r.FileName == "a.fits").Keep, Is.True);
        Assert.That(p.Results.Single(r => r.FileName == "b.fits").Keep, Is.False,
            "a row left flagged after the threshold moved would show a tick "
            + "next to a frame that is no longer going into the stack");
    }

    [Test]
    public void AnUnknownJobIsNotFound() {
        var svc = WithJob("j1", F("a.fits", 100, 2.0));

        Assert.That(svc.Reselect("nope", 2, null), Is.Null);
    }
}
