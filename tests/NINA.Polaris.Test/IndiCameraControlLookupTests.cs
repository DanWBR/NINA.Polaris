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
using NINA.INDI.Devices;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// How Polaris finds gain and offset on an INDI camera. There is no standard:
/// indi_asi_ccd names the elements Gain and Offset inside CCD_CONTROLS, other
/// drivers prefix them and keep the plain word in the label, others give each
/// control a property of its own. Matching on the element name alone made a
/// miss look like "this camera has no offset", so the write was skipped in
/// silence and the FITS carried no OFFSET card even though the driver was
/// running at one (issue #26, ToupTek ATR2600C).
/// </summary>
[TestFixture]
public class IndiCameraControlLookupTests {

    private static IndiNumberProperty Vector(params (string Name, string Label, double Value)[] els) {
        var p = new IndiNumberProperty { Device = "Cam", Name = "CCD_CONTROLS" };
        foreach (var e in els)
            p.Values[e.Name] = new IndiNumberElement { Label = e.Label, Value = e.Value, Min = 0, Max = 1000 };
        return p;
    }

    /// <summary>The ASI shape, which must keep working exactly as before.</summary>
    [Test]
    public void ExactName_Matches() {
        var v = Vector(("Gain", "Gain", 100), ("Offset", "Offset", 50));
        var hit = IndiCamera.FindControlElement(v, "Offset");
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Value.Key, Is.EqualTo("Offset"));
        Assert.That(hit.Value.Element.Value, Is.EqualTo(50));
    }

    [TestCase("offset")]
    [TestCase("OFFSET")]
    public void NameCasing_IsTolerated(string name) {
        var v = Vector((name, "Offset", 125));
        Assert.That(IndiCamera.FindControlElement(v, "Offset")?.Element.Value, Is.EqualTo(125));
    }

    /// <summary>A prefixed element with the plain word in the label. This is
    /// the shape that made the offset invisible: the control panel says
    /// "Offset", so the operator reasonably reports it as such.</summary>
    [Test]
    public void PrefixedName_FoundByItsLabel() {
        var v = Vector(("TC_GAIN", "Gain", 200), ("TC_OFFSET", "Offset", 125));
        var hit = IndiCamera.FindControlElement(v, "Offset");
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Value.Key, Is.EqualTo("TC_OFFSET"), "the write has to use the driver's own name");
        Assert.That(hit.Value.Element.Value, Is.EqualTo(125));
    }

    /// <summary>And by the name's suffix when the label is something else
    /// entirely (a localised driver, or a label with units).</summary>
    [Test]
    public void PrefixedName_FoundBySuffixWhenTheLabelDiffers() {
        var v = Vector(("TC_OFFSET", "Pedestal (ADU)", 30));
        Assert.That(IndiCamera.FindControlElement(v, "Offset")?.Key, Is.EqualTo("TC_OFFSET"));
    }

    [Test]
    public void ExactNameWins_OverALabelOnAnotherElement() {
        // A vector carrying both must resolve to the spec-named element, so no
        // camera that works today changes behaviour.
        var v = Vector(("SOMETHING_OFFSET", "Offset", 999), ("Offset", "Bias", 50));
        Assert.That(IndiCamera.FindControlElement(v, "Offset")?.Element.Value, Is.EqualTo(50));
    }

    [Test]
    public void NoOffsetElement_IsAMiss() {
        var v = Vector(("Gain", "Gain", 100), ("WB_R", "White balance R", 50));
        Assert.That(IndiCamera.FindControlElement(v, "Offset"), Is.Null);
    }

    [Test]
    public void MissingOrEmptyVector_IsAMiss() {
        Assert.That(IndiCamera.FindControlElement(null, "Offset"), Is.Null);
        Assert.That(IndiCamera.FindControlElement(Vector(), "Offset"), Is.Null);
    }

    /// <summary>Gain goes through the same rule, because the same drivers
    /// prefix both and a silently skipped gain write is worse than a skipped
    /// offset one.</summary>
    [Test]
    public void GainFollowsTheSameRule() {
        var v = Vector(("TC_GAIN", "Gain", 200));
        Assert.That(IndiCamera.FindControlElement(v, "Gain")?.Element.Value, Is.EqualTo(200));
    }

    /// <summary>A label with different padding or casing still counts: driver
    /// labels are written for humans, not for parsers.</summary>
    [TestCase(" Offset ")]
    [TestCase("offset")]
    public void LabelMatch_IgnoresCaseAndPadding(string label) {
        var v = Vector(("X1", label, 77));
        Assert.That(IndiCamera.FindControlElement(v, "Offset")?.Element.Value, Is.EqualTo(77));
    }
}
