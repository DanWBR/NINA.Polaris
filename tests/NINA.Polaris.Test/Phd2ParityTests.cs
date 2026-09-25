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
/// The native guider is meant to behave exactly like PHD2. These tests pin the
/// places where it had drifted: a second min-move applied to the pulse, a pulse
/// capped at the exposure time, and a multi-star combination of our own
/// invention. Every expected value here is computed by hand from the PHD2
/// sources named in the test.
/// </summary>
[TestFixture]
public class Phd2ParityTests {

    // ----- guide_algorithm_hysteresis.cpp -----

    [Test]
    public void Hysteresis_MatchesPhd2FormulaStepByStep() {
        // PHD2: dReturn = (1 - hys) * input + hys * lastMove; dReturn *= aggr;
        // if (|input| < minMove) dReturn = 0; m_lastMove = dReturn.
        var a = new HysteresisAlgorithm(hysteresis: 0.1, aggression: 0.7, minMove: 0.2);

        // lastMove starts at 0: 0.9 * 1.0 * 0.7
        Assert.That(a.Result(1.0), Is.EqualTo(0.63).Within(1e-12));
        // now lastMove = 0.63: (0.9 * 1.0 + 0.1 * 0.63) * 0.7
        Assert.That(a.Result(1.0), Is.EqualTo(0.6741).Within(1e-12));
        // below min move: zero, AND lastMove becomes zero (PHD2 assigns after
        // the cut, which is what makes the next correction start clean)
        Assert.That(a.Result(0.1), Is.EqualTo(0.0).Within(1e-12));
        Assert.That(a.Result(1.0), Is.EqualTo(0.63).Within(1e-12));
    }

    [Test]
    public void Hysteresis_MinMoveGatesTheInputNotTheOutput() {
        // The output of an accepted correction is allowed to be far below
        // min-move. PHD2 still sends it; the pulse path must not drop it.
        var a = new HysteresisAlgorithm(hysteresis: 0.1, aggression: 0.7, minMove: 0.2);
        double r = a.Result(0.25);        // input passes, output 0.1575
        Assert.That(r, Is.EqualTo(0.1575).Within(1e-12));
        Assert.That(System.Math.Abs(r), Is.LessThan(0.2), "output is below min-move by design");
    }

    // ----- mount.cpp MoveOffset + scope.cpp MoveAxis -----

    [Test]
    public void MoveDuration_HasNoMinimum_OnlyTheAxisMaximum() {
        // PHD2: requested = ROUND(|dist| / rate); MoveAxis clamps to the axis
        // Max Duration and guides whenever the result is > 0. There is no
        // minimum duration anywhere in that path.
        // rate 0.01 px/ms, 0.1575 px -> 16 ms, and 16 ms must survive.
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(0.1575, 0.01, 2500), Is.EqualTo(16));
        // 5 px -> 500 ms
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(5.0, 0.01, 2500), Is.EqualTo(500));
        // 100 px -> 10000 ms, clamped by the axis maximum
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(100.0, 0.01, 2500), Is.EqualTo(2500));
        // rounding is to the nearest millisecond, like PHD2's ROUND()
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(0.0149, 0.01, 2500), Is.EqualTo(1));
        // a zero or negative rate cannot produce a pulse
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(5.0, 0.0, 2500), Is.EqualTo(0));
    }

    // ----- guide_algorithm_resistswitch.cpp -----

    [Test]
    public void ResistSwitch_VetoesBelowMinMove_AndAppliesAggressionAfterTheVeto() {
        var a = new ResistSwitchAlgorithm(minMove: 0.2, aggression: 1.0, fastSwitch: true);
        Assert.That(a.Result(0.1), Is.EqualTo(0.0), "below min move");
    }

    [Test]
    public void ResistSwitch_NeedsACompellingHistoryBeforeItReverses() {
        var a = new ResistSwitchAlgorithm(minMove: 0.2, aggression: 1.0, fastSwitch: false);
        // Establish a direction with five consistent errors.
        for (int i = 0; i < 5; i++) a.Result(0.5);
        // One error the other way is not enough to switch: PHD2 vetoes it.
        Assert.That(a.Result(-0.5), Is.EqualTo(0.0), "a single reversal is not compelling");
    }

    [Test]
    public void ResistSwitch_FastSwitchTakesALargeExcursionImmediately() {
        var a = new ResistSwitchAlgorithm(minMove: 0.2, aggression: 1.0, fastSwitch: true);
        for (int i = 0; i < 5; i++) a.Result(0.5);       // settle on the + side
        // > 3 * minMove the other way: PHD2 forces the switch and moves at once.
        double r = a.Result(-1.0);
        Assert.That(r, Is.EqualTo(-1.0).Within(1e-12), "large excursion switches immediately");
    }

    [Test]
    public void ResistSwitch_AggressionScalesTheAcceptedMove() {
        var a = new ResistSwitchAlgorithm(minMove: 0.2, aggression: 0.5, fastSwitch: true);
        for (int i = 0; i < 5; i++) a.Result(0.5);
        Assert.That(a.Result(-1.0), Is.EqualTo(-0.5).Within(1e-12));
    }

    // ----- guider_multistar.cpp -----

    private static void AddStar(ushort[] img, int w, int h, double cx, double cy,
                                double peak, double sigma = 1.8) {
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                double dx = x - cx, dy = y - cy;
                double v = img[y * w + x] + peak * System.Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                img[y * w + x] = (ushort)System.Math.Clamp(v, 0, 65535);
            }
        }
    }

    private static ushort[] Field(int w, int h, params (double x, double y, double peak)[] stars) {
        var img = new ushort[w * h];
        System.Array.Fill(img, (ushort)300);
        foreach (var s in stars) AddStar(img, w, h, s.x, s.y, s.peak);
        return img;
    }

    [Test]
    public void MultiStar_StabilizesBeforeItAveragesAnything() {
        // PHD2 needs more than five primary samples before it trusts its sigma,
        // and refuses to average until then.
        var t = new MultiStarTracker(searchRegion: 12);
        t.Reset(new[] { (32.0, 32.0), (80.0, 40.0) });
        var img = Field(128, 96, (32.5, 32.25, 6000), (80.5, 40.25, 6000));

        for (int i = 0; i < 5; i++) {
            var r = t.Refine(img, 128, 96, 32.5, 32.25, 20.0, 2.0, 0.5, 0.25, allowRefine: true);
            Assert.That(r.Refined, Is.False, "no averaging while stabilizing");
            Assert.That(r.OffsetX, Is.EqualTo(0.5).Within(1e-9), "the primary offset passes through");
        }
        Assert.That(t.Stabilizing, Is.True);
    }

    [Test]
    public void MultiStar_NotAllowedWhileSettling() {
        var t = new MultiStarTracker(searchRegion: 12);
        t.Reset(new[] { (32.0, 32.0), (80.0, 40.0) });
        var img = Field(128, 96, (32.5, 32.0, 6000), (80.5, 40.0, 6000));
        var r = t.Refine(img, 128, 96, 32.5, 32.0, 20.0, 2.0, 0.5, 0.0, allowRefine: false);
        Assert.That(r.Refined, Is.False);
        Assert.That(r.OffsetX, Is.EqualTo(0.5).Within(1e-9));
    }

    /// <summary>Feed the tracker a spread of primary distances and then one
    /// small one. PHD2 leaves the stabilization window only when the current
    /// excursion is within two sigma of what it has seen, so a monotonic ramp
    /// (every sample larger than the last) would keep it stabilizing forever,
    /// which is correct and is why the warm-up alternates.</summary>
    private static void WarmUpToStable(MultiStarTracker t) {
        for (int i = 0; i < 10; i++) {
            double d = (i % 2 == 0) ? 0.1 : 0.9;
            t.Refine(Field(128, 96, (32.0 + d, 32.0 + d / 2, 6000), (80.0 + d, 40.0 + d / 2, 6000)),
                     128, 96, 32.0 + d, 32.0 + d / 2, 20.0, 2.0, d, d / 2, allowRefine: true);
        }
        t.Refine(Field(128, 96, (32.5, 32.25, 6000), (80.5, 40.25, 6000)),
                 128, 96, 32.5, 32.25, 20.0, 2.0, 0.5, 0.25, allowRefine: true);
    }

    [Test]
    public void MultiStar_RefinesOnlyWhenTheAverageIsSmallerThanThePrimaryAlone() {
        var t = new MultiStarTracker(searchRegion: 12);
        t.Reset(new[] { (32.0, 32.0), (80.0, 40.0) });

        WarmUpToStable(t);
        Assert.That(t.Stabilizing, Is.False, "should have stabilized by now");

        // The secondary says the field moved LESS than the primary claims, so
        // the average is smaller and PHD2 takes it.
        var img = Field(128, 96, (32.6, 32.3, 6000), (80.3, 40.15, 6000));
        var refined = t.Refine(img, 128, 96, 32.6, 32.3, 20.0, 2.0, 0.6, 0.3, allowRefine: true);
        Assert.That(refined.Refined, Is.True, "average is smaller, so it is used");
        Assert.That(System.Math.Abs(refined.OffsetX), Is.LessThan(0.6));
    }

    [Test]
    public void MultiStar_KeepsThePrimaryWhenAveragingWouldEnlargeTheError() {
        var t = new MultiStarTracker(searchRegion: 12);
        t.Reset(new[] { (32.0, 32.0), (80.0, 40.0) });
        WarmUpToStable(t);

        // Now the secondary disagrees in the direction that would make the
        // measured error bigger. PHD2 keeps the single-star measurement.
        var img = Field(128, 96, (32.2, 32.1, 6000), (80.8, 40.4, 6000));
        var r = t.Refine(img, 128, 96, 32.2, 32.1, 20.0, 2.0, 0.2, 0.1, allowRefine: true);
        Assert.That(r.Refined, Is.False, "the average was not smaller, so it is discarded");
        Assert.That(r.OffsetX, Is.EqualTo(0.2).Within(1e-9));
        Assert.That(r.OffsetY, Is.EqualTo(0.1).Within(1e-9));
    }

    [Test]
    public void MultiStar_ASingleStarIsNeverRefined() {
        var t = new MultiStarTracker(searchRegion: 12);
        t.Reset(new[] { (32.0, 32.0) });
        var img = Field(128, 96, (32.5, 32.0, 6000));
        var r = t.Refine(img, 128, 96, 32.5, 32.0, 20.0, 2.0, 0.5, 0.0, allowRefine: true);
        Assert.That(r.Refined, Is.False);
        Assert.That(r.UsedCount, Is.EqualTo(1));
    }
}
