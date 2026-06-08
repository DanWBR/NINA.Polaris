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
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test.Editor;

/// <summary>
/// Pins the tone-curve spline. Drives the editor's "Curves" panel and
/// the master tone shape; a regression here visibly bends every image
/// shown in the editor, so worth tight pins.
/// </summary>
[TestFixture]
public class ToneCurveTests {

    [Test]
    public void Identity_ReturnsLinearLut() {
        var lut = ToneCurve.Identity();
        for (int i = 0; i < 256; i++) {
            Assert.That(lut[i], Is.EqualTo((byte)i), $"Identity LUT broken at i={i}");
        }
    }

    [Test]
    public void Build_TwoEndpoints_LinearMapping() {
        // Just the endpoints, should produce identity-equivalent LUT.
        var lut = ToneCurve.Build(new[] { (0.0, 0.0), (255.0, 255.0) });
        for (int i = 0; i < 256; i++) {
            Assert.That(lut[i], Is.EqualTo((byte)i).Within(1),
                $"Two-endpoint curve drifted at i={i}");
        }
    }

    [Test]
    public void Build_SCurve_ContrastShape() {
        // Classic S-curve: shadows pulled down, highlights pushed up.
        var lut = ToneCurve.Build(new[] {
            (0.0, 0.0), (64.0, 40.0), (192.0, 215.0), (255.0, 255.0)
        });
        Assert.That(lut[64],  Is.LessThan(64),  "Shadow anchor should pull down");
        Assert.That(lut[192], Is.GreaterThan(192), "Highlight anchor should push up");
        Assert.That(lut[0],   Is.EqualTo(0));
        Assert.That(lut[255], Is.EqualTo(255));
    }

    [Test]
    public void Build_SinglePoint_FallsBackToIdentity() {
        var lut = ToneCurve.Build(new[] { (128.0, 200.0) });
        // Single point insufficient → identity (defensive).
        for (int i = 0; i < 256; i++) Assert.That(lut[i], Is.EqualTo((byte)i));
    }

    [Test]
    public void Build_OutsideRange_HoldsEndpoints() {
        // Spline anchors only cover x=64..192; values outside should hold
        // the endpoint Y rather than extrapolate wildly.
        var lut = ToneCurve.Build(new[] {
            (64.0, 100.0), (192.0, 150.0)
        });
        Assert.That(lut[0],   Is.EqualTo(100));
        Assert.That(lut[255], Is.EqualTo(150));
    }

    [Test]
    public void Build_Monotonic_AnchorsAreMonotonic_StaysReasonable() {
        // Strict monotonic input shouldn't produce wild non-monotonic
        // overshoots in the LUT, natural cubic CAN ring, but slightly.
        var lut = ToneCurve.Build(new[] {
            (0.0, 0.0), (32.0, 20.0), (96.0, 80.0), (160.0, 180.0), (255.0, 255.0)
        });
        // Allow tiny non-monotonicity (±3) which is within spline ringing.
        int worstDrop = 0;
        for (int i = 1; i < 256; i++) {
            int delta = lut[i - 1] - lut[i];
            if (delta > worstDrop) worstDrop = delta;
        }
        Assert.That(worstDrop, Is.LessThanOrEqualTo(3),
            $"Natural cubic spline ringing too large (worst drop {worstDrop})");
    }
}