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

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Some runtime state is derived from per-rig FIELDS, not from which rig is
/// active. Editing such a field used to raise nothing, so the operator could
/// change it in the UI and watch the running session keep the stale value
/// until the next rig switch. ActiveEquipmentProfileEdited closes that gap.
/// </summary>
[TestFixture]
public class ActiveRigEditedEventTests {

    private readonly List<string> _tempDirs = new();

    private ProfileService MakeProfiles() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-rigedit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = dir
            })
            .Build();
        return new ProfileService(cfg, NullLogger<ProfileService>.Instance);
    }

    [TearDown]
    public void Cleanup() {
        foreach (var d in _tempDirs) {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
        _tempDirs.Clear();
    }

    [Test]
    public void EditingTheActiveRig_RaisesTheEvent() {
        var profiles = MakeProfiles();
        var active = profiles.ActiveEquipmentProfile;
        Assert.That(active, Is.Not.Null, "Precondition: a default rig exists.");

        var raised = 0;
        profiles.ActiveEquipmentProfileEdited += _ => raised++;

        var ok = profiles.UpdateEquipmentProfile(active!.Id,
            r => r.AttachedFilter = "Ha");

        Assert.That(ok, Is.True);
        Assert.That(raised, Is.EqualTo(1),
            "Changing a per-rig field on the ACTIVE rig has to notify, "
            + "or the derived runtime state keeps the stale value.");
        Assert.That(profiles.ActiveEquipmentProfile!.AttachedFilter, Is.EqualTo("Ha"));
    }

    [Test]
    public void EditingAnInactiveRig_DoesNotRaiseTheEvent() {
        var profiles = MakeProfiles();
        var other = profiles.CreateEquipmentProfile("Second rig");
        Assert.That(other, Is.Not.Null);
        Assert.That(other!.Id, Is.Not.EqualTo(profiles.ActiveEquipmentProfile!.Id),
            "Precondition: the new rig must not be the active one.");

        var raised = 0;
        profiles.ActiveEquipmentProfileEdited += _ => raised++;

        profiles.UpdateEquipmentProfile(other.Id, r => r.AttachedFilter = "Ha");

        Assert.That(raised, Is.Zero,
            "Editing a rig that is not running must not disturb the live session.");
    }
}
