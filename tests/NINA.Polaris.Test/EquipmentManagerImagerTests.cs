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
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Alpaca;
using NINA.Polaris.Services.Simulator.Gear;

namespace NINA.Polaris.Test;

/// <summary>STAGE2 runtime model: the imaging-camera collection on
/// <see cref="EquipmentManager"/> (main = 0, aux = 1, extras = 2+).</summary>
[TestFixture]
public class EquipmentManagerImagerTests {

    private static EquipmentManager Make() =>
        new(new IndiClient("localhost", 7624), NullLogger<EquipmentManager>.Instance,
            new AlpacaDiscoveryCache(), new SimGearService());

    [Test]
    public void EnumerateImagers_Default_HasMainAndAuxOnly() {
        var equip = Make();
        var all = equip.EnumerateImagers();
        Assert.That(all.Count, Is.EqualTo(2));
        Assert.That(all[0].Role, Is.EqualTo("main"));
        Assert.That(all[1].Role, Is.EqualTo("aux"));
        Assert.That(equip.ExtraImagerCount, Is.EqualTo(0));
        Assert.That(equip.GetImager(0), Is.Null);   // nothing bound yet
        Assert.That(equip.GetImager(2), Is.Null);
    }

    [Test]
    public void SelectImager_ExtraSlot_BindsGrowsAndEnumerates() {
        var equip = Make();
        var cam = equip.SelectImager(2, "indi", "CCD Three");

        Assert.That(cam, Is.Not.Null);
        Assert.That(equip.ExtraImagerCount, Is.EqualTo(1));
        Assert.That(equip.GetImager(2), Is.SameAs(cam));

        var all = equip.EnumerateImagers();
        Assert.That(all.Count, Is.EqualTo(3));
        Assert.That(all[2].Role, Is.EqualTo("imager-3"));
        Assert.That(all[2].Index, Is.EqualTo(2));
        Assert.That(all[2].DeviceId, Is.EqualTo("CCD Three"));
        Assert.That(all[2].Driver, Is.EqualTo("indi"));
    }

    [Test]
    public void SelectImager_ReplacingASlot_KeepsCountAndSwapsCamera() {
        var equip = Make();
        var first = equip.SelectImager(2, "indi", "CCD A");
        var second = equip.SelectImager(2, "indi", "CCD B");

        Assert.That(equip.ExtraImagerCount, Is.EqualTo(1));
        Assert.That(equip.GetImager(2), Is.SameAs(second));
        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(equip.EnumerateImagers()[2].DeviceId, Is.EqualTo("CCD B"));
    }
}
