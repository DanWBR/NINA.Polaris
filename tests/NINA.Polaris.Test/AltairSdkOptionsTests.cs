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

using NINA.Camera.AltairSdk;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>The native Altair backend gained the controls the ToupTek one has
/// (same SDK under another name). These are the pure parts; the SDK calls
/// around them need the camera.</summary>
[TestFixture]
public class AltairSdkOptionsTests {
    [TestCase(1, 0x01)]
    [TestCase(2, 0x82)]
    [TestCase(3, 0x83)]
    [TestCase(4, 0x84)]
    public void BinningAveragesSoFramesKeepTheir16BitScale(int bin, int option) {
        Assert.That(AltairSdkCamera.BinningOption(bin), Is.EqualTo(option));
    }

    [TestCase(2, 12, 0x42)]   // 12-bit data summed 2x2 is 14-bit: fits
    [TestCase(4, 12, 0x44)]   // 4x4 adds 4 bits: 16, still fits
    [TestCase(2, 16, 0x02)]   // a 16-bit sensor has no room: saturating sum
    [TestCase(3, 14, 0x03)]
    public void SumBinningAddsBitsOnlyWhereTheFrameHasRoom(int bin, int bits, int option) {
        Assert.That(AltairSdkCamera.BinningOption(bin, sum: true, sensorBits: bits), Is.EqualTo(option));
    }

    [Test]
    public void TheOfferedBinsAreOneToFour() {
        Assert.That(AltairSdkCamera.Bins, Is.EqualTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void TheTecLimitRangeIsMaxHighMinLow() {
        // 6.1 V max, 0.5 V min, in 0.1 V.
        Assert.That(AltairSdkCamera.TecLimitRange((61 << 16) | 5), Is.EqualTo((5, 61)));
    }

    [Test]
    public void ACameraWithoutConversionGainOffersNone() {
        Assert.That(AltairSdkCamera.ConversionGainModes(0), Is.Empty);
    }

    [Test]
    public void ConversionGainIsLcgAndHcg() {
        ulong flags = (ulong)Altaircam.eFLAG.FLAG_CG;
        Assert.That(AltairSdkCamera.ConversionGainModes(flags), Is.EqualTo(new[] { "LCG", "HCG" }));
    }

    [Test]
    public void HdrIsTheThirdModeWhereTheSensorHasIt() {
        // The index is the SDK's own OPTION_CG value: 0 LCG, 1 HCG, 2 HDR.
        ulong flags = (ulong)Altaircam.eFLAG.FLAG_CG | (ulong)Altaircam.eFLAG.FLAG_CGHDR;
        Assert.That(AltairSdkCamera.ConversionGainModes(flags), Is.EqualTo(new[] { "LCG", "HCG", "HDR" }));
    }
}
