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

    [Test]
    public void ExtraImager_HasOwnFocuserAndFilterWheel() {
        var equip = Make();
        var foc = equip.SelectImagerFocuser(2, "indi", "Focuser Three");
        var fw = equip.SelectImagerFilterWheel(2, "indi", "EFW Three");

        Assert.That(foc, Is.Not.Null);
        Assert.That(fw, Is.Not.Null);
        Assert.That(equip.GetImagerFocuser(2), Is.SameAs(foc));
        Assert.That(equip.GetImagerFilterWheel(2), Is.SameAs(fw));
        // Binding a focuser/wheel to the extra slot did not spawn a second slot.
        Assert.That(equip.ExtraImagerCount, Is.EqualTo(1));
    }

    [Test]
    public void ImagerFocuser_Slots0And1_DelegateToMainAndAux() {
        var equip = Make();
        equip.SelectImagerFocuser(0, "indi", "Foc Main");
        equip.SelectImagerFocuser(1, "indi", "Foc Aux");
        Assert.That(equip.GetImagerFocuser(0), Is.SameAs(equip.Focuser));
        Assert.That(equip.GetImagerFocuser(1), Is.SameAs(equip.AuxFocuser));
        // No extra imager slots were created by binding the main/aux focusers.
        Assert.That(equip.ExtraImagerCount, Is.EqualTo(0));
    }

    [Test]
    public void ImagerFilterWheel_AuxSlot_IsNotSupportedYet() {
        var equip = Make();
        Assert.That(equip.GetImagerFilterWheel(1), Is.Null);
        Assert.Throws<System.NotSupportedException>(
            () => equip.SelectImagerFilterWheel(1, "indi", "EFW Aux"));
    }

    [Test]
    public void RemoveImager_DropsSlotAndShiftsHigherOnesDown() {
        var equip = Make();
        equip.SelectImager(2, "indi", "CCD A");
        var camB = equip.SelectImager(3, "indi", "CCD B");
        Assert.That(equip.ExtraImagerCount, Is.EqualTo(2));

        equip.RemoveImager(2);   // remove the first extra; B shifts into slot 2

        Assert.That(equip.ExtraImagerCount, Is.EqualTo(1));
        Assert.That(equip.GetImager(2), Is.SameAs(camB));
        Assert.That(equip.EnumerateImagers().Count, Is.EqualTo(3));   // main+aux+B
        Assert.That(equip.EnumerateImagers()[2].DeviceId, Is.EqualTo("CCD B"));
    }

    [Test]
    public void RemoveImager_MainOrAuxSlot_Throws() {
        var equip = Make();
        Assert.Throws<System.InvalidOperationException>(() => equip.RemoveImager(0));
        Assert.Throws<System.InvalidOperationException>(() => equip.RemoveImager(1));
    }

    [Test]
    public void RemoveImager_OutOfRange_IsNoOp() {
        var equip = Make();
        equip.SelectImager(2, "indi", "CCD A");
        Assert.DoesNotThrow(() => equip.RemoveImager(9));
        Assert.That(equip.ExtraImagerCount, Is.EqualTo(1));
    }
}
