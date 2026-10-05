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
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// One offset per rig, and what happens to the four that came before it.
///
/// LIVE, PREVIEW, AUTORUN and ADV each carried their own, which read as
/// flexibility and worked as a trap: an operator typed a pedestal into one
/// panel and got a different one out of a capture started from another, which
/// is most of what issue #26 turned out to be. The field now belongs to the
/// rig, next to gain.
///
/// An install that already had a pedestal in one of the retired fields must
/// keep it. Reverting someone's sensor to the driver's value on the next
/// session, silently, is exactly the surprise this is meant to end.
/// </summary>
[TestFixture]
public class OffsetConsolidationTests {

    private readonly List<string> _tempDirs = new();

    private string NewProfileDir() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-offset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static ProfileService Open(string dir) {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = dir
            }).Build();
        return new ProfileService(cfg, NullLogger<ProfileService>.Instance);
    }

    /// <summary>Write a rig the way an older release would have left it, then
    /// open it the way the app does on start.</summary>
    private ProfileService WithLegacyRig(int? rigOffset, int? preview, int? autorun, int? adv) {
        var dir = NewProfileDir();
        var seed = Open(dir);
        var id = seed.ActiveEquipmentProfile!.Id;
        seed.UpdateEquipmentProfile(id, r => {
            r.DefaultOffset = rigOffset;
            r.PreviewOffset = preview;
            r.AutorunOffset = autorun;
            r.AdvOffset = adv;
        });
        return Open(dir);   // a fresh load, which is where migrations run
    }

    [TearDown]
    public void Cleanup() {
        foreach (var d in _tempDirs) {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
        _tempDirs.Clear();
    }

    [Test]
    public void APedestalOnlyInAutorun_SurvivesTheUpgrade() {
        var profiles = WithLegacyRig(rigOffset: 0, preview: null, autorun: 64, adv: null);

        Assert.That(profiles.ActiveEquipmentProfile!.DefaultOffset, Is.EqualTo(64),
            "the operator had 64 on their running sequences; taking it away is a "
            + "silent change to how the sensor runs");
    }

    [Test]
    public void APedestalOnlyInPreview_SurvivesToo() {
        var profiles = WithLegacyRig(rigOffset: null, preview: 30, autorun: null, adv: null);

        Assert.That(profiles.ActiveEquipmentProfile!.DefaultOffset, Is.EqualTo(30));
    }

    /// <summary>AUTORUN wins, because it is the one that governed real imaging
    /// runs while the others governed framing snaps.</summary>
    [Test]
    public void WhenSeveralDisagree_AutorunWins() {
        var profiles = WithLegacyRig(rigOffset: 0, preview: 30, autorun: 64, adv: 10);

        Assert.That(profiles.ActiveEquipmentProfile!.DefaultOffset, Is.EqualTo(64));
    }

    [Test]
    public void AnExistingRigOffset_IsNotOverwritten() {
        var profiles = WithLegacyRig(rigOffset: 77, preview: 30, autorun: 64, adv: 10);

        Assert.That(profiles.ActiveEquipmentProfile!.DefaultOffset, Is.EqualTo(77),
            "the rig's own field is the one the operator sees now; a retired "
            + "field must not reach over it");
    }

    /// <summary>Zero is a value with a meaning: leave the driver alone. It is
    /// not a hole for a legacy number to fall into when the rig's own field
    /// already says zero and the others say zero too.</summary>
    [Test]
    public void AllZero_StaysZero() {
        var profiles = WithLegacyRig(rigOffset: 0, preview: 0, autorun: 0, adv: 0);

        Assert.That(profiles.ActiveEquipmentProfile!.DefaultOffset ?? 0, Is.EqualTo(0));
    }

    [Test]
    public void TheRetiredFieldsAreClearedSoTheMigrationRunsOnce() {
        var profiles = WithLegacyRig(rigOffset: 0, preview: 30, autorun: 64, adv: 10);
        var rig = profiles.ActiveEquipmentProfile!;

        Assert.Multiple(() => {
            Assert.That(rig.PreviewOffset, Is.Null);
            Assert.That(rig.AutorunOffset, Is.Null);
            Assert.That(rig.AdvOffset, Is.Null);
        });
    }

    /// <summary>And the clearing is on disk, not just in memory: a migration
    /// that forgets to save runs again next start, and would then reach over a
    /// value the operator has since changed.</summary>
    [Test]
    public void TheMigrationIsPersisted() {
        var dir = NewProfileDir();
        var seed = Open(dir);
        var id = seed.ActiveEquipmentProfile!.Id;
        seed.UpdateEquipmentProfile(id, r => { r.DefaultOffset = 0; r.AutorunOffset = 64; });

        Open(dir);                       // migrates and saves
        var reopened = Open(dir);        // nothing left to migrate

        Assert.Multiple(() => {
            Assert.That(reopened.ActiveEquipmentProfile!.DefaultOffset, Is.EqualTo(64));
            Assert.That(reopened.ActiveEquipmentProfile!.AutorunOffset, Is.Null);
        });
    }

    /// <summary>The capture paths read one place now. This is the property the
    /// consolidation exists for: whatever panel a frame was started from, the
    /// pedestal is the rig's.</summary>
    [Test]
    public void EveryCapturePathResolvesTheSameOffset() {
        var dir = NewProfileDir();
        var profiles = Open(dir);
        profiles.UpdateEquipmentProfile(profiles.ActiveEquipmentProfile!.Id,
            r => r.DefaultOffset = 55);

        Assert.That(RigCaptureDefaults.Offset(profiles), Is.EqualTo(55));
    }

    [Test]
    public void ZeroMeansTheDriverKeepsItsOwn() {
        var dir = NewProfileDir();
        var profiles = Open(dir);
        profiles.UpdateEquipmentProfile(profiles.ActiveEquipmentProfile!.Id,
            r => r.DefaultOffset = 0);

        Assert.That(RigCaptureDefaults.Offset(profiles), Is.Null,
            "null is the contract for 'write nothing to the driver'");
    }
}
