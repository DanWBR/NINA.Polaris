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

using NINA.Camera.ZwoSdk;
using NINA.Core.Enum;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>The pure parts of the ZWO control panel audit: how a control is
/// typed for the UI, and the Bayer tag of a mono-binned frame.</summary>
[TestFixture]
public class ZwoControlsAuditTests {
    [Test]
    public void FlipIsAListOfItsFourStates() {
        var (vt, options) = AsiSdkCamera.ControlKind(AsiSdkCamera.AsiFlip, 0, 3);
        Assert.That(vt, Is.EqualTo("enum"));
        Assert.That(options, Is.EqualTo(new[] { "None", "Horizontal", "Vertical", "Both" }));
    }

    [Test]
    public void FlipWithARangeItCannotNameStaysANumber() {
        Assert.That(AsiSdkCamera.ControlKind(AsiSdkCamera.AsiFlip, 0, 7).ValueType, Is.EqualTo("int"));
    }

    [TestCase(AsiSdkCamera.AsiMonoBin, 0, 1, "bool")]
    [TestCase(0, 0, 600, "int")]
    [TestCase(6, 40, 100, "int")]
    public void OtherControlsAreToggleOrNumber(int type, double min, double max, string expected) {
        var (vt, options) = AsiSdkCamera.ControlKind(type, min, max);
        Assert.That(vt, Is.EqualTo(expected));
        Assert.That(options, Is.Null);
    }

    [TestCase(true, 2, BayerPatternEnum.None)]
    [TestCase(true, 1, BayerPatternEnum.RGGB)]
    [TestCase(false, 2, BayerPatternEnum.RGGB)]
    public void MonoBinDropsTheBayerTagOnlyWhenBinning(bool monoBin, int bin, BayerPatternEnum expected) {
        Assert.That(AsiSdkCamera.EffectiveBayer(BayerPatternEnum.RGGB, monoBin, bin), Is.EqualTo(expected));
    }
}
