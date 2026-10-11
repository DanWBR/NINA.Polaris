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

using NINA.INDI.Devices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Which element of a camera's capture-format switch means "16-bit raw".
///
/// The rule used to be "the name contains 16 and not RGB", which is true of
/// every astro driver and catastrophic on a DSLR: indi_gphoto publishes the
/// camera's IMAGE QUALITY list under the same property name, with elements
/// FORMAT_1 to FORMAT_18. The old rule matched FORMAT_16, and Polaris wrote it
/// before every exposure, so a Canon R8 was silently switched to its sixteenth
/// image format and shot JPEG all night. The frames came back as 8-bit data in
/// a 16-bit FITS, every value a multiple of 257.
///
/// The caller now skips gphoto cameras outright. These pin the matching itself,
/// because the next driver with an oddly named element will come through here.
/// </summary>
[TestFixture]
public class Raw16FormatPickTests {

    [TestCase("SVB_IMG_RAW16")]       // SVBONY
    [TestCase("ASI_IMG_RAW16")]       // ZWO
    [TestCase("TOUPCAM_RAW16")]       // ToupTek and friends
    [TestCase("RAW 16-bit")]
    [TestCase("raw16")]
    public void TheRealThing_IsPicked(string name) {
        var names = new[] { "SVB_IMG_RAW8", name, "SVB_IMG_RGB24" };

        Assert.That(IndiCamera.PickRaw16Element(names), Is.EqualTo(name));
    }

    /// <summary>The Canon R8 case, verbatim: eighteen opaque entries, one of
    /// them numbered sixteen.</summary>
    [Test]
    public void AgphotoImageQualityList_MatchesNothing() {
        var names = new string[18];
        for (int i = 0; i < names.Length; i++) names[i] = "FORMAT_" + (i + 1);

        Assert.That(IndiCamera.PickRaw16Element(names), Is.Null,
            "FORMAT_16 is the camera's sixteenth image quality, not a bit depth");
    }

    [Test]
    public void AnRgbFormat_IsNeverIt() {
        Assert.That(IndiCamera.PickRaw16Element(new[] { "IMG_RGB16" }), Is.Null);
    }

    [Test]
    public void ADriverWithNoSuchFormat_GetsNull() {
        Assert.That(IndiCamera.PickRaw16Element(new[] { "ASI_IMG_RAW8", "ASI_IMG_Y8" }),
            Is.Null);
    }

    /// <summary>A name that says 16 without saying raw is still better than
    /// nothing for an astro driver, but it must lose to the explicit one.</summary>
    [Test]
    public void AnExplicitRawName_WinsOverABareSixteen() {
        var names = new[] { "MONO16", "CAM_RAW16" };

        Assert.That(IndiCamera.PickRaw16Element(names), Is.EqualTo("CAM_RAW16"));
    }

    [Test]
    public void ABareSixteen_IsTakenWhenItIsAllThereIs() {
        Assert.That(IndiCamera.PickRaw16Element(new[] { "MONO8", "MONO16" }),
            Is.EqualTo("MONO16"));
    }

    /// <summary>INDI's own CCD_CAPTURE_FORMAT names, which indi_toupbase and
    /// other drivers on the standard property use. No bit depth anywhere: it
    /// lives in CCD_INFO.CCD_BITSPERPIXEL. Requiring a "16" meant nothing was
    /// ever picked on a ToupTek, so the camera kept whatever format the driver
    /// or its saved config had, and on a colour AE676C that was RGB: three
    /// planes, 75 MB a frame, debayered by the driver.</summary>
    [Test]
    public void TheIndiStandardRawName_IsPicked() {
        Assert.That(IndiCamera.PickRaw16Element(new[] { "INDI_RGB", "INDI_RAW" }),
            Is.EqualTo("INDI_RAW"));
    }

    /// <summary>A digit in the name is a claim about depth, so an 8-bit raw is
    /// still refused. This is the case that stops the new tier swallowing
    /// everything.</summary>
    [Test]
    public void ABareRawRuleDoesNotPickAnEightBitRaw() {
        Assert.That(IndiCamera.PickRaw16Element(new[] { "ASI_IMG_RAW8", "ASI_IMG_Y8" }),
            Is.Null);
    }

    /// <summary>And an explicit 16 still wins over a name that claims
    /// nothing.</summary>
    [Test]
    public void AnExplicitSixteen_WinsOverABareRaw() {
        Assert.That(IndiCamera.PickRaw16Element(new[] { "INDI_RAW", "CAM_RAW16" }),
            Is.EqualTo("CAM_RAW16"));
    }
}
