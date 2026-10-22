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

using System.Globalization;
using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.FileFormat.XISF;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

/// <summary>
/// Issue #45: "RA != 0 || Dec != 0" stood for "the mount position is known",
/// so a frame taken at RA 0h, Dec 0° lost its coordinates. Known and unknown
/// are now an explicit flag on the metadata.
/// </summary>
[TestFixture]
public class MetadataCoordinatesTests {
    private string _dir = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "NinaMetaCoords_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static BaseImageData Image(ImageMetaData meta)
        => new(new ushort[64], new ImageProperties { Width = 8, Height = 8, BitDepth = 16 }, meta);

    private Dictionary<string, FITSHeaderCard> WriteFits(ImageMetaData meta) {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".fits");
        FITSWriter.Write(Image(meta), path);
        using var fs = File.OpenRead(path);
        return FITSReader.ReadHeadersOnly(fs);
    }

    private string WriteXisf(ImageMetaData meta) {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xisf");
        XISFWriter.Write(Image(meta), path, compress: false);
        return System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
    }

    private static double Value(Dictionary<string, FITSHeaderCard> hdr, string key)
        => double.Parse(hdr[key].Value, CultureInfo.InvariantCulture);

    [Test]
    public void AMountAtRaZeroDecZero_IsWritten() {
        var meta = new ImageMetaData();
        meta.Telescope.RightAscension = 0;
        meta.Telescope.Declination = 0;

        var hdr = WriteFits(meta);
        Assert.Multiple(() => {
            Assert.That(Value(hdr, "RA"), Is.EqualTo(0));
            Assert.That(Value(hdr, "DEC"), Is.EqualTo(0));
            Assert.That(hdr.ContainsKey("EQUINOX"), Is.True);
        });
        var xml = WriteXisf(meta);
        Assert.That(xml, Does.Contain("name=\"RA\"").And.Contain("name=\"DEC\""));
    }

    [Test]
    public void AnUnknownMountPosition_IsNotWritten() {
        var meta = new ImageMetaData();
        Assert.That(meta.Telescope.HasCoordinates, Is.False);

        var hdr = WriteFits(meta);
        Assert.That(hdr.ContainsKey("RA") || hdr.ContainsKey("DEC"), Is.False);
        Assert.That(WriteXisf(meta), Does.Not.Contain("name=\"RA\""));
    }

    [Test]
    public void ATargetWithoutCoordinates_KeepsItsNameOnly() {
        var meta = new ImageMetaData();
        meta.Target.Name = "M31";

        var hdr = WriteFits(meta);
        Assert.That(hdr.ContainsKey("OBJECT"), Is.True);
        Assert.That(hdr.ContainsKey("OBJCTRA") || hdr.ContainsKey("OBJCTDEC"), Is.False);
        Assert.That(WriteXisf(meta), Does.Not.Contain("OBJCTRA"));
    }

    [Test]
    public void ATargetAtRaZeroDecZero_IsWritten() {
        var meta = new ImageMetaData();
        meta.Target.Name = "Field";
        meta.Target.RightAscension = 0;
        meta.Target.Declination = 0;

        var hdr = WriteFits(meta);
        Assert.That(Value(hdr, "OBJCTRA"), Is.EqualTo(0));
        Assert.That(Value(hdr, "OBJCTDEC"), Is.EqualTo(0));
    }

    /// <summary>The reader used to keep the header's degrees in the hours
    /// field, so a read-then-write (calibration, Studio) multiplied a small RA
    /// by 15 again.</summary>
    [Test]
    public void ReadingAFits_KeepsTheMountPositionInHours() {
        var meta = new ImageMetaData();
        meta.Telescope.RightAscension = 1.5;
        meta.Telescope.Declination = -12.25;
        var path = Path.Combine(_dir, "roundtrip.fits");
        FITSWriter.Write(Image(meta), path);

        BaseImageData read;
        using (var fs = File.OpenRead(path)) read = FITSReader.Read(fs);
        Assert.Multiple(() => {
            Assert.That(read.MetaData.Telescope.HasCoordinates, Is.True);
            Assert.That(read.MetaData.Telescope.RightAscension, Is.EqualTo(1.5).Within(1e-6));
            Assert.That(read.MetaData.Telescope.Declination, Is.EqualTo(-12.25).Within(1e-6));
        });

        var again = Path.Combine(_dir, "again.fits");
        FITSWriter.Write(read, again);
        using var fs2 = File.OpenRead(again);
        Assert.That(Value(FITSReader.ReadHeadersOnly(fs2), "RA"), Is.EqualTo(22.5).Within(1e-6));
    }

    [Test]
    public void AFitsWithoutRaDec_ReadsAsUnknown() {
        var path = Path.Combine(_dir, "nopos.fits");
        FITSWriter.Write(Image(new ImageMetaData()), path);
        using var fs = File.OpenRead(path);
        Assert.That(FITSReader.Read(fs).MetaData.Telescope.HasCoordinates, Is.False);
    }
}
