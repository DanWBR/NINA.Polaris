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
using NINA.Polaris.Services;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

/// <summary>
/// Verifies the standard folder layout that ImageWriterService.BuildSubDir
/// produces for each IMAGETYP. This is the contract STUDIO relies on for
/// auto-matching calibration frames to lights (darks by exposure+gain,
/// flats by filter+gain), so it has to stay stable across refactors.
/// </summary>
[TestFixture]
public class ImageWriterDirectoryLayoutTests {

    private static IImageData Frame(string filter, double exp, int gain, string target,
                                     string imageType, DateTime creation) {
        var props = new ImageProperties { Width = 100, Height = 100, BitDepth = 16 };
        var meta = new ImageMetaData {
            CreationTime = creation,
            Exposure  = new ImageMetaData.ExposureInfo  { ExposureTime = exp, Filter = filter, ImageType = imageType },
            Camera    = new ImageMetaData.CameraInfo    { Gain = gain },
            Target    = new ImageMetaData.TargetInfo    { Name = target }
        };
        return new BaseImageData(new ushort[100 * 100], props, meta);
    }

    private static UserProfile EmptyProfile() => new();

    private static DateTime Session(int y, int mo, int d) => new(y, mo, d);

    [Test]
    public void Light_GoesUnder_rig_target_lights_session() {
        var img = Frame("Ha", 300, 100, "M31", "LIGHT",
            new DateTime(2026, 5, 21, 22, 30, 0, DateTimeKind.Local));
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "Backyard 130mm APO", Session(2026, 5, 21));
        Assert.That(sub, Is.EqualTo(Path.Combine("Backyard_130mm_APO",
            "M31", "lights", "2026-05-21")),
            "target before kind, so one object's whole night is one folder; and "
            + "no filter level, because the filename and the FITS header both "
            + "carry the filter already");
    }

    [Test]
    public void MultiCamera_InsertsTheCameraNameUnderTheRig() {
        // On a multi-camera rig each camera gets its own tree
        // ({rig}/{camera}/{target}/lights/{session}) so frames never collide.
        var img = Frame("L", 120, 100, "M42", "LIGHT",
            new DateTime(2026, 5, 21, 22, 0, 0, DateTimeKind.Local));
        img.MetaData.Camera.Name = "ASI 2600MC";
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "Dual Rig", Session(2026, 5, 21), multiCamera: true);
        Assert.That(sub, Is.EqualTo(Path.Combine("Dual_Rig",
            "ASI_2600MC", "M42", "lights", "2026-05-21")));
    }

    [Test]
    public void SingleCamera_DoesNotInsertTheCameraName() {
        var img = Frame("L", 120, 100, "M42", "LIGHT",
            new DateTime(2026, 5, 21, 22, 0, 0, DateTimeKind.Local));
        img.MetaData.Camera.Name = "ASI 2600MC";
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "Solo Rig", Session(2026, 5, 21), multiCamera: false);
        Assert.That(sub, Is.EqualTo(Path.Combine("Solo_Rig", "M42", "lights", "2026-05-21")));
    }

    [Test]
    public void Light_WithSpacesInTargetName_NormalisedToUnderscores() {
        var img = Frame("L", 60, 0, "Veil Nebula", "LIGHT",
            new DateTime(2026, 5, 21, 22, 0, 0, DateTimeKind.Local));
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "Default", Session(2026, 5, 21));
        Assert.That(sub, Does.Contain("Veil_Nebula"));
    }

    [Test]
    public void Dark_GoesUnder_rig_calibration_dark_exposure_gain() {
        var img = Frame("", 300, 100, "", "DARK", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("DARK", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "dark", "300s_g100")));
    }

    [Test]
    public void Bias_GoesUnder_rig_calibration_bias_gain() {
        var img = Frame("", 0, 100, "", "BIAS", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("BIAS", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "bias", "g100")));
    }

    [Test]
    public void Flat_GoesUnder_rig_calibration_flat_filterGain() {
        var img = Frame("Ha", 0.5, 100, "", "FLAT", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("FLAT", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "flat", "Ha_g100")));
    }

    [Test]
    public void DarkFlat_GetsItsOwnBucket() {
        var img = Frame("", 0.5, 100, "", "DARKFLAT", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("DARKFLAT", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "darkflat", "0.5s_g100")));
    }

    [Test]
    public void Snap_GoesUnder_rig_snaps_filterDate() {
        // PREVIEW-tab snaps live in their own folder so they don't
        // pollute the science lights from a sequence. Layout:
        //   {rig}/snaps/{filter}_{yyyy-MM-dd}/
        var img = Frame("L", 2.0, 100, "snap", "SNAP", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("SNAP", img, EmptyProfile(),
            "MyRig", Session(2026, 5, 21));
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "snaps", "L_2026-05-21")));
    }

    [Test]
    public void Snap_WithNoFilter_FallsBackToL() {
        // Same default-fallback rule as Light/Flat: empty filter
        // becomes "L" so the path always has a stable segment.
        var img = Frame("", 1.0, 0, "snap", "SNAP", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("SNAP", img, EmptyProfile(),
            "MyRig", Session(2026, 5, 21));
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "snaps", "L_2026-05-21")));
    }

    [Test]
    public void Aux_GoesUnder_rig_target_aux_session() {
        // Auxiliary-camera frames sit beside the main camera's lights under the
        // same target, in their own aux/ folder so the two never mix.
        var img = Frame("L", 120, 0, "M31", "AUX",
            new DateTime(2026, 5, 21, 22, 30, 0, DateTimeKind.Local));
        var sub = ImageWriterService.BuildSubDir("AUX", img, EmptyProfile(),
            "MyRig", Session(2026, 5, 21));
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "M31", "aux", "2026-05-21")));
    }

    [Test]
    public void UnknownTarget_FallsBackTo_Unknown() {
        var img = Frame("L", 60, 0, "", "LIGHT", new DateTime(2026, 5, 21));
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "MyRig", Session(2026, 5, 21));
        Assert.That(sub, Does.Contain("Unknown"));
    }

    [Test]
    public void FractionalExposure_FormattedWithoutTrailingZeros() {
        var img = Frame("L", 0.25, 100, "", "DARK", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("DARK", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "dark", "0.25s_g100")));
    }

    [Test]
    public void EmptyRigName_FallsBackTo_Default() {
        var img = Frame("L", 60, 0, "M42", "LIGHT", new DateTime(2026, 5, 21));
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "", Session(2026, 5, 21));
        Assert.That(sub.StartsWith("Default"), Is.True,
            "Empty rig should default to 'Default'");
    }

    // --- SessionDateForLocal: noon-to-noon convention ---

    [Test]
    public void SessionDate_PastNoon_IsCurrentDate() {
        // 22:30 local on May 21 → still May 21 (the evening just started)
        var local = new DateTime(2026, 5, 21, 22, 30, 0, DateTimeKind.Local);
        Assert.That(ImageWriterService.SessionDateForLocal(local),
            Is.EqualTo(new DateTime(2026, 5, 21)));
    }

    [Test]
    public void SessionDate_BeforeNoon_IsPreviousDate() {
        // 03:00 local on May 22 → belongs to the May 21 night
        var local = new DateTime(2026, 5, 22, 3, 0, 0, DateTimeKind.Local);
        Assert.That(ImageWriterService.SessionDateForLocal(local),
            Is.EqualTo(new DateTime(2026, 5, 21)));
    }

    [Test]
    public void SessionDate_AtExactlyNoon_RollsForward() {
        // 12:00 is the start of the new session in our convention
        var local = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Local);
        Assert.That(ImageWriterService.SessionDateForLocal(local),
            Is.EqualTo(new DateTime(2026, 5, 22)));
    }
}