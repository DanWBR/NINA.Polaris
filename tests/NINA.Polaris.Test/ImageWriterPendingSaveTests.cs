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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.Image.ImageData;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>The sequencers name a frame's file at capture time and write it
/// behind the capture, so the SD card write (1 to 3 s for a DSLR RAW on a
/// Raspberry Pi) no longer keeps the camera idle. Nothing about the file may
/// change between the two halves.</summary>
[TestFixture]
public class ImageWriterPendingSaveTests {
    private string _dir = null!;
    private ProfileService _profiles = null!;
    private ImageWriterService _writer = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-pending-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Profiles:Directory"] = _dir }).Build();
        _profiles = new ProfileService(config, NullLogger<ProfileService>.Instance);
        _profiles.Active.ImageOutputDir = Path.Combine(_dir, "images");
        var equipment = new EquipmentManager(new IndiClient("localhost", 7624),
            NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        _writer = new ImageWriterService(equipment, _profiles, NullLogger<ImageWriterService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static BaseImageData RawFrame(byte[] raw) {
        var meta = new ImageMetaData();
        meta.Camera.Name = "GPhoto CCD";
        return new BaseImageData(new ushort[4 * 2],
            new ImageProperties { Width = 4, Height = 2, BitDepth = 16 }, meta) {
            RawFileBytes = raw, RawFileExtension = ".arw"
        };
    }

    [Test]
    public void PrepareNamesTheFile_CommitWritesIt_Verbatim() {
        var raw = new byte[] { 0x49, 0x49, 0x2A, 0x00, 7, 8, 9 };
        var saved = new List<string>();
        _writer.ImageSaved += p => saved.Add(p);

        var pending = _writer.PrepareSave(RawFrame(raw), "M31", "LIGHT");

        Assert.That(pending, Is.Not.Null);
        Assert.That(pending!.Path, Does.EndWith(".arw"));
        Assert.That(File.Exists(pending.Path), Is.False, "nothing is written before Commit");
        Assert.That(saved, Is.Empty, "the storage push must not be told about a file that is not there yet");
        Assert.That(_writer.LastWrittenPath, Is.Null);

        var path = pending.Commit();

        Assert.That(path, Is.EqualTo(pending.Path));
        Assert.That(File.ReadAllBytes(path!), Is.EqualTo(raw), "the RAW is the camera's file, byte for byte");
        Assert.That(saved, Is.EqualTo(new[] { path }));
        Assert.That(_writer.LastWrittenPath, Is.EqualTo(path));
    }

    [Test]
    public void CommitIsIdempotent() {
        var pending = _writer.PrepareSave(RawFrame(new byte[] { 1, 2, 3 }), "M31", "LIGHT")!;
        var first = pending.Commit();
        File.WriteAllBytes(first!, new byte[] { 9 });

        Assert.That(pending.Commit(), Is.EqualTo(first));
        Assert.That(File.ReadAllBytes(first!), Is.EqualTo(new byte[] { 9 }), "a second Commit does not write again");
    }

    [Test]
    public void TwoPreparedFrames_GetDistinctNames_BeforeEitherIsWritten() {
        var a = _writer.PrepareSave(RawFrame(new byte[] { 1 }), "M31", "LIGHT")!;
        var b = _writer.PrepareSave(RawFrame(new byte[] { 2 }), "M31", "LIGHT")!;

        Assert.That(a.Path, Is.Not.EqualTo(b.Path), "the frame number is taken at capture time");
        Assert.That(b.Commit(), Is.Not.Null);
        Assert.That(a.Commit(), Is.Not.Null);
        Assert.That(File.ReadAllBytes(a.Path), Is.EqualTo(new byte[] { 1 }));
        Assert.That(File.ReadAllBytes(b.Path), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void NoOutputDir_PreparesNothing_LikeSaveImage() {
        _profiles.Active.ImageOutputDir = "";
        Assert.That(_writer.PrepareSave(RawFrame(new byte[] { 1 }), "M31", "LIGHT"), Is.Null);
        Assert.That(_writer.SaveImage(RawFrame(new byte[] { 1 }), "M31", "LIGHT"), Is.Null);
    }
}
