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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Endpoints;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// RIGPUT-1: a PARTIAL rig PUT must not reset the per-rig value-type fields to
/// model defaults. Two callers send a one-field body (a LIVE compute-mode toggle,
/// a slew-safety patch); the handler used to write gain/offset/binning/cooler
/// unconditionally, so those partial saves silently zeroed them — the offset→0
/// case clipped the SV405CC to black.
///
/// The fix makes every value-type field nullable with no initializer, so an
/// omitted property deserialises to null and the handler's HasValue guard leaves
/// the stored value alone. These pin the two halves that make that work:
///   (1) the deserialisation mechanism (omitted ⇒ null; explicit 0/false ⇒ kept),
///   (2) a genuinely new rig still gets sensible non-null defaults.
/// </summary>
[TestFixture]
public class RigPartialPutTests {
    // The web binder ASP.NET uses for the PUT body: camelCase, case-insensitive.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>THE mechanism. A partial body (only name + a LIVE compute-mode
    /// field, exactly what app.js patches) must leave every value-type field null,
    /// so the handler's `if (HasValue)` skips them and the rig keeps its values.
    /// If any came back non-null (e.g. a lingering initializer), the handler would
    /// write the model default over the rig — the bug.</summary>
    [Test]
    public void PartialBody_LeavesValueTypeFieldsNull() {
        const string partial = """
            { "name": "Backyard", "liveStackComputeMode": "server" }
            """;
        var p = JsonSerializer.Deserialize<EquipmentProfile>(partial, Web)!;

        Assert.Multiple(() => {
            Assert.That(p.DefaultGain, Is.Null, "gain omitted ⇒ null ⇒ handler leaves it alone");
            Assert.That(p.DefaultOffset, Is.Null);
            Assert.That(p.DefaultBinning, Is.Null);
            Assert.That(p.CoolerTargetTemperature, Is.Null);
            Assert.That(p.CoolerRampDegPerMinute, Is.Null);
            Assert.That(p.FocuserStepSize, Is.Null);
            Assert.That(p.FocuserBacklashSteps, Is.Null);
            Assert.That(p.VerticalFlipImage, Is.Null);
        });
    }

    /// <summary>An EXPLICIT 0 / false must survive — that's the whole reason for
    /// nullable over a `> 0` guard. Offset 0 (a real setting on some cameras) and
    /// flip false are distinguishable from "absent".</summary>
    [Test]
    public void ExplicitZeroAndFalse_AreKept_NotTreatedAsAbsent() {
        const string body = """
            { "name": "x", "defaultOffset": 0, "defaultGain": 0, "verticalFlipImage": false,
              "coolerTargetTemperature": 0, "focuserBacklashSteps": 0 }
            """;
        var p = JsonSerializer.Deserialize<EquipmentProfile>(body, Web)!;

        Assert.Multiple(() => {
            Assert.That(p.DefaultOffset, Is.EqualTo(0), "explicit 0 is a value, not absent");
            Assert.That(p.DefaultGain, Is.EqualTo(0));
            Assert.That(p.VerticalFlipImage, Is.False);
            Assert.That(p.CoolerTargetTemperature, Is.EqualTo(0));
            Assert.That(p.FocuserBacklashSteps, Is.EqualTo(0));
        });
    }

    /// <summary>A genuinely NEW rig must NOT be all-null (which would resolve offset
    /// to 0 at the read site — black-level clipping). CreateEquipmentProfile sets
    /// the sensible defaults the model used to carry.</summary>
    [Test]
    public void CreateEquipmentProfile_GivesSensibleNonNullDefaults() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-rigput-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = dir })
                .Build();
            var svc = new ProfileService(cfg, NullLogger<ProfileService>.Instance);

            var rig = svc.CreateEquipmentProfile("Fresh");

            Assert.Multiple(() => {
                Assert.That(rig.DefaultGain, Is.EqualTo(100));
                Assert.That(rig.DefaultOffset, Is.EqualTo(50), "a new rig must NOT run at offset 0");
                Assert.That(rig.DefaultBinning, Is.EqualTo(1));
                Assert.That(rig.CoolerTargetTemperature, Is.EqualTo(-10));
                Assert.That(rig.FocuserStepSize, Is.EqualTo(50));
            });
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>End-to-end through ProfileService.UpdateEquipmentProfile, mimicking
    /// what the endpoint does: a rig with real values, then a "partial patch" that
    /// only touches one field must leave the numerics intact. This is the closest
    /// we can get to the endpoint without a WebApplication — the patch lambda here
    /// mirrors the handler's HasValue guards.</summary>
    [Test]
    public void PartialUpdate_PreservesExistingNumerics() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-rigput2-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = dir })
                .Build();
            var svc = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
            var rig = svc.CreateEquipmentProfile("Rig");
            // User configured a non-default offset (the field that clipped to black).
            svc.UpdateEquipmentProfile(rig.Id, r => { r.DefaultOffset = 120; r.DefaultGain = 300; });

            // A partial PUT arrives: only liveStackComputeMode present ⇒ the omitted
            // numerics are null ⇒ the handler's guard skips them.
            var patch = JsonSerializer.Deserialize<EquipmentProfile>(
                "{ \"liveStackComputeMode\": \"client\" }", Web)!;
            svc.UpdateEquipmentProfile(rig.Id, r => {
                if (patch.DefaultOffset.HasValue) r.DefaultOffset = patch.DefaultOffset.Value;
                if (patch.DefaultGain.HasValue) r.DefaultGain = patch.DefaultGain.Value;
                r.LiveStackComputeMode = patch.LiveStackComputeMode;
            });

            // Read the SPECIFIC rig back (CreateEquipmentProfile doesn't make it
            // active, so ActiveEquipmentProfile could be a different one).
            var after = svc.ListEquipmentProfiles().Single(r => r.Id == rig.Id);
            Assert.That(after.DefaultOffset, Is.EqualTo(120), "partial PUT must not reset the user's offset");
            Assert.That(after.DefaultGain, Is.EqualTo(300));
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- The STRING half of the same hole (RigPatch) ----
    //
    // Nullable value types made "absent" detectable, but strings keep their
    // property initialiser, and several of those initialisers are real-looking
    // values: Name = "Default", NativeRaAlgorithm = "hysteresis",
    // NativeDecAlgorithm = "resistswitch", NativePierSideHandling = "mirror".
    // A one-field body bound straight to EquipmentProfile therefore arrived at
    // the handler claiming the rig was called "Default" — non-blank, so the
    // blank-guard let it through and the operator's "SV503" rig was renamed.
    // RigPatch.Merge seeds the model from the STORED rig instead.

    private static EquipmentProfile StoredRig() => new() {
        Name = "SV503",
        Camera = "0",
        CameraDriver = "zwo-sdk",
        Telescope = "ZWO AM3 USB",
        NativeRaAlgorithm = "predictive",
        NativeDecAlgorithm = "lowpass",
        NativeDecGuideMode = "north",
        NativePierSideHandling = "recalibrate",
        PHD2AlgoPreset = "Smooth",
        FilterNames = new[] { "Red", "Green", "Blue" },
        DefaultOffset = 120,
        FocalLengthMm = 478
    };

    [Test]
    public void Merge_ComputeModeOnlyBody_KeepsTheRigName() {
        var merged = RigPatch.Merge(StoredRig(),
            JsonNode.Parse("""{ "liveStackComputeMode": "server" }""")!.AsObject());

        Assert.Multiple(() => {
            Assert.That(merged.Name, Is.EqualTo("SV503"),
                "the reported bug: an absent name arrived as the initialiser \"Default\"");
            Assert.That(merged.LiveStackComputeMode, Is.EqualTo("server"), "the field actually sent");
        });
    }

    [Test]
    public void Merge_PartialBody_KeepsEveryStringWithANonEmptyDefault() {
        var merged = RigPatch.Merge(StoredRig(),
            JsonNode.Parse("""{ "coolerRampDegPerMinute": 1.5 }""")!.AsObject());

        Assert.Multiple(() => {
            Assert.That(merged.NativeRaAlgorithm, Is.EqualTo("predictive"));
            Assert.That(merged.NativeDecAlgorithm, Is.EqualTo("lowpass"));
            Assert.That(merged.NativeDecGuideMode, Is.EqualTo("north"));
            Assert.That(merged.NativePierSideHandling, Is.EqualTo("recalibrate"));
            Assert.That(merged.PHD2AlgoPreset, Is.EqualTo("Smooth"));
            Assert.That(merged.CameraDriver, Is.EqualTo("zwo-sdk"));
            Assert.That(merged.Camera, Is.EqualTo("0"));
            Assert.That(merged.Telescope, Is.EqualTo("ZWO AM3 USB"));
            Assert.That(merged.FilterNames, Is.EqualTo(new[] { "Red", "Green", "Blue" }),
                "collections are absent just as easily as strings");
            Assert.That(merged.FocalLengthMm, Is.EqualTo(478));
            Assert.That(merged.CoolerRampDegPerMinute, Is.EqualTo(1.5));
        });
    }

    [Test]
    public void Merge_PresentValues_StillWin() {
        var merged = RigPatch.Merge(StoredRig(),
            JsonNode.Parse("""{ "name": "Backyard", "nativeDecGuideMode": "off", "defaultOffset": 30 }""")!.AsObject());

        Assert.Multiple(() => {
            Assert.That(merged.Name, Is.EqualTo("Backyard"), "a real rename must still apply");
            Assert.That(merged.NativeDecGuideMode, Is.EqualTo("off"));
            Assert.That(merged.DefaultOffset, Is.EqualTo(30));
        });
    }

    [Test]
    public void Merge_ExplicitBlankName_LeavesTheGuardToRejectIt() {
        // A blank name still reaches the handler as blank (not as the stored
        // value), so the existing !IsNullOrWhiteSpace guard is what drops it.
        var merged = RigPatch.Merge(StoredRig(),
            JsonNode.Parse("""{ "name": "  " }""")!.AsObject());
        Assert.That(merged.Name, Is.EqualTo("  "));
    }

    [Test]
    public void Merge_NullBodyOrEmptyObject_ChangesNothing() {
        foreach (var patch in new JsonObject?[] { null, new JsonObject() }) {
            var merged = RigPatch.Merge(StoredRig(), patch);
            Assert.That(merged.Name, Is.EqualTo("SV503"));
            Assert.That(merged.NativeDecAlgorithm, Is.EqualTo("lowpass"));
        }
    }

    [Test]
    public void Merge_PascalCaseBody_DoesNotCollideWithTheStoredSpelling() {
        // An old/other client may send PascalCase. Case-insensitive binding
        // would otherwise see both "name" and "Name" for the same property.
        var merged = RigPatch.Merge(StoredRig(),
            JsonNode.Parse("""{ "Name": "Backyard" }""")!.AsObject());
        Assert.That(merged.Name, Is.EqualTo("Backyard"));
    }
}
