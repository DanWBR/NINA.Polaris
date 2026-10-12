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

using NINA.Camera.QhySdk;
using NINA.Core.Enum;
using NUnit.Framework;
using Area = NINA.Camera.QhySdk.QhySdkCamera.QhyArea;

namespace NINA.Polaris.Test;

/// <summary>
/// The parts of the QHY native backend that need no camera: the Bayer
/// mapping, the 8-to-16-bit widening, the frame geometry handed to
/// SetQHYCCDResolution and the readout buffer size. The SDK calls around them
/// can only be checked against hardware.
/// </summary>
[TestFixture]
public class QhySdkCameraTests {

    // ----- Bayer -----

    [TestCase(1u, BayerPatternEnum.GBRG)]   // BAYER_GB
    [TestCase(2u, BayerPatternEnum.GRBG)]   // BAYER_GR
    [TestCase(3u, BayerPatternEnum.BGGR)]   // BAYER_BG
    [TestCase(4u, BayerPatternEnum.RGGB)]   // BAYER_RG
    public void BayerIdsMapToTheirLayout(uint id, BayerPatternEnum expected) {
        Assert.That(QhySdkCamera.MapBayer(id), Is.EqualTo(expected));
    }

    [TestCase(0u)]            // QHYCCD_SUCCESS: "available", but no layout
    [TestCase(5u)]
    [TestCase(0xFFFFFFFFu)]   // QHYCCD_ERROR: what a mono sensor answers
    public void AnythingElseIsMono(uint id) {
        Assert.That(QhySdkCamera.MapBayer(id), Is.EqualTo(BayerPatternEnum.None));
    }

    // ----- Pixel conversion -----

    [Test]
    public void EightBitIsShiftedIntoTheHighByte() {
        var px = QhySdkCamera.Widen8To16(new byte[] { 0, 1, 128, 255, 99 }, 4);
        Assert.That(px, Is.EqualTo(new ushort[] { 0, 256, 32768, 65280 }));
    }

    [Test]
    public void SixteenBitIsCopiedLittleEndian() {
        var px = QhySdkCamera.Copy16(new byte[] { 0x34, 0x12, 0xFF, 0xFF, 0x01, 0x00, 0xAA }, 3);
        Assert.That(px, Is.EqualTo(new ushort[] { 0x1234, 0xFFFF, 0x0001 }));
    }

    // ----- Geometry -----

    private static readonly Area Chip = new(0, 0, 6280, 4210);

    [Test]
    public void NoRoiIsTheWholeSensor() {
        Assert.That(QhySdkCamera.ComputeResolution(0, 0, 0, 0, 1, Chip),
            Is.EqualTo(new Area(0, 0, 6280, 4210)));
    }

    [Test]
    public void BinningDividesOriginAndSize() {
        // SetQHYCCDResolution takes binned pixels.
        Assert.That(QhySdkCamera.ComputeResolution(0, 0, 0, 0, 2, Chip),
            Is.EqualTo(new Area(0, 0, 3140, 2104)));
        Assert.That(QhySdkCamera.ComputeResolution(1000, 600, 2000, 1200, 2, Chip),
            Is.EqualTo(new Area(500, 300, 1000, 600)));
        Assert.That(QhySdkCamera.ComputeResolution(0, 0, 0, 0, 3, Chip),
            Is.EqualTo(new Area(0, 0, 2092, 1402)));
    }

    [Test]
    public void TheEffectiveAreaOriginIsAddedBeforeBinning() {
        // A sensor with overscan: ROI (0,0) is the first light-sensitive pixel,
        // which sits at (24, 12) in the chip image the SDK addresses.
        var eff = new Area(24, 12, 6000, 4000);
        Assert.That(QhySdkCamera.ComputeResolution(0, 0, 0, 0, 1, eff),
            Is.EqualTo(new Area(24, 12, 6000, 4000)));
        Assert.That(QhySdkCamera.ComputeResolution(100, 50, 400, 300, 2, eff),
            Is.EqualTo(new Area(62, 31, 200, 150)));
    }

    [Test]
    public void AnOddOriginIsMadeEvenToKeepTheBayerPhase() {
        Assert.That(QhySdkCamera.ComputeResolution(101, 51, 400, 300, 1, Chip),
            Is.EqualTo(new Area(100, 50, 400, 300)));
    }

    [Test]
    public void ARoiPastTheEdgeIsClampedIntoTheSensor() {
        var f = QhySdkCamera.ComputeResolution(6000, 4000, 1000, 1000, 1, Chip);
        Assert.That(f, Is.EqualTo(new Area(6000, 4000, 280, 210)));
        Assert.That(f.X + f.W, Is.LessThanOrEqualTo(Chip.W));
        Assert.That(f.Y + f.H, Is.LessThanOrEqualTo(Chip.H));
    }

    [Test]
    public void SizeIsEvenAndNeverBelowTwo() {
        Assert.That(QhySdkCamera.ComputeResolution(0, 0, 101, 77, 1, Chip),
            Is.EqualTo(new Area(0, 0, 100, 76)));
        Assert.That(QhySdkCamera.ComputeResolution(0, 0, 3, 3, 4, Chip),
            Is.EqualTo(new Area(0, 0, 2, 2)));
    }

    [Test]
    public void TheEffectiveAreaIsUsedOnlyWhenItFitsTheChip() {
        Assert.That(QhySdkCamera.PickSensorArea(6280, 4210, true, 24, 12, 6000, 4000),
            Is.EqualTo(new Area(24, 12, 6000, 4000)));
        // Call failed, empty, or pokes outside the chip image: the whole chip.
        Assert.That(QhySdkCamera.PickSensorArea(6280, 4210, false, 24, 12, 6000, 4000),
            Is.EqualTo(Chip));
        Assert.That(QhySdkCamera.PickSensorArea(6280, 4210, true, 0, 0, 0, 0),
            Is.EqualTo(Chip));
        Assert.That(QhySdkCamera.PickSensorArea(6280, 4210, true, 300, 0, 6000, 4210),
            Is.EqualTo(Chip));
    }

    // ----- Readout buffer -----

    [Test]
    public void TheBufferIsNeverSmallerThanWhatTheSdkAsksFor() {
        long full = 6280L * 4210 * 2;
        Assert.That(QhySdkCamera.BufferBytes(80_000_000, 6280, 4210), Is.EqualTo(80_000_000));
        Assert.That(QhySdkCamera.BufferBytes(1000, 6280, 4210), Is.EqualTo(full));
        // GetQHYCCDMemLength failed: fall back to the full chip at 16 bits.
        Assert.That(QhySdkCamera.BufferBytes(0xFFFFFFFF, 6280, 4210), Is.EqualTo(full));
        Assert.That(QhySdkCamera.BufferBytes(0, 6280, 4210), Is.EqualTo(full));
    }

    // ----- Return codes and ids -----

    [Test]
    public void NegativeCodesAreFailuresAndTheInformationalOnesAreNot() {
        Assert.That(QhySdkCamera.IsFailure(0xFFFFFFFF), Is.True);       // QHYCCD_ERROR
        Assert.That(QhySdkCamera.IsFailure(unchecked((uint)-3)), Is.True); // DEVICELOSE
        Assert.That(QhySdkCamera.IsFailure(0), Is.False);
        Assert.That(QhySdkCamera.IsFailure(0x2001), Is.False);          // READ_DIRECTLY
        Assert.That(QhySdkCamera.IsParamError(4294967295.0), Is.True);
        Assert.That(QhySdkCamera.IsParamError(-12.5), Is.False);
    }

    [TestCase("QHY600M-6bb54f4c1d2e3f40", "QHY600M")]
    [TestCase("QHY5LII-M-6077c4f1", "QHY5LII-M")]
    [TestCase("QHY268C", "QHY268C")]
    public void TheModelFallbackCutsTheSerialOff(string id, string model) {
        Assert.That(QhyRegistry.ModelFromId(id), Is.EqualTo(model));
    }

    [Test]
    public void AnUnconnectedCameraIsNamedAfterItsId() {
        var cam = new QhySdkCamera("QHY600M-6bb54f4c1d2e3f40");
        Assert.That(cam.DeviceName, Is.EqualTo("QHY600M"));
        Assert.That(cam.IsConnected, Is.False);
        Assert.That(cam.IsColorSensor, Is.Null);
        Assert.That(cam.Capabilities.SupportsVideoStream, Is.False);
    }
}
