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
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>File names and calibration folders for a DSLR and for a frame
/// with no filter in the light path. A DSLR dark with no wheel on the scope
/// came out as "..._G_60s_g0_...": a filter that was not there and a gain the
/// camera does not have, where its ISO was 1600.</summary>
[TestFixture]
public class ImageWriterNamingTests {
    private const string DefaultPattern = "{target}_{camera}_{filter}_{exposure}s_g{gain}_{temp}C_{datetime}_{seq}";

    private static IImageData Frame(string filter, double exp, int gain, int iso, double temp = 0,
                                     string target = "Target2", string camera = "GPhoto CCD") {
        var props = new ImageProperties { Width = 10, Height = 10, BitDepth = 16 };
        var meta = new ImageMetaData {
            CreationTime = new DateTime(2026, 10, 15, 19, 33, 15, DateTimeKind.Local),
            Exposure = new ImageMetaData.ExposureInfo { ExposureTime = exp, Filter = filter, ImageType = "DARK" },
            Camera = new ImageMetaData.CameraInfo { Gain = gain, Iso = iso, Name = camera, Temperature = temp },
            Target = new ImageMetaData.TargetInfo { Name = target }
        };
        return new BaseImageData(new ushort[100], props, meta);
    }

    [Test]
    public void ADslrFrame_SaysItsIso_AndNoFilterItDoesNotHave() {
        var name = ImageWriterService.SubstitutePattern(DefaultPattern, Frame("", 60, 0, 1600), 11);
        Assert.That(name, Is.EqualTo("Target2_GPhoto CCD_60s_iso1600_0C_2026-10-15_19-33-15_0011"));
    }

    [Test]
    public void AGainCamera_KeepsItsGain_AndFilter() {
        var name = ImageWriterService.SubstitutePattern(DefaultPattern,
            Frame("Ha", 300, 100, 0, temp: -10, target: "M31", camera: "ZWO ASI2600MM"), 1);
        Assert.That(name, Is.EqualTo("M31_ZWO ASI2600MM_Ha_300s_g100_-10C_2026-10-15_19-33-15_0001"));
    }

    [Test]
    public void AnEmptyFilterLeadingThePattern_TakesTheSeparatorAfterIt() {
        var name = ImageWriterService.SubstitutePattern("{filter}_{target}_{seq}", Frame("", 60, 0, 1600), 3);
        Assert.That(name, Is.EqualTo("Target2_0003"));
    }

    [Test]
    public void TheGainAndIsoTokens_OnADslr() {
        var img = Frame("", 60, 0, 800);
        Assert.Multiple(() => {
            Assert.That(ImageWriterService.SubstitutePattern("{gain}", img, 1), Is.EqualTo("800"));
            Assert.That(ImageWriterService.SubstitutePattern("{iso}", img, 1), Is.EqualTo("800"));
        });
    }

    [Test]
    public void ADslrDark_IsFiledUnderItsIso() {
        var sub = ImageWriterService.BuildSubDir("DARK", Frame("", 60, 0, 1600), new UserProfile(), "SQA55", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("SQA55", "calibration", "GPhoto_CCD", "dark", "60s_iso1600")));
    }

    [Test]
    public void ADslrBias_IsFiledUnderItsIso() {
        var sub = ImageWriterService.BuildSubDir("BIAS", Frame("", 0, 0, 1600), new UserProfile(), "SQA55", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("SQA55", "calibration", "GPhoto_CCD", "bias", "iso1600")));
    }

    [Test]
    public void AGainCameraDark_StaysUnderItsGain() {
        var sub = ImageWriterService.BuildSubDir("DARK", Frame("", 300, 100, 0), new UserProfile(), "R", DateTime.Today);
        Assert.That(sub, Does.EndWith(Path.Combine("dark", "300s_g100")));
    }
}
