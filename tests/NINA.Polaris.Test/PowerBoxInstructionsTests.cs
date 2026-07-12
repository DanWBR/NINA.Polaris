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

using System.Linq;
using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class PowerBoxInstructionsTests {
    [Test]
    public void PowerBox_Instructions_RoundtripPreservesTypesAndParams() {
        var doc = new SequenceDocument {
            Name = "PowerBox",
            Root = new SequentialContainer {
                Name = "Root",
                Items = new() {
                    new SetPowerOutletInstruction { Outlet = 2, On = true },
                    new SetDewHeaterInstruction { Channel = 1, Percent = 70 },
                    new PowerCycleOutletInstruction { Outlet = 3, OffSeconds = 8 },
                    new SetSwitchValueInstruction { Channel = 4, Value = 13.5 },
                }
            }
        };

        var back = SequenceJson.Deserialize(SequenceJson.Serialize(doc));
        var items = ((SequentialContainer)back.Root).Items;

        Assert.That(items[0], Is.TypeOf<SetPowerOutletInstruction>());
        Assert.That(((SetPowerOutletInstruction)items[0]).Outlet, Is.EqualTo(2));
        Assert.That(((SetPowerOutletInstruction)items[0]).On, Is.True);

        Assert.That(items[1], Is.TypeOf<SetDewHeaterInstruction>());
        Assert.That(((SetDewHeaterInstruction)items[1]).Channel, Is.EqualTo(1));
        Assert.That(((SetDewHeaterInstruction)items[1]).Percent, Is.EqualTo(70));

        Assert.That(items[2], Is.TypeOf<PowerCycleOutletInstruction>());
        Assert.That(((PowerCycleOutletInstruction)items[2]).Outlet, Is.EqualTo(3));
        Assert.That(((PowerCycleOutletInstruction)items[2]).OffSeconds, Is.EqualTo(8));

        Assert.That(items[3], Is.TypeOf<SetSwitchValueInstruction>());
        Assert.That(((SetSwitchValueInstruction)items[3]).Channel, Is.EqualTo(4));
        Assert.That(((SetSwitchValueInstruction)items[3]).Value, Is.EqualTo(13.5));
    }

    [Test]
    public void PowerBox_Instructions_AppearInPaletteUnderPowerBoxCategory() {
        var known = SequenceEntityJsonConverter.KnownTypes;
        foreach (var t in new[] { "SetPowerOutlet", "SetDewHeater", "PowerCycleOutlet", "SetSwitchValue" }) {
            var entry = known.FirstOrDefault(k => k.Type == t);
            Assert.That(entry.Type, Is.EqualTo(t), $"'{t}' missing from the sequencer palette");
            Assert.That(entry.Category, Is.EqualTo("Power Box"));
            Assert.That(entry.Class, Is.EqualTo("Instruction"));
        }
    }

    [Test]
    public void SetDewHeater_RejectsNegativePercent() {
        Assert.That(new SetDewHeaterInstruction { Percent = -5 }.Validate(), Is.Not.Empty);
        Assert.That(new SetDewHeaterInstruction { Percent = 60 }.Validate(), Is.Empty);
    }

    [Test]
    public void PowerCycleOutlet_RejectsNegativeOffSeconds() {
        Assert.That(new PowerCycleOutletInstruction { OffSeconds = -1 }.Validate(), Is.Not.Empty);
        Assert.That(new PowerCycleOutletInstruction { OffSeconds = 5 }.Validate(), Is.Empty);
    }
}
