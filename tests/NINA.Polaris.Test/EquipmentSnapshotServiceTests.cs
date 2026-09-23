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

using Microsoft.Extensions.Logging.Abstractions;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Field report 2026-09-22: moving the mount with the joystick while a stream
/// was running froze the host, and the client blamed the network. The status
/// payload was reading live hardware on whatever thread happened to build it,
/// and those reads block (an SDK lock the video grab loop keeps barging, a 15 s
/// Alpaca HTTP read). This service is what moved them off that path.
///
/// <para>The contract these tests pin: a caller reading the snapshot never waits
/// for a device, the last good value keeps being served, and the freshness is
/// visible so the UI can say the host is busy rather than inventing a network
/// problem.</para>
/// </summary>
[TestFixture]
public class EquipmentSnapshotServiceTests {
    private static EquipmentSnapshotService NewService() {
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        return new EquipmentSnapshotService(equip, NullLogger<EquipmentSnapshotService>.Instance);
    }

    [Test]
    public void BeforeTheFirstRefresh_NothingIsServedAndTheAgeSaysSo() {
        using var svc = NewService();
        Assert.Multiple(() => {
            Assert.That(svc.Latest, Is.Null);
            // MaxValue rather than zero: "no snapshot yet" must not look like
            // "a snapshot from this instant", which is what the UI would show.
            Assert.That(svc.Age, Is.EqualTo(TimeSpan.MaxValue));
            Assert.That(svc.Stalled, Is.False, "nothing is in flight yet");
        });
    }

    [Test]
    public async Task OnceStarted_ItServesASnapshotAndKeepsItFresh() {
        using var svc = NewService();
        await svc.StartAsync(CancellationToken.None);
        try {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (svc.Latest == null && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.That(svc.Latest, Is.Not.Null, "the loop must publish a snapshot");
            Assert.That(svc.Age, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(svc.Stalled, Is.False,
                "a rig with no devices connected has nothing slow to read");

            // And it keeps going: a second refresh lands within a couple of
            // intervals, so the snapshot cannot silently freeze at boot.
            var first = svc.Latest;
            var refreshed = DateTime.UtcNow.AddSeconds(5);
            while (ReferenceEquals(svc.Latest, first) && DateTime.UtcNow < refreshed)
                await Task.Delay(50);
            Assert.That(svc.Latest, Is.Not.SameAs(first), "the loop must keep refreshing");
        } finally {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public void TheIntervalPacesItselfAndTheStallWindowIsLonger() {
        // The loop is self-paced: a refresh that overruns starts the next one
        // late instead of stacking up, and the stall window has to be several
        // intervals or a filter wheel mid-move would be called a stall.
        Assert.That(EquipmentSnapshotService.Interval,
            Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(EquipmentSnapshotService.StaleAfter,
            Is.GreaterThanOrEqualTo(EquipmentSnapshotService.Interval * 3));
    }
}
