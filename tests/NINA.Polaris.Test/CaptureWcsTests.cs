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

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.INDI.Client;
using NINA.Image.FileFormat.FITS;
using NINA.Image.FileFormat.XISF;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

[TestFixture]
public class CaptureWcsTests {
    private string _dir = null!;
    private ProfileService _profiles = null!;
    private EquipmentManager _equipment = null!;
    private Mock<ICamera> _camera = null!;
    private Mock<ITelescope> _mount = null!;
    private PlateSolveService _solver = null!;
    private ImageWriterService _writer = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-wcs-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Profiles:Directory"] = _dir }).Build();
        _profiles = new ProfileService(config, NullLogger<ProfileService>.Instance);
        _profiles.Active.ImageOutputDir = Path.Combine(_dir, "images");
        _equipment = new EquipmentManager(new IndiClient("localhost", 7624),
            NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        _camera = new Mock<ICamera>();
        _camera.SetupGet(c => c.IsConnected).Returns(true);
        _camera.SetupGet(c => c.DeviceName).Returns("Main");
        _camera.SetupGet(c => c.Capabilities).Returns(new CameraCapabilities(true, true, false, false, false));
        _camera.SetupGet(c => c.MaxX).Returns(40);
        _camera.SetupGet(c => c.MaxY).Returns(30);
        _camera.SetupGet(c => c.PixelSizeX).Returns(3.76);
        _camera.SetupGet(c => c.PixelSizeY).Returns(3.76);
        _mount = new Mock<ITelescope>();
        _mount.SetupGet(m => m.IsConnected).Returns(true);
        _mount.SetupGet(m => m.RightAscension).Returns(22.0);
        _mount.SetupGet(m => m.Declination).Returns(47.0);
        SetEquipment("Camera", _camera.Object);
        SetEquipment("Telescope", _mount.Object);
        _solver = new PlateSolveService(config, NullLogger<PlateSolveService>.Instance);
        _writer = new ImageWriterService(_equipment, _profiles,
            NullLogger<ImageWriterService>.Instance, plateSolve: _solver);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private void SetEquipment(string property, object value)
        => typeof(EquipmentManager).GetProperty(property)!.SetValue(_equipment, value);

    private static BaseImageData Frame(int binX = 1, int binY = 1, WcsInfo? wcs = null) {
        int w = 40 / binX, h = 30 / binY;
        var meta = new ImageMetaData();
        meta.Camera.Name = "Main";
        meta.Camera.BinX = (short)binX;
        meta.Camera.BinY = (short)binY;
        return new BaseImageData(Enumerable.Range(0, w * h).Select(i => (ushort)i).ToArray(),
            new ImageProperties { Width = w, Height = h, BitDepth = 16, Wcs = wcs }, meta);
    }

    private static PlateSolveResult Result() => new() {
        Success = true, RaHours = 5, DecDeg = -10,
        // Positive determinant deliberately exercises parity that a scalar
        // scale/rotation reconstruction would lose.
        CD11 = 0.0003, CD12 = -0.0004, CD21 = 0.0004, CD22 = 0.0003
    };

    private void Calibrate(BaseImageData? frame = null) {
        var context = CaptureWcsContext.Create(_equipment, _profiles, frame ?? Frame());
        Assert.That(context, Is.Not.Null);
        _solver.RecordSolve(Result(), context);
    }

    [TestCase(1, 1)]
    [TestCase(2, 3)]
    public void SaveLight_WritesNumericWcsWithCurrentJ2000PointingAndBinning(int binX, int binY) {
        Calibrate();
        var image = Frame(binX, binY);
        var path = _writer.SaveImage(image, "Test", "LIGHT");
        Assert.That(path, Is.Not.Null);
        using var stream = File.OpenRead(path!);
        var headers = FITSReader.ReadHeadersOnly(stream);
        var wcs = WcsHeaders.Read(headers)!;
        Assert.Multiple(() => {
            Assert.That(wcs.RaDeg, Is.EqualTo(330));
            Assert.That(wcs.DecDeg, Is.EqualTo(47));
            Assert.That(wcs.RefPixelX, Is.EqualTo((image.Properties.Width + 1) / 2.0));
            Assert.That(wcs.RefPixelY, Is.EqualTo((image.Properties.Height + 1) / 2.0));
            Assert.That(wcs.CD11, Is.EqualTo(0.0003 * binX).Within(1e-10));
            Assert.That(wcs.CD12, Is.EqualTo(-0.0004 * binY).Within(1e-10));
            Assert.That(wcs.CD21, Is.EqualTo(0.0004 * binX).Within(1e-10));
            Assert.That(wcs.CD22, Is.EqualTo(0.0003 * binY).Within(1e-10));
            Assert.That(headers["RADESYS"].Value, Is.EqualTo("FK5"));
            Assert.That(image.Properties.Wcs, Is.Null, "Saving must not mutate the preview frame");
        });
        var bytes = File.ReadAllBytes(path!);
        var cards = Enumerable.Range(0, bytes.Length / 80)
            .Select(i => Encoding.ASCII.GetString(bytes, i * 80, 80)).TakeWhile(c => !c.StartsWith("END ")).ToArray();
        foreach (string key in new[] { "CRVAL1", "CRVAL2", "CRPIX1", "CRPIX2", "CD1_1", "CD1_2", "CD2_1", "CD2_2", "EQUINOX" }) {
            var card = cards.Single(c => c.StartsWith(key.PadRight(8) + "="));
            Assert.That(card[10..].TrimStart(), Does.Not.StartWith("'"), key + " must be numeric");
        }
        stream.Position = 0;
        var read = FITSReader.Read(stream);
        Assert.That(read.Data, Is.EqualTo(image.Data));
    }

    [Test]
    public void BinnedSolve_ScalesBackToUnbinnedFrame() {
        Calibrate(Frame(2, 2));
        var output = _writer.WithCaptureWcs(Frame(), "LIGHT", false, null);
        Assert.That(output.Properties.Wcs!.CD11, Is.EqualTo(0.00015).Within(1e-12));
    }

    [TestCase("DARK")]
    [TestCase("BIAS")]
    [TestCase("FLAT")]
    [TestCase("DARKFLAT")]
    [TestCase("AUX")]
    public void NonScienceFrames_DoNotInheritMainCameraGeometry(string type) {
        Calibrate();
        var image = Frame();
        Assert.That(_writer.WithCaptureWcs(image, type, false, null), Is.SameAs(image));
    }

    [Test]
    public void NoSolve_StackedAndAuxOptics_DoNotInventWcs() {
        var image = Frame();
        Assert.That(_writer.WithCaptureWcs(image, "LIGHT", false, null), Is.SameAs(image));
        Calibrate();
        Assert.That(_writer.WithCaptureWcs(image, "LIGHT", true, null), Is.SameAs(image));
        Assert.That(_writer.WithCaptureWcs(image, "LIGHT", false, 200), Is.SameAs(image));
    }

    [Test]
    public void SolvingImportedImage_DoesNotReplaceCaptureGeometry() {
        Calibrate();
        var original = _solver.CaptureCalibration;
        _solver.RecordSolve(new PlateSolveResult { Success = true, RotationDeg = 80 }, null);
        Assert.That(_solver.CaptureCalibration, Is.SameAs(original));
    }

    [TestCase("camera")]
    [TestCase("mount")]
    [TestCase("focal")]
    [TestCase("pier")]
    [TestCase("disconnected")]
    [TestCase("slewing")]
    [TestCase("invalid-pointing")]
    public void ChangedSetupOrUnavailableMount_DoesNotReuseWcs(string change) {
        Calibrate();
        switch (change) {
            case "camera": SetEquipment("Camera", new Mock<ICamera>().Object); break;
            case "mount": SetEquipment("Telescope", new Mock<ITelescope>().Object); break;
            case "focal": _profiles.ActiveEquipmentProfile!.FocalLengthMm += 100; break;
            case "pier": _mount.SetupGet(m => m.SideOfPier).Returns((PierSide)123); break;
            case "disconnected": _mount.SetupGet(m => m.IsConnected).Returns(false); break;
            case "slewing": _mount.SetupGet(m => m.IsSlewing).Returns(true); break;
            case "invalid-pointing": _mount.SetupGet(m => m.RightAscension).Returns(double.NaN); break;
        }
        Assert.That(_writer.WithCaptureWcs(Frame(), "LIGHT", false, null).Properties.Wcs, Is.Null);
    }

    [Test]
    public void RotatorMove_RequiresNewSolve() {
        var rotator = new Mock<IRotator>();
        rotator.SetupGet(r => r.IsConnected).Returns(true);
        rotator.SetupGet(r => r.Position).Returns(0);
        SetEquipment("Rotator", rotator.Object);
        Calibrate();
        rotator.SetupGet(r => r.Position).Returns(30);
        Assert.That(_writer.WithCaptureWcs(Frame(), "LIGHT", false, null).Properties.Wcs, Is.Null);
        Calibrate();
        Assert.That(_writer.WithCaptureWcs(Frame(), "LIGHT", false, null).Properties.Wcs, Is.Not.Null);
    }

    [Test]
    public void CroppedFrameAndOtherCamera_CannotSeedCaptureCalibration() {
        var cropped = new BaseImageData(new ushort[20 * 15],
            new ImageProperties { Width = 20, Height = 15, BitDepth = 16 }, Frame().MetaData);
        Assert.That(CaptureWcsContext.Create(_equipment, _profiles, cropped), Is.Null);
        var aux = Frame();
        aux.MetaData.Camera.Name = "Auxiliary";
        Assert.That(CaptureWcsContext.Create(_equipment, _profiles, aux), Is.Null);
    }

    [Test]
    public void PreviewVerticalFlip_IsUndoneForOriginalCameraPixels() {
        var context = CaptureWcsContext.Create(_equipment, _profiles, Frame(), verticallyFlipped: true)!;
        _solver.RecordSolve(Result(), context);
        var wcs = _writer.WithCaptureWcs(Frame(), "LIGHT", false, null).Properties.Wcs!;
        Assert.Multiple(() => {
            Assert.That(wcs.CD11, Is.EqualTo(0.0003));
            Assert.That(wcs.CD12, Is.EqualTo(0.0004));
            Assert.That(wcs.CD21, Is.EqualTo(0.0004));
            Assert.That(wcs.CD22, Is.EqualTo(-0.0003));
        });
    }

    [Test]
    public void CalibrationMetadata_OverridesDefaultLightArgument() {
        Calibrate();
        var frame = Frame();
        frame.MetaData.Exposure.ImageType = "Dark Frame";
        Assert.That(_writer.WithCaptureWcs(frame, "LIGHT", false, null), Is.SameAs(frame));
    }

    [Test]
    public void ScalarSolverResult_UsesMeasuredScaleAndRotation() {
        var context = CaptureWcsContext.Create(_equipment, _profiles, Frame())!;
        _solver.RecordSolve(new PlateSolveResult {
            Success = true, RaHours = 0, DecDeg = 0,
            ScaleArcsecPerPixel = 3.6, RotationDeg = 90
        }, context);
        var wcs = _writer.WithCaptureWcs(Frame(), "SNAP", false, null).Properties.Wcs!;
        Assert.That(wcs.CD12, Is.EqualTo(0.001).Within(1e-12));
        Assert.That(wcs.CD21, Is.EqualTo(0.001).Within(1e-12));
    }

    [Test]
    public void InvalidSolverMatrix_IsRejected() {
        var result = Result();
        result.CD11 = double.NaN;
        var context = CaptureWcsContext.Create(_equipment, _profiles, Frame())!;
        Assert.That(CaptureWcsCalibration.FromSolve(context, result), Is.Null);
    }

    [Test]
    public void ExistingWcs_IsPreservedByFitsAndXisf() {
        Calibrate();
        var wcs = WcsHeaders.FromSolveResult(10, -20, 2, 35, 40, 30);
        var frame = Frame(wcs: wcs);
        Assert.That(_writer.WithCaptureWcs(frame, "LIGHT", false, null), Is.SameAs(frame));
        using var fits = new MemoryStream();
        FITSWriter.Write(frame, fits);
        fits.Position = 0;
        Assert.That(FITSReader.Read(fits).Properties.Wcs!.RaDeg, Is.EqualTo(10));
        using var xisf = new MemoryStream();
        XISFWriter.Write(frame, xisf, compress: false);
        var bytes = xisf.ToArray();
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes, 16, length).TrimEnd(' ', '\0'));
        var keywords = xml.Descendants().Where(e => e.Name.LocalName == "FITSKeyword")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Attribute("value")!.Value);
        Assert.That(double.Parse(keywords["CRVAL1"], CultureInfo.InvariantCulture), Is.EqualTo(10));
        Assert.That(keywords["CTYPE1"], Is.EqualTo("RA---TAN"));
    }
}
