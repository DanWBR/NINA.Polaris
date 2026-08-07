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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;
using System.Collections.Generic;

namespace NINA.Polaris.Test;

/// <summary>
/// FIELD6-14: after a hinted and a blind solve both fail, ASTAP is retried with a
/// COARSER downsample. Measured on real M8 L-Ultimate subs: a marginal frame that
/// failed at -z 2 solved at -z 4 (coarser binning averages down noise, flattens
/// the bright nebula's high-frequency structure, and compacts stars). This pins
/// the escalation factor. The end-to-end recovery is a field check (needs ASTAP +
/// the failing FITS — verified there: green+z4 solved a frame green+z2 couldn't).
/// </summary>
[TestFixture]
public class AstapDownsampleEscalationTests {
    /// <summary>Solver wired to a profile carrying a downsample setting, which
    /// is the value the escalation has to beat.</summary>
    private static AstapSolver MakeSolver(int profileDownsample) {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        profiles.Active.PlateSolveDownsample = profileDownsample;
        return new AstapSolver(cfg, NullLogger<AstapSolver>.Instance, profiles);
    }

    /// <summary>The default (-z 2) escalates to at least 4 — the factor that
    /// recovered the marginal M8 frame.</summary>
    [Test]
    public void Escalate_FromDefaultTwo_GoesToFour() {
        Assert.That(AstapSolver.EscalatedDownsample(2), Is.EqualTo(4));
    }

    /// <summary>Auto (-z 0, "ASTAP decides") isn't coarse enough for these frames —
    /// escalate it to a concrete 4.</summary>
    [Test]
    public void Escalate_FromAuto_GoesToFour() {
        Assert.That(AstapSolver.EscalatedDownsample(0), Is.EqualTo(4));
    }

    /// <summary>Already coarse: always take a real step up so the retry is actually
    /// different from the pass that just failed.</summary>
    [Test]
    public void Escalate_FromCoarse_StepsUpFurther() {
        Assert.That(AstapSolver.EscalatedDownsample(4), Is.EqualTo(6));
        Assert.That(AstapSolver.EscalatedDownsample(3), Is.EqualTo(5));
    }

    /// <summary>
    /// The escalated retry has to reach the command line. Field log from the
    /// Q6A: three solves in a row carried "-z 1", the last of them under a line
    /// announcing "retrying hinted at -z 4", because the profile's downsample
    /// setting outranked the per-call value and the ladder's last rung re-ran
    /// the solve that had just failed.
    /// </summary>
    [Test]
    public void ExplicitDownsample_ReachesTheCommandLine() {
        var solver = MakeSolver(profileDownsample: 1);

        var normal = solver.BuildArgs("/tmp/f.fits", new PlateSolveOptions { Downsample = 2 });
        Assert.That(normal, Does.Contain("-z 1"),
            "without an explicit value the operator's setting still rules");

        var escalated = solver.BuildArgs("/tmp/f.fits", new PlateSolveOptions {
            Downsample = AstapSolver.EscalatedDownsample(1),
            DownsampleIsExplicit = true
        });
        Assert.That(escalated, Does.Contain("-z 4"),
            "the retry ladder's coarser factor must win over the profile default");
        Assert.That(escalated, Does.Not.Contain("-z 1"));
    }

    /// <summary>
    /// The ladder escalates to a COARSER downsample because the failures it was
    /// built for are noisy, finely-sampled frames. On an undersampled frame that
    /// is backwards: the stars already span about a pixel, and binning turns
    /// them into single-pixel spikes ASTAP reads as hot pixels.
    ///
    /// Field report 2026-08-07: a live stack at 1:2 put a 2.75"/px rig at
    /// 5.5"/px, and the ladder answered with -z 4, i.e. 22"/px. Every solve
    /// failed for an hour; the same sky solved instantly at 1:1.
    /// </summary>
    [TestCase(5.5)]
    [TestCase(4.0)]
    [TestCase(12.0)]
    public void CoarseFramesRetryWithEveryPixel(double arcsecPerPixel) {
        Assert.That(AstapSolver.EscalatedDownsample(2, arcsecPerPixel), Is.EqualTo(1),
            $"a {arcsecPerPixel} arcsec/px nao sobra estrela para binar: "
            + "a retentativa tem de usar todos os pixels");
    }

    [TestCase(1.0)]
    [TestCase(2.75)]
    [TestCase(3.9)]
    public void FinelySampledFramesStillEscalateCoarser(double arcsecPerPixel) {
        Assert.That(AstapSolver.EscalatedDownsample(2, arcsecPerPixel), Is.EqualTo(4),
            "frames bem amostrados continuam ganhando a deteccao mais grosseira do FIELD6-14");
    }

    /// <summary>Scale unknown (0) must not change the established behaviour.</summary>
    [Test]
    public void UnknownScaleKeepsTheOriginalLadder() {
        Assert.That(AstapSolver.EscalatedDownsample(2, 0), Is.EqualTo(4));
        Assert.That(AstapSolver.EscalatedDownsample(0, 0), Is.EqualTo(4));
    }
}
