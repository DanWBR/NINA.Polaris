// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Focus;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class FilterFocusMemoryTests {
    private static EquipmentProfile RigWith(params (string filter, int pos, double temp, double ageHours)[] entries) {
        var rig = new EquipmentProfile { AutoFocus = new AutoFocusSettings() };
        var now = DateTime.UtcNow;
        foreach (var (f, pos, temp, age) in entries) {
            rig.FilterFocusMemory[f] = new FilterFocusMemory {
                Position = pos, TemperatureC = temp, Utc = now.AddHours(-age), FocuserName = "Foc-A"
            };
        }
        FilterFocusMath.RecomputeOffsets(rig, rig.AutoFocus!.FilterMemoryTempToleranceC);
        return rig;
    }

    // ---- IsValid ----

    [Test]
    public void IsValid_FreshSameTempSameFocuser_IsTrue() {
        var mem = new FilterFocusMemory { Position = 1000, TemperatureC = 10, Utc = DateTime.UtcNow, FocuserName = "Foc-A" };
        Assert.That(FilterFocusMath.IsValid(mem, 10.5, "Foc-A", 1.5, 12, DateTime.UtcNow), Is.True);
    }

    [Test]
    public void IsValid_TempDriftBeyondTolerance_IsFalse() {
        var mem = new FilterFocusMemory { Position = 1000, TemperatureC = 10, Utc = DateTime.UtcNow, FocuserName = "Foc-A" };
        Assert.That(FilterFocusMath.IsValid(mem, 13.0, "Foc-A", 1.5, 12, DateTime.UtcNow), Is.False);
    }

    [Test]
    public void IsValid_TooOld_IsFalse() {
        var mem = new FilterFocusMemory { Position = 1000, TemperatureC = 10, Utc = DateTime.UtcNow.AddHours(-24), FocuserName = "Foc-A" };
        Assert.That(FilterFocusMath.IsValid(mem, 10.0, "Foc-A", 1.5, 12, DateTime.UtcNow), Is.False);
    }

    [Test]
    public void IsValid_DifferentFocuser_IsFalse() {
        var mem = new FilterFocusMemory { Position = 1000, TemperatureC = 10, Utc = DateTime.UtcNow, FocuserName = "Foc-A" };
        Assert.That(FilterFocusMath.IsValid(mem, 10.0, "Foc-B", 1.5, 12, DateTime.UtcNow), Is.False);
    }

    [Test]
    public void IsValid_NoProbe_UsesAgeAndEquipmentOnly() {
        // Both temps NaN → temperature check skipped; fresh + same focuser → valid.
        var mem = new FilterFocusMemory { Position = 1000, TemperatureC = double.NaN, Utc = DateTime.UtcNow, FocuserName = "Foc-A" };
        Assert.That(FilterFocusMath.IsValid(mem, double.NaN, "Foc-A", 1.5, 12, DateTime.UtcNow), Is.True);
    }

    // ---- PlanForFilter ----

    [Test]
    public void Plan_FreshTarget_RestoresAbsolute() {
        var rig = RigWith(("L", 1000, 10, 0.1));
        var plan = FilterFocusMath.PlanForFilter(rig, "L", 10.2, "Foc-A", rig.AutoFocus!, DateTime.UtcNow);
        Assert.That(plan.Kind, Is.EqualTo(FocusPlanKind.RestoreAbsolute));
        Assert.That(plan.Position, Is.EqualTo(1000));
    }

    [Test]
    public void Plan_StaleTargetWithFreshAnchor_OffsetTransfer() {
        // L fresh at 10°C; R last focused long ago (stale by age) but its offset
        // was learned (+40). Switching to R at 10°C → derive from L: 1000 + 40.
        var rig = RigWith(("L", 1000, 10, 0.1), ("R", 1040, 10, 48));
        var plan = FilterFocusMath.PlanForFilter(rig, "R", 10.1, "Foc-A", rig.AutoFocus!, DateTime.UtcNow);
        Assert.That(plan.Kind, Is.EqualTo(FocusPlanKind.OffsetTransfer));
        Assert.That(plan.Position, Is.EqualTo(1040));
        Assert.That(plan.DerivedFrom, Is.EqualTo("L"));
    }

    [Test]
    public void Plan_StaleTargetNoAnchor_IsStale() {
        // Only R, stale by temperature, nothing else to anchor on.
        var rig = RigWith(("R", 1040, 5, 0.1));
        var plan = FilterFocusMath.PlanForFilter(rig, "R", 12.0, "Foc-A", rig.AutoFocus!, DateTime.UtcNow);
        Assert.That(plan.Kind, Is.EqualTo(FocusPlanKind.Stale));
        Assert.That(plan.Position, Is.EqualTo(1040));
    }

    [Test]
    public void Plan_NoMemory_IsNone() {
        var rig = new EquipmentProfile { AutoFocus = new AutoFocusSettings() };
        var plan = FilterFocusMath.PlanForFilter(rig, "Ha", 10, "Foc-A", rig.AutoFocus!, DateTime.UtcNow);
        Assert.That(plan.Kind, Is.EqualTo(FocusPlanKind.None));
    }

    // ---- RecomputeOffsets ----

    [Test]
    public void RecomputeOffsets_RelativeToReferenceL() {
        var rig = RigWith(("L", 1000, 10, 0.1), ("R", 1040, 10, 0.1), ("B", 975, 10, 0.1));
        Assert.That(rig.FilterOffsets["L"], Is.EqualTo(0));
        Assert.That(rig.FilterOffsets["R"], Is.EqualTo(40));
        Assert.That(rig.FilterOffsets["B"], Is.EqualTo(-25));
    }

    [Test]
    public void RecomputeOffsets_SkipsTemperatureIncompatiblePoints() {
        // R learned far from L's temperature → its offset is not (re)derived.
        var rig = new EquipmentProfile { AutoFocus = new AutoFocusSettings() };
        var now = DateTime.UtcNow;
        rig.FilterFocusMemory["L"] = new FilterFocusMemory { Position = 1000, TemperatureC = 10, Utc = now };
        rig.FilterFocusMemory["R"] = new FilterFocusMemory { Position = 1040, TemperatureC = 25, Utc = now };
        rig.FilterOffsets["R"] = 999; // pre-existing, must be left alone
        FilterFocusMath.RecomputeOffsets(rig, 1.5);
        Assert.That(rig.FilterOffsets["L"], Is.EqualTo(0));
        Assert.That(rig.FilterOffsets["R"], Is.EqualTo(999));
    }

    // ---- Persistence: Save-As carries the field ----

    [Test]
    public void CloneActiveRigAs_CarriesMemoryAndSettings() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-ffm-" + Guid.NewGuid().ToString("N"));
        try {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = dir })
                .Build();
            var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
            var active = profiles.ActiveEquipmentProfile;

            profiles.UpdateEquipmentProfile(active.Id, r => {
                r.AutoFocus ??= new AutoFocusSettings();
                r.AutoFocus.FilterMemoryAutoApply = false;
                r.AutoFocus.FilterMemoryMaxAgeHours = 6;
                r.FilterFocusMemory["L"] = new FilterFocusMemory {
                    Position = 1234, TemperatureC = 8.5, Utc = DateTime.UtcNow, FocuserName = "Foc-A", Hfr = 1.9
                };
            });

            var clone = profiles.CloneActiveRigAs("Copy");
            Assert.That(clone.FilterFocusMemory.ContainsKey("L"), Is.True, "clone dropped the focus memory");
            Assert.That(clone.FilterFocusMemory["L"].Position, Is.EqualTo(1234));
            Assert.That(clone.FilterFocusMemory["L"].TemperatureC, Is.EqualTo(8.5).Within(1e-9));
            Assert.That(clone.AutoFocus!.FilterMemoryAutoApply, Is.False);
            Assert.That(clone.AutoFocus!.FilterMemoryMaxAgeHours, Is.EqualTo(6));
            // Deep copy, not a shared reference.
            clone.FilterFocusMemory["L"].Position = 5;
            Assert.That(profiles.ActiveEquipmentProfile.FilterFocusMemory["L"].Position, Is.EqualTo(1234));
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
