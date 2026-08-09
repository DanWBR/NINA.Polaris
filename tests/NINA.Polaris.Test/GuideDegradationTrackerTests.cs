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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The advisory half of the wind work: "worse than this session's own normal
/// for a while", which is the case the runaway guard's absolute threshold
/// deliberately does not cover.
/// </summary>
[TestFixture]
public class GuideDegradationTrackerTests {

    private static readonly DateTime T0 = new(2026, 8, 7, 22, 0, 0, DateTimeKind.Utc);

    /// <summary>Feed n frames at a given RMS, one per 1.4s (the cadence in the
    /// real logs). Returns the time reached.</summary>
    private static DateTime Feed(GuideDegradationTracker t, double rms, int n, DateTime from) {
        var now = from;
        for (int i = 0; i < n; i++) {
            t.Push(rms, now);
            now = now.AddSeconds(1.4);
        }
        return now;
    }

    [Test]
    public void WithoutEnoughHistoryThereIsNoNormalToBeWorseThan() {
        var t = new GuideDegradationTracker();

        Feed(t, 0.5, 10, T0);

        Assert.That(t.BaselineArcsec, Is.Null);
        Assert.That(t.Degraded, Is.False);
    }

    [Test]
    public void SteadyGuidingNeverWarns() {
        var t = new GuideDegradationTracker();

        Feed(t, 0.5, 400, T0);

        Assert.That(t.BaselineArcsec, Is.EqualTo(0.5).Within(0.01));
        Assert.That(t.Degraded, Is.False);
    }

    [Test]
    public void ABriefRoughPatchDoesNotWarn() {
        var t = new GuideDegradationTracker();
        var now = Feed(t, 0.5, 200, T0);

        // 30 frames at 4x, about 42 seconds: under the two-minute hold.
        Feed(t, 2.0, 30, now);

        Assert.That(t.Degraded, Is.False,
            "a gust is not a degraded session; the hold is what separates them");
    }

    [Test]
    public void SustainedDegradationWarns() {
        var t = new GuideDegradationTracker();
        var now = Feed(t, 0.5, 200, T0);

        Feed(t, 2.0, 120, now);          // ~168s at 4x normal

        Assert.That(t.Degraded, Is.True);
        Assert.That(t.DegradedSinceUtc, Is.Not.Null);
        Assert.That(t.DegradedSinceUtc, Is.EqualTo(now).Within(TimeSpan.FromSeconds(2)),
            "the spell is dated from when the error went over, not from when the "
            + "warning was raised");
    }

    [Test]
    public void RecoveringClearsTheWarning() {
        var t = new GuideDegradationTracker();
        var now = Feed(t, 0.5, 200, T0);
        now = Feed(t, 2.0, 120, now);
        Assert.That(t.Degraded, Is.True, "precondition");

        Feed(t, 0.5, 5, now);

        Assert.That(t.Degraded, Is.False);
        Assert.That(t.DegradedSinceUtc, Is.Null);
    }

    /// <summary>THE design decision, and the one worth a test.
    ///
    /// A plain trailing median learns from the bad patch as well, so the
    /// baseline climbs to meet the degradation and the ratio stops crossing.
    /// Measured on the SV503 logs, a moving baseline warned for 2 minutes
    /// across the whole windy night; freezing it while degraded gives 81. Here:
    /// half an hour at 4x normal must still be reported as degraded at the end,
    /// not quietly accepted as the new normal.</summary>
    [Test]
    public void ALongBadSpellDoesNotBecomeTheNewNormal() {
        var t = new GuideDegradationTracker();
        var now = Feed(t, 0.5, 200, T0);

        Feed(t, 2.0, 1300, now);          // ~30 minutes at 4x

        Assert.That(t.Degraded, Is.True,
            "the baseline must not have learned its way up to the bad value");
        Assert.That(t.BaselineArcsec, Is.EqualTo(0.5).Within(0.05),
            "and it should still describe the healthy part of the session");
    }

    [Test]
    public void ResetForgetsTheSession() {
        var t = new GuideDegradationTracker();
        var now = Feed(t, 0.5, 200, T0);
        Feed(t, 2.0, 120, now);
        Assert.That(t.Degraded, Is.True, "precondition");

        t.Reset();

        Assert.That(t.Degraded, Is.False);
        Assert.That(t.BaselineArcsec, Is.Null);
    }

    /// <summary>A rig whose normal is already poor should warn on the same
    /// RATIO, not on an absolute number: that is the whole reason this is
    /// relative and the guard is not.</summary>
    [Test]
    public void TheBarIsRelativeToTheRigNotAbsolute() {
        var fine = new GuideDegradationTracker();
        var rough = new GuideDegradationTracker();
        var n1 = Feed(fine, 0.4, 200, T0);
        var n2 = Feed(rough, 3.0, 200, T0);

        Feed(fine, 1.2, 120, n1);        // 3x on a good rig
        Feed(rough, 9.0, 120, n2);       // 3x on a poor one

        Assert.That(fine.Degraded, Is.True);
        Assert.That(rough.Degraded, Is.True);

        // And the poor rig sitting at its own normal says nothing, even though
        // 3 arcsec would look alarming next to the good rig's 0.4.
        var calm = new GuideDegradationTracker();
        Feed(calm, 3.0, 400, T0);
        Assert.That(calm.Degraded, Is.False);
    }
}
