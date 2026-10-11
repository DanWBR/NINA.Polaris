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

using NINA.Guider.Portable;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The two gates PHD2 runs between finding the star and moving the mount.
/// Expected values come from guider_multistar.cpp (MassChecker,
/// DistanceChecker) and guider.cpp (UpdateCurrentDistance).
/// </summary>
[TestFixture]
public class FrameChecksTests {

    // ----- MassChecker -----

    [Test]
    public void MassChecker_AcceptsEverythingUntilItHasFiveSamples() {
        var mc = new MassChecker(() => 0);
        for (int i = 0; i < 4; i++) {
            Assert.That(mc.CheckMass(1000), Is.False, "no verdict without data");
            mc.AppendData(1000);
        }
    }

    [Test]
    public void MassChecker_RejectsAMassCollapse_AndASpike() {
        long t = 0;
        var mc = new MassChecker(() => t);
        for (int i = 0; i < 8; i++) { mc.AppendData(1000); t += 1000; }

        // Default threshold 0.5: the band is [low * 0.5, high * 1.5] with a
        // spike limit at median * 2.
        Assert.That(mc.CheckMass(1000), Is.False, "the star it has been guiding on");
        Assert.That(mc.CheckMass(400), Is.True, "half the mass is a different object");
        Assert.That(mc.CheckMass(2500), Is.True, "a spike is rejected too");
        Assert.That(mc.CheckMass(1400), Is.False, "ordinary scintillation passes");
    }

    [Test]
    public void MassChecker_ForgetsSamplesOlderThanTwiceTheWindow() {
        long t = 0;
        var mc = new MassChecker(() => t, timeWindowMs: 1000);   // keeps 2000 ms
        for (int i = 0; i < 8; i++) { mc.AppendData(1000); t += 100; }
        Assert.That(mc.CheckMass(400), Is.True);

        // Move well past the window and rebuild the history around a new level.
        t += 10_000;
        mc.Reset();
        for (int i = 0; i < 8; i++) { mc.AppendData(400); t += 100; }
        Assert.That(mc.CheckMass(400), Is.False, "400 is the normal mass now");
    }

    // ----- DistanceChecker -----

    private const double Avg = 1.0;     // smoothed average error, px
    private const int Frames = 50;      // well past MIN_FRAMES_FOR_STATS

    [Test]
    public void DistanceChecker_IsInertWhileGuidingNormally() {
        var dc = new DistanceChecker(() => 0);
        // PHD2 passes an effectively infinite tolerance unless "tolerate jumps"
        // is on, so even a big excursion is guided on.
        Assert.That(dc.CheckDistance(50.0, double.MaxValue, Avg, Frames, measuring: true), Is.True);
        Assert.That(dc.CurrentState, Is.EqualTo(DistanceChecker.State.Guiding));
    }

    [Test]
    public void DistanceChecker_DropsTheFirstWildFrameAfterAStarLoss() {
        long t = 0;
        var dc = new DistanceChecker(() => t);
        dc.Activate();                               // the star was lost
        Assert.That(dc.CurrentState, Is.EqualTo(DistanceChecker.State.Waiting));

        // Forced tolerance 2.0: 3 px against a 1 px average is rejected.
        Assert.That(dc.CheckDistance(3.0, double.MaxValue, Avg, Frames, measuring: true), Is.False);
        // A plausible offset ends the suspicion at once.
        Assert.That(dc.CheckDistance(1.5, double.MaxValue, Avg, Frames, measuring: true), Is.True);
        Assert.That(dc.CurrentState, Is.EqualTo(DistanceChecker.State.Guiding));
    }

    [Test]
    public void DistanceChecker_GivesUpAfterFiveSecondsAndAcceptsTheStarWhereItIs() {
        long t = 0;
        var dc = new DistanceChecker(() => t);
        dc.Activate();

        Assert.That(dc.CheckDistance(9.0, double.MaxValue, Avg, Frames, measuring: true), Is.False);
        t += DistanceChecker.WaitIntervalMs + 1;
        // Timed out: PHD2 stops rejecting and starts guiding it back.
        Assert.That(dc.CheckDistance(9.0, double.MaxValue, Avg, Frames, measuring: true), Is.True);
        Assert.That(dc.CurrentState, Is.EqualTo(DistanceChecker.State.Recovering));
    }

    [Test]
    public void DistanceChecker_JudgesNothingWithoutEnoughFrames() {
        var dc = new DistanceChecker(() => 0);
        dc.Activate();
        Assert.That(dc.CheckDistance(99.0, double.MaxValue, Avg,
                                     DistanceChecker.MinFramesForStats - 1, measuring: true), Is.True);
    }

    [Test]
    public void DistanceChecker_JudgesNothingWhileSettling() {
        var dc = new DistanceChecker(() => 0);
        dc.Activate();
        Assert.That(dc.CheckDistance(99.0, double.MaxValue, Avg, Frames, measuring: false), Is.True);
    }

    // ----- CurrentErrorTracker -----

    [Test]
    public void CurrentError_SeedsWithTheMeanOfTheFirstTenFrames() {
        var t = new CurrentErrorTracker();
        for (int i = 0; i < 9; i++) t.Update(2.0, 1.0);
        Assert.That(t.FrameCount, Is.EqualTo(9));
        Assert.That(t.AvgDistanceLong, Is.EqualTo(2.0).Within(1e-9), "mean of identical samples");
    }

    [Test]
    public void CurrentError_SwitchesToHeavySmoothingAfterTenFrames() {
        var t = new CurrentErrorTracker();
        for (int i = 0; i < 10; i++) t.Update(1.0, 0.5);
        double before = t.AvgDistanceLong;
        t.Update(5.0, 2.5);
        // alpha_long = 0.045, so one big sample barely moves it.
        Assert.That(t.AvgDistanceLong, Is.EqualTo(before + 0.045 * (5.0 - before)).Within(1e-9));
        // while the fast average moves a lot more (alpha 0.3)
        Assert.That(t.AvgDistance, Is.GreaterThan(t.AvgDistanceLong));
    }
}
