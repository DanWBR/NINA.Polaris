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

using System.Text.Json;
using System.Text.Json.Nodes;
using NINA.Polaris.Endpoints;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Off-axis guider mode, and the half-port that made it a dead control.
///
/// An OAG takes its light off the main optical train, so the guide camera
/// images at the MAIN scope's focal length. Everything that derives the
/// guider's pixel scale, its calibration key or its field of view has to read
/// the effective focal length rather than the guide-scope one.
///
/// The preview line carried the checkbox in the interface and none of the
/// backend: no field on the rig, so the tick was dropped on the way to the
/// server, and the guider went on computing its pixel scale from a guide scope
/// that was not in the light path. Nothing failed, nothing was logged, and the
/// only symptom was a pixel scale, and therefore a calibration, quietly wrong
/// by whatever ratio the two focal lengths had.
///
/// These pin the two halves that have to travel together: the rule itself, and
/// the field names the interface binds actually existing on the rig under the
/// spellings the client sends.
/// </summary>
[TestFixture]
public class OagGuideModeTests {

    // The web binder ASP.NET uses for a PUT body: camelCase, case-insensitive.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The rule. Off, the guider images through its own scope; on, it
    /// images through the main one.</summary>
    [Test]
    public void EffectiveFocalLength_FollowsTheToggle() {
        var rig = new EquipmentProfile {
            FocalLengthMm = 1000,
            GuiderFocalLengthMm = 200
        };

        Assert.Multiple(() => {
            rig.GuiderIsOag = false;
            Assert.That(rig.EffectiveGuiderFocalLengthMm, Is.EqualTo(200),
                "a separate guide scope images at its own focal length");
            rig.GuiderIsOag = true;
            Assert.That(rig.EffectiveGuiderFocalLengthMm, Is.EqualTo(1000),
                "an OAG images through the main OTA, so this is the main focal length");
        });
    }

    /// <summary>A guider pixel scale is 206.265 * pixel / focal length, so
    /// taking the wrong focal length scales every measured rate by the ratio of
    /// the two. With a 1000 mm OTA and a 200 mm finder that is a factor of five:
    /// the number the operator reads, and the calibration stored next to it, are
    /// wrong by that much and nothing says so.</summary>
    [Test]
    public void WrongFocalLength_MisscalesThePixelScaleByTheRatio() {
        var rig = new EquipmentProfile {
            FocalLengthMm = 1000,
            GuiderFocalLengthMm = 200,
            GuiderIsOag = true
        };
        const double pixelUm = 3.76;

        var right = 206.265 * pixelUm / rig.EffectiveGuiderFocalLengthMm;
        var wrong = 206.265 * pixelUm / rig.GuiderFocalLengthMm;

        Assert.That(wrong / right, Is.EqualTo(5).Within(1e-9));
        Assert.That(right, Is.EqualTo(0.7756).Within(5e-4));
    }

    /// <summary>The guard against the half-port. These are the exact property
    /// names the optics card binds and app.js sends; a body carrying them has to
    /// arrive on the rig. When the fields are missing from the model the merge
    /// drops them in silence, which is precisely how the checkbox came to do
    /// nothing.</summary>
    [Test]
    public void OpticsBody_CarriesTheOagFieldsOntoTheRig() {
        var stored = new EquipmentProfile {
            Name = "Backyard", FocalLengthMm = 1000, GuiderFocalLengthMm = 200
        };
        var patch = JsonNode.Parse("""
            {
              "guiderIsOag": true,
              "oagOffsetMm": 12.5,
              "oagPositionAngleDeg": 30,
              "guiderCameraMaxX": 1936,
              "guiderCameraMaxY": 1096,
              "guiderCameraPixelSizeUm": 2.9
            }
            """)!.AsObject();

        var merged = RigPatch.Merge(stored, patch);

        Assert.Multiple(() => {
            Assert.That(merged.GuiderIsOag, Is.True, "the checkbox never reached the rig");
            Assert.That(merged.OagOffsetMm, Is.EqualTo(12.5));
            Assert.That(merged.OagPositionAngleDeg, Is.EqualTo(30));
            Assert.That(merged.GuiderCameraMaxX, Is.EqualTo(1936));
            Assert.That(merged.GuiderCameraMaxY, Is.EqualTo(1096));
            Assert.That(merged.GuiderCameraPixelSizeUm, Is.EqualTo(2.9));
            Assert.That(merged.EffectiveGuiderFocalLengthMm, Is.EqualTo(1000),
                "with OAG on, the effective focal length is the main scope's");
        });
    }

    /// <summary>The endpoint assigns these from the merge without a guard, which
    /// is only safe because an omitting body yields the stored value. A partial
    /// PUT from another card must not turn OAG mode off or lose the prism
    /// geometry.</summary>
    [Test]
    public void PartialBody_KeepsTheStoredOagSettings() {
        var stored = new EquipmentProfile {
            Name = "Backyard",
            FocalLengthMm = 1000,
            GuiderIsOag = true,
            OagOffsetMm = 12.5,
            OagPositionAngleDeg = 30,
            GuiderCameraMaxX = 1936,
            GuiderCameraMaxY = 1096,
            GuiderCameraPixelSizeUm = 2.9
        };
        var patch = JsonNode.Parse("""{ "attachedFilter": "Ha" }""")!.AsObject();

        var merged = RigPatch.Merge(stored, patch);

        Assert.Multiple(() => {
            Assert.That(merged.GuiderIsOag, Is.True, "a one-field PUT turned OAG mode off");
            Assert.That(merged.OagOffsetMm, Is.EqualTo(12.5));
            Assert.That(merged.OagPositionAngleDeg, Is.EqualTo(30));
            Assert.That(merged.GuiderCameraPixelSizeUm, Is.EqualTo(2.9));
        });
    }

    /// <summary>An explicit false is a real "OAG off", not an omission. The
    /// endpoint assigns the bool unconditionally and relies on this.</summary>
    [Test]
    public void ExplicitFalse_TurnsOagOff() {
        var stored = new EquipmentProfile { Name = "Backyard", GuiderIsOag = true };
        var patch = JsonNode.Parse("""{ "guiderIsOag": false }""")!.AsObject();

        Assert.That(RigPatch.Merge(stored, patch).GuiderIsOag, Is.False);
    }

    /// <summary>The effective focal length is derived, not stored. Serialising
    /// it would put a read-only property in the body the client PUTs back.</summary>
    [Test]
    public void EffectiveFocalLength_IsNotSerialised() {
        var json = JsonSerializer.Serialize(
            new EquipmentProfile { Name = "Backyard", FocalLengthMm = 1000 }, Web);

        Assert.That(json, Does.Not.Contain("effectiveGuiderFocalLength"));
    }
}
