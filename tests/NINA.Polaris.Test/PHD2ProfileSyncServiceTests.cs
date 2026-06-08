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
/// Tests focus on the parts of PHD2ProfileSyncService that don't require
/// a live PHD2, early-out behavior when PHD2 is disconnected, defaults
/// applied to the SyncStatus, and the event subscription lifecycle.
/// Full end-to-end sync needs a real PHD2 (integration tests, separate).
/// </summary>
[TestFixture]
public class PHD2ProfileSyncServiceTests {

    private (PHD2ProfileSyncService sync, ProfileService profiles, PHD2Client phd2)
        MakeService() {
        var cfg = new ConfigurationBuilder().Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var sync = new PHD2ProfileSyncService(phd2, profiles,
            NullLogger<PHD2ProfileSyncService>.Instance);
        return (sync, profiles, phd2);
    }

    [Test]
    public async Task SyncRigToProfileAsync_PHD2Disconnected_ReturnsWarningNoOp() {
        var (sync, profiles, _) = MakeService();
        var rig = profiles.ActiveEquipmentProfile;

        var result = await sync.SyncRigToProfileAsync(rig, default);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("not connected").IgnoreCase);
        Assert.That(sync.CurrentStatus.Phase, Is.EqualTo("phd2-disconnected"));
    }

    [Test]
    public void InitialStatus_IsIdle() {
        var (sync, _, _) = MakeService();
        Assert.That(sync.CurrentStatus.Phase, Is.EqualTo("idle"));
        Assert.That(sync.CurrentStatus.RigId, Is.Empty);
        Assert.That(sync.CurrentStatus.Error, Is.Null);
    }

    [Test]
    public async Task ProfileActivated_AutoSyncDisabled_NoSyncTriggered() {
        var (sync, profiles, _) = MakeService();
        var rig = profiles.ActiveEquipmentProfile;
        // Flip auto-sync OFF
        profiles.UpdateEquipmentProfile(rig.Id, r => r.PHD2AutoSyncOnRigSwitch = false);

        // Activate; sync should not change CurrentStatus (still idle).
        var initialPhase = sync.CurrentStatus.Phase;
        profiles.ActivateEquipmentProfile(rig.Id);

        // Give the fire-and-forget time to NOT happen.
        await Task.Delay(150);

        Assert.That(sync.CurrentStatus.Phase, Is.EqualTo(initialPhase),
            "Auto-sync disabled → status should stay idle");
    }

    [Test]
    public void Dispose_UnsubscribesFromActivationEvent() {
        var (sync, profiles, _) = MakeService();
        // Subscribe via the constructor + auto-sync=true → activation
        // would normally trigger the handler, but after Dispose it shouldn't.
        sync.Dispose();
        Assert.DoesNotThrow(() => profiles.ActivateEquipmentProfile(profiles.ActiveEquipmentProfile.Id),
            "Activate after dispose should not throw");
    }
}