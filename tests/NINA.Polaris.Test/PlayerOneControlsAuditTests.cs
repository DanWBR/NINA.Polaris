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

using NINA.Camera.PlayerOneSdk;
using NINA.Core.Enum;
using NUnit.Framework;
using static NINA.Camera.PlayerOneSdk.Native.PoaNative;

namespace NINA.Polaris.Test;

/// <summary>The PlayerOne panel listed only the seven configs the enum
/// named. These are the pure parts of listing the rest: what stays out, the
/// flip list, the Bayer tag under mono bin, and negative config values.</summary>
[TestFixture]
public class PlayerOneControlsAuditTests {
    // POAConfig is internal, so the cases pass it as an int.
    [TestCase((int)POAConfig.POA_GUIDE_NORTH)]
    [TestCase((int)POAConfig.POA_GUIDE_SOUTH)]
    [TestCase((int)POAConfig.POA_GUIDE_EAST)]
    [TestCase((int)POAConfig.POA_GUIDE_WEST)]
    [TestCase((int)POAConfig.POA_EXP)]
    [TestCase((int)POAConfig.POA_FLIP_NONE)]
    [TestCase((int)POAConfig.POA_FLIP_BOTH)]
    [TestCase((int)POAConfig.POA_HEATER)]
    public void GuidePulsesDuplicatesAndRawFlipsStayOffThePanel(int cfg) {
        Assert.That(PlayerOneSdkCamera.HiddenFromPanel((POAConfig)cfg), Is.True);
    }

    [TestCase((int)POAConfig.POA_EXPOSURE)]
    [TestCase((int)POAConfig.POA_GAIN)]
    [TestCase((int)POAConfig.POA_WB_R)]
    [TestCase((int)POAConfig.POA_USB_BANDWIDTH_LIMIT)]
    [TestCase((int)POAConfig.POA_FRAME_LIMIT)]
    [TestCase((int)POAConfig.POA_MONO_BIN)]
    [TestCase((int)POAConfig.POA_HEATER_POWER)]
    public void TheRestIsListed(int cfg) {
        Assert.That(PlayerOneSdkCamera.HiddenFromPanel((POAConfig)cfg), Is.False);
    }

    [TestCase(0, (int)POAConfig.POA_FLIP_NONE)]
    [TestCase(1, (int)POAConfig.POA_FLIP_HORI)]
    [TestCase(2, (int)POAConfig.POA_FLIP_VERT)]
    [TestCase(3, (int)POAConfig.POA_FLIP_BOTH)]
    [TestCase(9, (int)POAConfig.POA_FLIP_BOTH)]
    public void FlipChoiceMapsToItsConfig(int index, int expected) {
        Assert.That((int)PlayerOneSdkCamera.FlipConfig(index), Is.EqualTo(expected));
        Assert.That(PlayerOneSdkCamera.FlipOptions, Has.Length.EqualTo(4));
    }

    [TestCase(true, 2, BayerPatternEnum.None)]
    [TestCase(true, 1, BayerPatternEnum.GRBG)]
    [TestCase(false, 2, BayerPatternEnum.GRBG)]
    public void MonoBinDropsTheBayerTagOnlyWhenBinning(bool monoBin, int bin, BayerPatternEnum expected) {
        Assert.That(PlayerOneSdkCamera.EffectiveBayer(BayerPatternEnum.GRBG, monoBin, bin), Is.EqualTo(expected));
    }

    [Test]
    public void NegativeValuesFillTheWhole64BitLong() {
        // White balance runs -1200..1200; a 64-bit Linux C long with only the
        // low half written reads as 4294966096.
        var v = POAConfigValue.Int(-1200);
        Assert.That(v.longValue, Is.EqualTo(-1200L));
        Assert.That(v.intValue, Is.EqualTo(-1200));
    }
}
