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

using System.Collections.Concurrent;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// A filter move is over when the driver's FILTER_SLOT leaves Busy reading the
/// slot asked for, not at its first reply (discussion #37: PREVIEW exposed with
/// the wheel still turning). The driver is simulated by writing FILTER_SLOT into
/// the client's property cache, which is what the wheel reads.
/// </summary>
[TestFixture]
public class IndiFilterWheelArrivalTests {
    private const string Dev = "QHY CFW";

    private static void SetSlot(IndiClient client, IndiPropertyState state, double slot) {
        var props = client.Devices.GetOrAdd(Dev, _ => new ConcurrentDictionary<string, IndiProperty>());
        props["FILTER_SLOT"] = new IndiNumberProperty {
            Device = Dev, Name = "FILTER_SLOT", State = state,
            Values = { ["FILTER_SLOT_VALUE"] = new IndiNumberElement { Value = slot } }
        };
    }

    [Test]
    public async Task Waits_WhileTheWheelIsBusy_AndReturnsWhenItStopsOnTheSlot() {
        using var client = new IndiClient();
        var wheel = new IndiFilterWheel(client, Dev);
        // The driver's first reply: Busy, still reading the old slot.
        SetSlot(client, IndiPropertyState.Busy, 1);
        var wait = wheel.WaitForArrivalAsync(3, CancellationToken.None);

        await Task.Delay(400);
        Assert.That(wait.IsCompleted, Is.False, "returned while the wheel was still turning");
        SetSlot(client, IndiPropertyState.Busy, 3);
        await Task.Delay(300);
        Assert.That(wait.IsCompleted, Is.False, "returned on the slot number while still Busy");

        SetSlot(client, IndiPropertyState.Ok, 3);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Waits_ForTheRequestedSlot_EvenWhenTheStateIsAlreadyOk() {
        using var client = new IndiClient();
        var wheel = new IndiFilterWheel(client, Dev);
        // Ack timed out on a silent driver: the cache still shows the old slot.
        SetSlot(client, IndiPropertyState.Ok, 1);
        var wait = wheel.WaitForArrivalAsync(2, CancellationToken.None);

        await Task.Delay(300);
        Assert.That(wait.IsCompleted, Is.False);
        SetSlot(client, IndiPropertyState.Ok, 2);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public void AnAlert_FailsTheMove() {
        using var client = new IndiClient();
        var wheel = new IndiFilterWheel(client, Dev);
        SetSlot(client, IndiPropertyState.Alert, 1);
        Assert.ThrowsAsync<InvalidOperationException>(
            () => wheel.WaitForArrivalAsync(4, CancellationToken.None));
    }

    [Test]
    public void AWheelThatNeverArrives_TimesOut() {
        using var client = new IndiClient();
        var wheel = new IndiFilterWheel(client, Dev);
        SetSlot(client, IndiPropertyState.Busy, 1);
        var saved = IndiFilterWheel.ArrivalTimeout;
        IndiFilterWheel.ArrivalTimeout = TimeSpan.FromMilliseconds(400);
        try {
            var ex = Assert.ThrowsAsync<TimeoutException>(
                () => wheel.WaitForArrivalAsync(5, CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("slot 5"));
        } finally {
            IndiFilterWheel.ArrivalTimeout = saved;
        }
    }

    [Test]
    public async Task AWheelAlreadyOnTheSlot_IsNotMoved() {
        // No INDI server behind this client: any write would fail or hang on
        // the ack, so finishing at once proves nothing was sent.
        using var client = new IndiClient();
        var wheel = new IndiFilterWheel(client, Dev);
        SetSlot(client, IndiPropertyState.Ok, 3);
        await wheel.SetPositionAsync(3).WaitAsync(TimeSpan.FromSeconds(1));
    }
}
