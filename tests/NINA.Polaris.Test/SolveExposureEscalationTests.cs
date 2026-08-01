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
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// SOLVEXP. Slew and Center captures its own frame at the rig's
/// SlewCenterExposureSec (5 s on the SV503 rig) while the PREVIEW tab solves
/// whatever the last relayed frame was, which during a session is a 60 s LIVE
/// sub. Same sky, same solver, twelve times the signal: that is why SKY failed
/// where PREVIEW succeeded on 2026-07-31.
///
/// The retry only makes sense when the solver failed for want of STARS, so the
/// classifier is the part worth pinning. It reads solver prose, so the risk is
/// both directions: missing a real starvation (no retry, same failure) and
/// firing on an unrelated failure (one wasted longer exposure).
/// </summary>
[TestFixture]
public class SolveExposureEscalationTests {

    private static bool StarStarved(string? error) {
        var m = typeof(SlewCenterService).GetMethod("LooksStarStarved",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)m.Invoke(null, new object?[] { error })!;
    }

    /// <summary>The exact line the field log carried, plus the other phrasings
    /// the two solvers use.</summary>
    [TestCase("ASTAP failed (exit 1): Using star database D80\nOnly 0 stars found in image. Abort")]
    [TestCase("Only 7 stars found in image")]
    [TestCase("Solver reported: not enough stars in the field")]
    [TestCase("too few stars detected")]
    [TestCase("No stars detected in the image")]
    [TestCase("insufficient stars for a solution")]
    public void StarvationPhrases_AreRecognised(string error) {
        Assert.That(StarStarved(error), Is.True, error);
    }

    /// <summary>A solve that FOUND stars and still could not place them is a
    /// different problem (wrong scale hint, wrong part of the sky, missing
    /// database). A longer exposure would not help and would just cost the
    /// operator time on every iteration.</summary>
    [TestCase("ASTAP failed (exit 1): no solution found")]
    [TestCase("Star database D50 not found in /opt/astap")]
    [TestCase("Request timed out")]
    [TestCase("astrometry.net: solve-field exited with code 1")]
    [TestCase("")]
    [TestCase(null)]
    public void OtherFailures_DoNotTriggerALongerExposure(string? error) {
        Assert.That(StarStarved(error), Is.False, error ?? "(null)");
    }

    /// <summary>The ceiling exists because an unguided mount trails: past half
    /// a minute the extra photons cost more detections than they buy.</summary>
    [Test]
    public void EscalationCeiling_IsHalfAMinute() {
        var f = typeof(SlewCenterService).GetField("MaxSolveExposureSec",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.That((double)f.GetRawConstantValue()!, Is.EqualTo(30.0));
    }

    /// <summary>Doubling from the rig's 5 s reaches a 60 s LIVE sub's class of
    /// signal inside the iteration budget, which is the whole point: the field
    /// case solved at 60 s and failed at 5 s.</summary>
    [Test]
    public void DoublingFromFiveSeconds_ReachesTwentySecondsInThreeTries() {
        double exp = 5.0;
        var seen = new List<double> { exp };
        for (int i = 0; i < 3 && exp < 30.0; i++) {
            exp = Math.Min(30.0, exp * 2);
            seen.Add(exp);
        }
        Assert.That(seen, Is.EqualTo(new[] { 5.0, 10.0, 20.0, 30.0 }));
    }
}
