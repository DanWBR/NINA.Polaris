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

using NINA.Polaris.Services;
using NINA.Polaris.Services.Sequencer.Instructions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class FilterOffsetTests {
    [Test]
    public void EquipmentProfile_FilterOffsets_DefaultsEmpty() {
        var p = new EquipmentProfile();
        Assert.That(p.FilterOffsets, Is.Not.Null);
        Assert.That(p.FilterOffsets.Count, Is.EqualTo(0));
    }

    [Test]
    public void EquipmentProfile_FilterOffsets_Roundtrips() {
        var p = new EquipmentProfile();
        p.FilterOffsets["L"] = 0;
        p.FilterOffsets["R"] = -12;
        p.FilterOffsets["G"] = -8;
        Assert.That(p.FilterOffsets["L"], Is.EqualTo(0));
        Assert.That(p.FilterOffsets["R"], Is.EqualTo(-12));
        Assert.That(p.FilterOffsets["G"], Is.EqualTo(-8));
    }

    [Test]
    public void MoveToFilterOffsetInstruction_TypeDiscriminator_Stable() {
        var i = new MoveToFilterOffsetInstruction { FilterName = "R", OffsetSteps = -12 };
        Assert.That(i.Type, Is.EqualTo("MoveToFilterOffset"));
    }
}