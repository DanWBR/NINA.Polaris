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
using NINA.Polaris.Services.PlateSolving;

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
}
