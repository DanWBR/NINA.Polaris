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
/// The turn instruction for a hand-turned rotator. The operator is the loop
/// here (measure, turn, measure again), so the direction word is the product:
/// a wrong one sends them the long way round and the next measurement is
/// worse than the last.
/// </summary>
[TestFixture]
public class ManualRotatorAdviceTests {

    // ----- how far -----

    /// <summary>A rectangular sensor turned by 180 degrees frames the same
    /// field, so the answer is always the short way round. Nobody should be
    /// told to turn a camera 170 degrees by hand when 10 the other way gives
    /// the same picture.</summary>
    [TestCase(10.0, 0.0, 10.0)]
    [TestCase(0.0, 10.0, -10.0)]
    [TestCase(170.0, 0.0, -10.0)]
    [TestCase(0.0, 170.0, 10.0)]
    [TestCase(200.0, 30.0, -10.0)]
    [TestCase(90.0, 0.0, 90.0)]
    public void SkyDeltaDeg_TakesTheShortWayRound(double target, double solved, double expected) {
        Assert.That(ManualRotatorAdvice.SkyDeltaDeg(target, solved), Is.EqualTo(expected).Within(1e-9));
    }

    [Test]
    public void Compute_TurnIsTheMagnitudeOfTheDelta() {
        var a = ManualRotatorAdvice.Compute(targetPa: 25.0, solvedPa: 10.0);
        Assert.That(a.DeltaDeg, Is.EqualTo(15.0).Within(1e-9));
        Assert.That(a.TurnDeg, Is.EqualTo(15.0).Within(1e-9));
        Assert.That(a.WithinTolerance, Is.False);
    }

    // ----- which way -----

    /// <summary>Looking at the back of the camera the sky has north up and
    /// east left, and position angle runs north towards east, so it grows
    /// anti-clockwise.</summary>
    [Test]
    public void Compute_UnmirroredField_GrowsAnticlockwise() {
        var up = ManualRotatorAdvice.Compute(30.0, 10.0);   // needs +20
        Assert.That(up.Direction, Is.EqualTo("ccw"));
        var down = ManualRotatorAdvice.Compute(10.0, 30.0); // needs -20
        Assert.That(down.Direction, Is.EqualTo("cw"));
    }

    /// <summary>A star diagonal puts east on the right, and the sense of the
    /// turn goes with it. The CD matrix carries that parity; a positive
    /// determinant is the mirrored case.</summary>
    [Test]
    public void Compute_MirroredField_ReversesTheDirection() {
        // CD for a normal frame (north up, east left): negative determinant.
        var normal = ManualRotatorAdvice.Compute(30.0, 10.0, -1e-4, 0, 0, 1e-4);
        Assert.That(normal.Mirrored, Is.False);
        Assert.That(normal.Direction, Is.EqualTo("ccw"));

        // Flip one axis: positive determinant, mirrored.
        var mirrored = ManualRotatorAdvice.Compute(30.0, 10.0, 1e-4, 0, 0, 1e-4);
        Assert.That(mirrored.Mirrored, Is.True);
        Assert.That(mirrored.Direction, Is.EqualTo("cw"));
    }

    [Test]
    public void IsMirrored_NeedsTheWholeMatrix() {
        // A solver that reports no CD matrix must not be guessed at: treat it
        // as unmirrored and let the rig's reverse flag correct it.
        Assert.That(ManualRotatorAdvice.IsMirrored(null, null, null, null), Is.False);
        Assert.That(ManualRotatorAdvice.IsMirrored(1e-4, 0, 0, null), Is.False);
        Assert.That(ManualRotatorAdvice.IsMirrored(1e-4, 0, 0, 1e-4), Is.True);
        Assert.That(ManualRotatorAdvice.IsMirrored(-1e-4, 0, 0, 1e-4), Is.False);
    }

    /// <summary>The per-rig override is the last word: it exists because
    /// parity cannot cover which side of the camera the operator stands on,
    /// and the UI flips it when a turn made the error grow.</summary>
    [Test]
    public void Compute_ReverseFlipsWhateverTheParitySaid() {
        var plain = ManualRotatorAdvice.Compute(30.0, 10.0, reverse: false);
        var flipped = ManualRotatorAdvice.Compute(30.0, 10.0, reverse: true);
        Assert.That(plain.Direction, Is.EqualTo("ccw"));
        Assert.That(flipped.Direction, Is.EqualTo("cw"));
        Assert.That(flipped.Reversed, Is.True);
        Assert.That(flipped.TurnDeg, Is.EqualTo(plain.TurnDeg),
            "reversing changes the direction, never the distance");
    }

    [Test]
    public void Compute_MirroredAndReversed_CancelOut() {
        var a = ManualRotatorAdvice.Compute(30.0, 10.0, 1e-4, 0, 0, 1e-4, reverse: true);
        Assert.That(a.Direction, Is.EqualTo("ccw"));
    }

    // ----- when to stop -----

    [Test]
    public void Compute_InsideTolerance_AsksForNoTurn() {
        var a = ManualRotatorAdvice.Compute(10.4, 10.0, toleranceDeg: 1.0);
        Assert.That(a.WithinTolerance, Is.True);
        Assert.That(a.Direction, Is.EqualTo("none"),
            "a direction here would have the operator chasing noise");
    }

    [Test]
    public void Compute_OnTheToleranceEdge_CountsAsDone() {
        var a = ManualRotatorAdvice.Compute(11.0, 10.0, toleranceDeg: 1.0);
        Assert.That(a.WithinTolerance, Is.True);
    }

    [TestCase(0.0)]
    [TestCase(-5.0)]
    public void Compute_NonsenseTolerance_FallsBackToTheDefault(double tol) {
        // 0 would make every measurement "not there yet" forever.
        var a = ManualRotatorAdvice.Compute(10.2, 10.0, toleranceDeg: tol);
        Assert.That(a.WithinTolerance, Is.True);
    }

    [Test]
    public void Compute_CarriesBothAnglesBack() {
        var a = ManualRotatorAdvice.Compute(123.4, 100.0);
        Assert.That(a.TargetPa, Is.EqualTo(123.4).Within(1e-9));
        Assert.That(a.SolvedPa, Is.EqualTo(100.0).Within(1e-9));
    }

    /// <summary>Same fold as the motorised path, so the two never disagree
    /// about how far off the framing is.</summary>
    [Test]
    public void SkyDeltaDeg_AgreesWithTheRotatorLoop() {
        foreach (var (t, s) in new[] { (10.0, 0.0), (200.0, 30.0), (0.0, 179.0), (45.0, 315.0) }) {
            Assert.That(ManualRotatorAdvice.SkyDeltaDeg(t, s),
                Is.EqualTo(SlewCenterService.RotationErrorDeg(t, s)).Within(1e-9));
        }
    }
}
