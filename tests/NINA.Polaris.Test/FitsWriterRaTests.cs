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
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the RA → degrees conversion for the FITS RA / OBJCTRA keywords.
/// The metadata contract is RA in HOURS, so the normal case is hours × 15.
/// A defensive guard catches a mount adapter that mistakenly hands degrees
/// (> 24): without it a value like 84.2° became 1263° in the header and broke
/// plate solving (real field report on an M42 frame).
/// </summary>
[TestFixture]
public class FitsWriterRaTests {
    private string _dir = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "NinaFitsRa_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private double WriteAndReadRa(double telescopeRa) {
        var props = new ImageProperties { Width = 8, Height = 8, BitDepth = 16 };
        var meta = new ImageMetaData();
        meta.Telescope.RightAscension = telescopeRa;
        meta.Telescope.Declination = -5.39;
        var img = new BaseImageData(new ushort[64], props, meta);
        var path = Path.Combine(_dir, "ra.fits");
        FITSWriter.Write(img, path);

        using var fs = File.OpenRead(path);
        var hdr = FITSReader.ReadHeadersOnly(fs);
        Assert.That(hdr.TryGetValue("RA", out var card), Is.True, "RA keyword present");
        return double.Parse(card.Value, CultureInfo.InvariantCulture);
    }

    [Test]
    public void Hours_are_converted_to_degrees() {
        // M42 ≈ 5.588 h → 83.82°
        Assert.That(WriteAndReadRa(5.588), Is.EqualTo(83.82).Within(0.01));
    }

    [Test]
    public void Degrees_value_is_passed_through_not_multiplied() {
        // A mount that wrongly reports degrees (84.2) must NOT become 1263°.
        Assert.That(WriteAndReadRa(84.2), Is.EqualTo(84.2).Within(0.01));
    }

    [Test]
    public void Boundary_24h_still_treated_as_hours() {
        // 24 h is the upper edge of the hours range → 360°, not pass-through.
        Assert.That(WriteAndReadRa(24.0), Is.EqualTo(360.0).Within(0.01));
    }

    [Test]
    public void DateObs_is_written_as_utc_start() {
        // PixInsight/SPCC (and astropy/Siril/ASTAP) read the FITS-standard
        // DATE-OBS for observation time; a frame lacking it was rejected
        // (field report). It must carry the UTC start of the exposure.
        var props = new ImageProperties { Width = 8, Height = 8, BitDepth = 16 };
        var when = new DateTime(2026, 7, 12, 3, 45, 12, 500, DateTimeKind.Utc);
        var meta = new ImageMetaData { CreationTime = when };
        var img = new BaseImageData(new ushort[64], props, meta);
        var path = Path.Combine(_dir, "dateobs.fits");
        FITSWriter.Write(img, path);

        using var fs = File.OpenRead(path);
        var hdr = FITSReader.ReadHeadersOnly(fs);
        Assert.That(hdr.TryGetValue("DATE-OBS", out var card), Is.True, "DATE-OBS keyword present");
        Assert.That(card.Value, Is.EqualTo("2026-07-12T03:45:12.500"));
    }
}
