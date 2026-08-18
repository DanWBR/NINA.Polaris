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
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>STAGE2 data model: the unified <see cref="EquipmentProfile.Imagers"/>
/// view over main + aux + ExtraImagers, and that only the storage (legacy
/// fields + ExtraImagers) is serialized.</summary>
[TestFixture]
public class ImagerConfigTests {

    [Test]
    public void Imagers_LegacyRig_ProjectsMainAndAux() {
        // A rig with no extra imagers still exposes exactly two: main + aux.
        var rig = new EquipmentProfile();
        Assert.That(rig.Imagers.Count, Is.EqualTo(2));
        Assert.That(rig.Imagers[0].Role, Is.EqualTo("main"));
        Assert.That(rig.Imagers[1].Role, Is.EqualTo("aux"));
    }

    [Test]
    public void Imagers_ProjectsMainFields() {
        var rig = new EquipmentProfile {
            Camera = "cam0", CameraDriver = "asi",
            FocalLengthMm = 530, ApertureMm = 106,
            CameraPixelSizeUm = 3.76, CameraMaxX = 6248, CameraMaxY = 4176, CameraBitDepth = 16,
            TelescopeBrand = "WO", TelescopeModel = "RedCat",
            Focuser = "foc0", FocuserDriver = "asi",
            FilterWheel = "efw0", FilterWheelDriver = "asi",
            FilterNames = new[] { "L", "R", "G", "B" },
        };
        var main = rig.Imagers[0];
        Assert.That(main.DeviceId, Is.EqualTo("cam0"));
        Assert.That(main.Driver, Is.EqualTo("asi"));
        Assert.That(main.Enabled, Is.True);
        Assert.That(main.FocalLengthMm, Is.EqualTo(530));
        Assert.That(main.ApertureMm, Is.EqualTo(106));
        Assert.That(main.MaxX, Is.EqualTo(6248));
        Assert.That(main.TelescopeModel, Is.EqualTo("RedCat"));
        // Each imager carries its own focuser + filter wheel.
        Assert.That(main.Focuser, Is.EqualTo("foc0"));
        Assert.That(main.FilterWheel, Is.EqualTo("efw0"));
        Assert.That(main.FilterNames, Is.EqualTo(new[] { "L", "R", "G", "B" }));
    }

    [Test]
    public void Imagers_ProjectsAuxFields() {
        var rig = new EquipmentProfile {
            AuxCamera = "cam1", AuxCameraDriver = "svbony", AuxEnabled = true,
            AuxFocalLengthMm = 250, AuxApertureMm = 50,
            AuxExposureMs = 30000, AuxGain = 100, AuxBinning = 2,
        };
        var aux = rig.Imagers[1];
        Assert.That(aux.DeviceId, Is.EqualTo("cam1"));
        Assert.That(aux.Driver, Is.EqualTo("svbony"));
        Assert.That(aux.Enabled, Is.True);
        Assert.That(aux.FocalLengthMm, Is.EqualTo(250));
        Assert.That(aux.ExposureMs, Is.EqualTo(30000));
        Assert.That(aux.Gain, Is.EqualTo(100));
        Assert.That(aux.Binning, Is.EqualTo(2));
    }

    [Test]
    public void Imagers_IncludesExtrasWithAssignedRoles() {
        var rig = new EquipmentProfile();
        rig.ExtraImagers.Add(new ImagerConfig { DeviceId = "cam2", Driver = "playerone", ExposureMs = 15000 });
        rig.ExtraImagers.Add(new ImagerConfig { DeviceId = "cam3", Role = "widefield" });

        Assert.That(rig.Imagers.Count, Is.EqualTo(4));
        // Third camera gets an auto-assigned role; an explicit role is kept.
        Assert.That(rig.Imagers[2].Role, Is.EqualTo("imager-3"));
        Assert.That(rig.Imagers[2].DeviceId, Is.EqualTo("cam2"));
        Assert.That(rig.Imagers[3].Role, Is.EqualTo("widefield"));
    }

    [Test]
    public void Serialization_PersistsExtraImagers_NotTheProjection() {
        var rig = new EquipmentProfile();
        rig.ExtraImagers.Add(new ImagerConfig { DeviceId = "cam2", Driver = "asi", FocalLengthMm = 135 });

        var json = JsonSerializer.Serialize(rig);
        // The storage list is persisted…
        Assert.That(json, Does.Contain("ExtraImagers"));
        Assert.That(json, Does.Contain("cam2"));
        // …but the computed unified view is not (would double-store main/aux).
        Assert.That(json, Does.Not.Contain("\"Imagers\""));

        var round = JsonSerializer.Deserialize<EquipmentProfile>(json)!;
        Assert.That(round.ExtraImagers.Count, Is.EqualTo(1));
        Assert.That(round.ExtraImagers[0].DeviceId, Is.EqualTo("cam2"));
        Assert.That(round.ExtraImagers[0].FocalLengthMm, Is.EqualTo(135));
        Assert.That(round.Imagers.Count, Is.EqualTo(3));
    }
}
