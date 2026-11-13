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
using System.Collections.Generic;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// EQMod starts every session in "Add point on Sync"; a plate-solve sync then
/// adds an alignment point instead of correcting the pointing. Polaris
/// switches it to Standard Sync before syncing.
/// </summary>
[TestFixture]
public class EqmodSyncModeTests {

    private static void SetSyncMode(IndiClient client, string device, bool append) {
        var props = client.Devices.GetOrAdd(device, _ => new ConcurrentDictionary<string, IndiProperty>());
        props["ALIGNSYNCMODE"] = new IndiSwitchProperty {
            Device = device,
            Name = "ALIGNSYNCMODE",
            Values = new Dictionary<string, bool> {
                ["ALIGNSTANDARDSYNC"] = !append,
                ["ALIGNAPPENDSYNC"] = append,
            }
        };
    }

    [Test]
    public void AppendSync_IsDetected() {
        using var client = new IndiClient();
        SetSyncMode(client, "EQMod Mount", append: true);
        Assert.That(IndiTelescope.InAppendSyncMode(client, "EQMod Mount"), Is.True);
    }

    [Test]
    public void StandardSync_IsLeftAlone() {
        using var client = new IndiClient();
        SetSyncMode(client, "EQMod Mount", append: false);
        Assert.That(IndiTelescope.InAppendSyncMode(client, "EQMod Mount"), Is.False);
    }

    [Test]
    public void OtherDrivers_HaveNoSyncMode() {
        using var client = new IndiClient();
        Assert.That(IndiTelescope.InAppendSyncMode(client, "ZWO AM5"), Is.False);
    }
}
