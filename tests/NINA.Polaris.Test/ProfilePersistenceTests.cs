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
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Regression coverage for the field bug where a default rig vanished after a
/// browser/server restart: a torn active.json reset the profile to empty and
/// the next save overwrote it. Persistence is now atomic (.tmp + replace) with
/// a .bak the loader recovers from, and corrupt files are preserved, never
/// silently discarded.
/// </summary>
[TestFixture]
public class ProfilePersistenceTests {

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-profile-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ProfileService NewService() {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = _dir })
            .Build();
        return new ProfileService(cfg, NullLogger<ProfileService>.Instance);
    }

    [Test]
    public void Save_ThenReload_PreservesRigs() {
        var svc = NewService();
        var rig = svc.CreateEquipmentProfile("Backyard SCT");
        svc.UpdateEquipmentProfile(rig.Id, r => { r.Camera = "ASI2600"; r.FocalLengthMm = 1000; });

        // Fresh instance = simulates an app/browser restart re-reading from disk.
        var reloaded = NewService();
        var rigs = reloaded.ListEquipmentProfiles();

        Assert.That(rigs.Any(r => r.Name == "Backyard SCT"),
            "The rig must survive a reload");
        Assert.That(rigs.First(r => r.Name == "Backyard SCT").Camera, Is.EqualTo("ASI2600"));
    }

    [Test]
    public void Save_WritesBackupOfPreviousVersion() {
        var svc = NewService();
        svc.CreateEquipmentProfile("Rig A");   // first save creates active.json
        svc.CreateEquipmentProfile("Rig B");   // second save backs up the first

        Assert.That(File.Exists(Path.Combine(_dir, "active.json.bak")), Is.True,
            "A .bak of the previous good profile should exist after the second save");
    }

    [Test]
    public void Load_RecoversRigsFromBackup_WhenMainIsCorrupt() {
        var svc = NewService();
        svc.CreateEquipmentProfile("Rig A");
        svc.CreateEquipmentProfile("Travel APO");   // ensures a good .bak exists

        var mainPath = Path.Combine(_dir, "active.json");
        var bakPath = mainPath + ".bak";
        Assert.That(File.Exists(bakPath), Is.True, "precondition: backup exists");

        // Simulate a power-cut torn write: truncate the main file to garbage.
        File.WriteAllText(mainPath, "{ \"name\": \"Defau");   // invalid JSON

        var reloaded = NewService();
        var rigs = reloaded.ListEquipmentProfiles();

        // The backup holds "Rig A" (the state before the "Travel APO" save), so
        // at minimum that rig must come back instead of an empty profile.
        Assert.That(rigs, Is.Not.Empty, "must not reset to an empty profile");
        Assert.That(rigs.Any(r => r.Name == "Rig A"),
            "rigs should be recovered from the backup, not wiped");
    }

    // ----- BACKUP-RESTORE (#637): export/import to a user file -----

    [Test]
    public void ImportEquipmentProfiles_RestoresLostRig_WithoutTouchingOthers() {
        var svc = NewService();
        var a = svc.CreateEquipmentProfile("Rig A");
        var b = svc.CreateEquipmentProfile("Rig B");
        svc.UpdateEquipmentProfile(b.Id, r => r.Camera = "ASI533");
        // Snapshot the set as a "backup file" would capture it.
        var backup = svc.ListEquipmentProfiles();
        var savedB = backup.First(r => r.Id == b.Id);

        // The user loses Rig B.
        Assert.That(svc.DeleteEquipmentProfile(b.Id), Is.True);
        Assert.That(svc.ListEquipmentProfiles().Any(r => r.Id == b.Id), Is.False);

        // Restoring the backup brings B back and leaves A alone.
        var applied = svc.ImportEquipmentProfiles(new[] { savedB }, activeId: null);
        Assert.That(applied, Is.EqualTo(1));
        var rigs = svc.ListEquipmentProfiles();
        Assert.That(rigs.Any(r => r.Id == a.Id), "Rig A must be untouched by the restore");
        var restored = rigs.FirstOrDefault(r => r.Id == b.Id);
        Assert.That(restored, Is.Not.Null, "Rig B must be restored");
        Assert.That(restored!.Camera, Is.EqualTo("ASI533"), "restored rig keeps its settings");

        // And it survives a reload from disk.
        Assert.That(NewService().ListEquipmentProfiles().Any(r => r.Id == b.Id), Is.True);
    }

    [Test]
    public void ImportEquipmentProfiles_MergesByIdInsteadOfDuplicating() {
        var svc = NewService();
        var a = svc.CreateEquipmentProfile("Rig A");
        int before = svc.ListEquipmentProfiles().Count;

        // Re-importing the same Id updates in place, does not add a duplicate.
        var edited = svc.ListEquipmentProfiles().First(r => r.Id == a.Id);
        edited.FocalLengthMm = 2000;
        svc.ImportEquipmentProfiles(new[] { edited }, activeId: a.Id);

        var rigs = svc.ListEquipmentProfiles();
        Assert.That(rigs.Count(r => r.Id == a.Id), Is.EqualTo(1), "no duplicate rig by Id");
        Assert.That(rigs.Count, Is.EqualTo(before));
        Assert.That(rigs.First(r => r.Id == a.Id).FocalLengthMm, Is.EqualTo(2000));
    }

    [Test]
    public void ExportImportProfile_FullRoundTrip_RestoresEverything() {
        var svc = NewService();
        svc.CreateEquipmentProfile("Backyard SCT");
        svc.CreateEquipmentProfile("Travel APO");
        var json = svc.ExportActiveJson();

        // A different, empty install restores from the exported file.
        var otherDir = Path.Combine(Path.GetTempPath(), "polaris-profile-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherDir);
        try {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = otherDir })
                .Build();
            var svc2 = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
            svc2.ImportProfile(json);

            Assert.That(svc2.ListEquipmentProfiles().Any(r => r.Name == "Backyard SCT"));
            Assert.That(svc2.ListEquipmentProfiles().Any(r => r.Name == "Travel APO"));

            // Persisted: a reload of the second install still has them.
            var svc2Reload = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
            Assert.That(svc2Reload.ListEquipmentProfiles().Any(r => r.Name == "Travel APO"), Is.True);
        } finally {
            try { Directory.Delete(otherDir, recursive: true); } catch { }
        }
    }

    [Test]
    public void ImportProfile_RejectsGarbage_WithoutWipingCurrent() {
        var svc = NewService();
        svc.CreateEquipmentProfile("Keep Me");
        Assert.Throws<System.Text.Json.JsonException>(() => svc.ImportProfile("not a profile"));
        Assert.That(svc.ListEquipmentProfiles().Any(r => r.Name == "Keep Me"), Is.True,
            "a bad import file must not destroy the current profile");
    }

    [Test]
    public void Load_PreservesCorruptFile_WhenNoBackup() {
        // Hand-craft a profile dir with only a corrupt main, no .bak.
        var mainPath = Path.Combine(_dir, "active.json");
        File.WriteAllText(mainPath, "totally not json");

        var svc = NewService();   // should not throw, should start fresh

        // The corrupt file must be preserved (copied aside), never destroyed.
        var preserved = Directory.GetFiles(_dir, "active.json.corrupt-*");
        Assert.That(preserved, Is.Not.Empty,
            "the unparseable profile must be preserved for recovery");
        // And the service is usable with a fresh Default rig.
        Assert.That(svc.ListEquipmentProfiles(), Is.Not.Empty);
    }
}
