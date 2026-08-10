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
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Test;

/// <summary>
/// Field geometry for solve hints, pinned against a real rig.
///
/// The numbers come from an Orange Pi 5 Pro session on 2026-08-10: ASI585MC
/// (3840x2160, 2.9um) behind an SV550 at 1366mm in the profile. ASTAP solved
/// the frame and reported FOV=0.28d, scale=0.5"/px, FL=1268mm, so the arithmetic
/// here has an independent check from the solver itself.
///
/// This exists because a solve with no scale hint searches every plate scale:
/// on that rig, a thin frame took 18s to fail without -fov and 1s to solve with
/// it.
/// </summary>
[TestFixture]
public class PlateSolveHintsTests {

    // The rig as configured.
    private const double FocalMm = 1366.0;
    private const double PixelUm = 2.9;
    private const int FrameH = 2160;
    private const long SensorH = 2160;

    [Test]
    public void From_TheFieldRig_MatchesWhatTheSolverMeasured() {
        var g = PlateSolveHints.From(FocalMm, PixelUm, FrameH, SensorH);

        // 206.2648 * 2.9 / 1366 = 0.4378"/px
        Assert.That(g.ScaleArcsecPerPixel, Is.EqualTo(0.438).Within(0.005));
        // 2160 * 2.9um = 6.264mm tall; 2*atan(6.264/2732) = 0.263 deg
        Assert.That(g.FovDeg, Is.EqualTo(0.263).Within(0.005));
        Assert.That(g.NativeBinning, Is.EqualTo(1));
    }

    /// <summary>ASTAP measured the true focal length as 1268mm against a
    /// configured 1366mm, and still solved: the hint has to be in the right
    /// neighbourhood, not exact. This pins that the disagreement stays inside
    /// the tolerance a solver copes with, so a slightly stale profile does not
    /// turn a working hint into a harmful one.</summary>
    [Test]
    public void From_AnEightPercentFocalError_MovesTheHintByLessThanTenPercent() {
        var configured = PlateSolveHints.From(1366.0, PixelUm, FrameH, SensorH);
        var measured = PlateSolveHints.From(1268.0, PixelUm, FrameH, SensorH);

        var ratio = measured.FovDeg / configured.FovDeg;
        Assert.That(ratio, Is.EqualTo(1366.0 / 1268.0).Within(0.01),
            "FOV scales with 1/focal length");
        Assert.That(measured.FovDeg, Is.EqualTo(0.283).Within(0.005),
            "should land on the 0.28 deg ASTAP reported");
    }

    /// <summary>
    /// THE ONE THAT BROKE A NIGHT. A reduced frame keeps the sensor's native
    /// pixel pitch in its metadata, so pairing that pitch with the reduced
    /// height halves the FOV and the scale at once. A live stack at 1:2 broke
    /// solving for an hour in the field while the same sky solved instantly at
    /// 1:1.
    /// </summary>
    [Test]
    public void From_AHalfResFrame_UsesTheEffectivePixelPitch() {
        var full = PlateSolveHints.From(FocalMm, PixelUm, FrameH, SensorH);
        var half = PlateSolveHints.From(FocalMm, PixelUm, FrameH / 2, SensorH);

        Assert.That(half.NativeBinning, Is.EqualTo(2), "a 2x reduction must be detected");
        Assert.That(half.EffectivePixelUm, Is.EqualTo(2 * PixelUm).Within(1e-9));
        Assert.That(half.ScaleArcsecPerPixel, Is.EqualTo(2 * full.ScaleArcsecPerPixel).Within(1e-6),
            "half the samples across the same sky means twice the arcsec per pixel");
        Assert.That(half.FovDeg, Is.EqualTo(full.FovDeg).Within(1e-4),
            "the FIELD does not change when the frame is reduced; only the sampling does");
    }

    [Test]
    public void From_AQuarterResFrame_IsAlsoDetected() {
        var q = PlateSolveHints.From(FocalMm, PixelUm, FrameH / 4, SensorH);
        Assert.That(q.NativeBinning, Is.EqualTo(4));
        Assert.That(q.EffectivePixelUm, Is.EqualTo(4 * PixelUm).Within(1e-9));
    }

    /// <summary>A crop is not a reduction: the pitch in the metadata is already
    /// right and must not be multiplied, or the hint goes out by the crop
    /// ratio.</summary>
    [Test]
    public void From_ACroppedFrame_LeavesThePitchAlone() {
        var c = PlateSolveHints.From(FocalMm, PixelUm, 1500, SensorH);   // 1.44x, not integer
        Assert.That(c.NativeBinning, Is.EqualTo(1));
        Assert.That(c.EffectivePixelUm, Is.EqualTo(PixelUm).Within(1e-9));
    }

    [Test]
    public void From_WithoutAFocalLength_YieldsNoHintRatherThanNonsense(
            [Values(0.0, -1.0)] double focal) {
        var g = PlateSolveHints.From(focal, PixelUm, FrameH, SensorH);
        Assert.That(g.FovDeg, Is.Zero);
        Assert.That(g.ScaleArcsecPerPixel, Is.Zero);
    }

    [Test]
    public void Apply_FillsTheGapsButNeverOverridesTheCaller() {
        var g = PlateSolveHints.From(FocalMm, PixelUm, FrameH, SensorH);

        var empty = new PlateSolveOptions();
        PlateSolveHints.Apply(empty, g);
        Assert.That(empty.FovDeg, Is.EqualTo(g.FovDeg).Within(1e-9));
        Assert.That(empty.ScaleArcsecPerPixel, Is.EqualTo(g.ScaleArcsecPerPixel).Within(1e-9));

        var explicitOpts = new PlateSolveOptions { FovDeg = 1.5, ScaleArcsecPerPixel = 2.0 };
        PlateSolveHints.Apply(explicitOpts, g);
        Assert.That(explicitOpts.FovDeg, Is.EqualTo(1.5), "an explicit request must win");
        Assert.That(explicitOpts.ScaleArcsecPerPixel, Is.EqualTo(2.0));
    }

    [Test]
    public void Apply_WithNoGeometry_LeavesTheOptionsUntouched() {
        var o = new PlateSolveOptions();
        PlateSolveHints.Apply(o, PlateSolveHints.From(0, 0, 0, 0));
        Assert.That(o.FovDeg, Is.Zero);
        Assert.That(o.ScaleArcsecPerPixel, Is.Zero);
    }
}
