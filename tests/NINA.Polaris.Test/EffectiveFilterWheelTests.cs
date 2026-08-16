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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// FILTERNAME: EffectiveFilterWheel overlays the rig's saved filter names on top
/// of the driver's, so read-only wheels (ASCOM/Alpaca) can still be renamed in
/// Polaris. These pin the overlay, the effective name→slot selection, and the
/// profile-side persistence (with the focus-offset remap + blank-keep).
/// </summary>
[TestFixture]
public class EffectiveFilterWheelTests {
    private string _dir = "";
    private ProfileService _profiles = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-effw-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = _dir })
            .Build();
        _profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void SetSavedNames(string[] names) =>
        _profiles.UpdateEquipmentProfile(_profiles.ActiveEquipmentProfile.Id,
            r => r.FilterNames = names);

    private void SetOffsets(Dictionary<string, int> offsets) =>
        _profiles.UpdateEquipmentProfile(_profiles.ActiveEquipmentProfile.Id,
            r => r.FilterOffsets = offsets);

    [Test]
    public void FilterNames_OverlaysProfileOverDriver_PerSlot() {
        var inner = new FakeWheel { Names = new[] { "clear1", "clear2", "clear3" } };
        SetSavedNames(new[] { "Lum", "", "Blue" });   // middle slot left to the driver
        var w = new EffectiveFilterWheel(inner, _profiles);

        Assert.That(w.FilterNames, Is.EqualTo(new[] { "Lum", "clear2", "Blue" }));
    }

    [Test]
    public void FilterNames_LengthAlwaysMatchesDriver_EvenIfProfileLongerOrShorter() {
        var inner = new FakeWheel { Names = new[] { "a", "b" } };
        SetSavedNames(new[] { "X", "Y", "Z", "W" });   // profile has more slots than the wheel
        var w = new EffectiveFilterWheel(inner, _profiles);
        Assert.That(w.FilterNames, Is.EqualTo(new[] { "X", "Y" }));
    }

    [Test]
    public void CurrentFilterName_ReturnsEffectiveNameForCurrentSlot() {
        var inner = new FakeWheel { Names = new[] { "clear1", "clear2", "clear3" }, CurrentDriverName = "clear2" };
        SetSavedNames(new[] { "Lum", "Ha", "Blue" });
        var w = new EffectiveFilterWheel(inner, _profiles);
        Assert.That(w.CurrentFilterName, Is.EqualTo("Ha"));
    }

    [Test]
    public async Task SetFilterByName_ResolvesEffectiveNameToDriverSlot() {
        var inner = new FakeWheel { Names = new[] { "clear1", "clear2", "clear3" } };
        SetSavedNames(new[] { "Lum", "Ha", "Blue" });
        var w = new EffectiveFilterWheel(inner, _profiles);

        await w.SetFilterByNameAsync("Blue");
        // Delegates by the DRIVER's own name for that slot, so the backend's
        // native position base is respected.
        Assert.That(inner.LastSetByName, Is.EqualTo("clear3"));
    }

    [Test]
    public async Task SetFilterNames_PersistsToProfile_AndRemapsOffsetsBySlot() {
        var inner = new FakeWheel { Names = new[] { "clear1", "clear2", "clear3" }, SupportsEdit = false };
        SetOffsets(new Dictionary<string, int> { ["clear1"] = 10, ["clear3"] = -20 });
        var w = new EffectiveFilterWheel(inner, _profiles);

        await w.SetFilterNamesAsync(new[] { "Lum", "Ha", "Blue" });

        var rig = _profiles.ActiveEquipmentProfile;
        Assert.Multiple(() => {
            Assert.That(rig.FilterNames, Is.EqualTo(new[] { "Lum", "Ha", "Blue" }));
            Assert.That(rig.FilterOffsets["Lum"], Is.EqualTo(10), "offset followed the rename by slot");
            Assert.That(rig.FilterOffsets["Blue"], Is.EqualTo(-20));
            Assert.That(rig.FilterOffsets.ContainsKey("clear1"), Is.False, "old key dropped");
        });
    }

    [Test]
    public async Task SetFilterNames_BlankEntryKeepsCurrentEffectiveName() {
        var inner = new FakeWheel { Names = new[] { "clear1", "clear2", "clear3" }, SupportsEdit = false };
        var w = new EffectiveFilterWheel(inner, _profiles);

        await w.SetFilterNamesAsync(new[] { "Lum", "", "Blue" });
        Assert.That(_profiles.ActiveEquipmentProfile.FilterNames,
            Is.EqualTo(new[] { "Lum", "clear2", "Blue" }), "blank kept the driver name, not erased");
    }

    [Test]
    public async Task SetFilterNames_ReadOnlyDriver_DoesNotPushToDriver_AndDoesNotThrow() {
        var inner = new FakeWheel { Names = new[] { "clear1", "clear2" }, SupportsEdit = false };
        var w = new EffectiveFilterWheel(inner, _profiles);
        await w.SetFilterNamesAsync(new[] { "Lum", "Ha" });
        Assert.That(inner.SetNamesCalled, Is.False, "a read-only driver must not be pushed to");
    }

    [Test]
    public async Task SetFilterNames_WritableDriver_AlsoPushesToDriver() {
        var inner = new FakeWheel { Names = new[] { "F1", "F2" }, SupportsEdit = true };
        var w = new EffectiveFilterWheel(inner, _profiles);
        await w.SetFilterNamesAsync(new[] { "Lum", "Ha" });
        Assert.That(inner.SetNamesCalled, Is.True);
        Assert.That(inner.LastSetNames, Is.EqualTo(new[] { "Lum", "Ha" }));
    }

    [Test]
    public void Capabilities_ReflectTheInnerDriver() {
        var ro = new EffectiveFilterWheel(new FakeWheel { SupportsEdit = false }, _profiles);
        var rw = new EffectiveFilterWheel(new FakeWheel { SupportsEdit = true }, _profiles);
        Assert.That(ro.Capabilities.SupportsEditNames, Is.False);
        Assert.That(rw.Capabilities.SupportsEditNames, Is.True);
    }

    // ── fake backend ─────────────────────────────────────────────────
    private sealed class FakeWheel : IFilterWheel {
        public string[] Names { get; set; } = System.Array.Empty<string>();
        public string CurrentDriverName { get; set; } = "";
        public bool SupportsEdit { get; set; }
        public string? LastSetByName { get; private set; }
        public int LastSetPosition { get; private set; } = -1;
        public bool SetNamesCalled { get; private set; }
        public string[]? LastSetNames { get; private set; }

        public string DeviceName => "fake";
        public bool IsConnected => true;
        public int Position { get; private set; }
        public bool IsMoving => false;
        public string[] FilterNames => Names;
        public int FilterCount => Names.Length;
        public string CurrentFilterName => CurrentDriverName;
        public FilterWheelCapabilities Capabilities => new(SupportsEditNames: SupportsEdit);

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetPositionAsync(int position, CancellationToken ct = default) {
            LastSetPosition = position; Position = position; return Task.CompletedTask;
        }
        public Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) {
            LastSetByName = filterName; return Task.CompletedTask;
        }
        public Task SetFilterNamesAsync(string[] names, CancellationToken ct = default) {
            if (!SupportsEdit)
                throw new System.NotSupportedException("read-only");
            SetNamesCalled = true; LastSetNames = names; Names = names; return Task.CompletedTask;
        }
    }
}
