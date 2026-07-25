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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    // ---- Channel addressing (stable Key vs positional id) ----------------
    //
    // The numeric id is a POSITION in the device's channel map, but a saved
    // sequence outlives that map: it shifts whenever the driver publishes a
    // different property set. These pin the rule that a recorded Key wins.

    private sealed class FakeSwitch : NINA.Image.Interfaces.ISwitchDevice {
        private readonly List<NINA.Image.Interfaces.SwitchChannel> _ch;
        public FakeSwitch(params (string key, string name)[] channels) {
            _ch = channels.Select((c, i) => new NINA.Image.Interfaces.SwitchChannel(
                i, c.name, true, 0, 0, 1, 1, true, c.key)).ToList();
        }
        public string DeviceName => "Fake";
        public bool IsConnected => true;
        public IReadOnlyList<NINA.Image.Interfaces.SwitchChannel> Channels => _ch;
        public int SwitchCount => _ch.Count;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetBoolAsync(int id, bool on, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetValueAsync(int id, double v, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Test]
    public void ChannelKey_WinsOverAStaleNumericId() {
        // The sequence was saved when the mount outlet sat at index 0; the
        // driver now publishes an extra property ahead of it, so it is at 2.
        var pb = new FakeSwitch(("USB.PORT_1", "USB 1"),
                                ("USB.PORT_2", "USB 2"),
                                ("POWER.DC_3", "DC 3"));
        var id = PowerBoxTarget.Resolve(pb, "POWER.DC_3", fallbackId: 0);
        Assert.That(id, Is.EqualTo(2),
            "a recorded key must follow the channel, not the position it used to have");
    }

    [Test]
    public void MissingChannelKey_Throws_RatherThanActingOnThePosition() {
        var pb = new FakeSwitch(("POWER.DC_1", "DC 1"));
        // Silently falling back to index 0 is exactly the hazard: on a power
        // box that means cutting power to an unrelated device.
        Assert.That(() => PowerBoxTarget.Resolve(pb, "POWER.DC_9", fallbackId: 0),
            Throws.InvalidOperationException);
    }

    [Test]
    public void NoChannelKey_FallsBackToTheNumericId_ForSequencesSavedBefore() {
        var pb = new FakeSwitch(("POWER.DC_1", "DC 1"), ("POWER.DC_2", "DC 2"));
        Assert.That(PowerBoxTarget.Resolve(pb, null, fallbackId: 1), Is.EqualTo(1));
        Assert.That(PowerBoxTarget.Resolve(pb, "", fallbackId: 1), Is.EqualTo(1));
    }

    [Test]
    public void ChannelKey_SurvivesSerialization() {
        var doc = new SequenceDocument {
            Name = "Keys",
            Root = new SequentialContainer {
                Name = "Root",
                Items = new() {
                    new SetPowerOutletInstruction { Outlet = 2, ChannelKey = "POWER.DC_3", On = true },
                    new PowerCycleOutletInstruction { Outlet = 0, ChannelKey = "POWER.DC_1", OffSeconds = 4 },
                }
            }
        };
        var items = ((SequentialContainer)SequenceJson.Deserialize(SequenceJson.Serialize(doc)).Root).Items;
        Assert.That(((SetPowerOutletInstruction)items[0]).ChannelKey, Is.EqualTo("POWER.DC_3"));
        Assert.That(((PowerCycleOutletInstruction)items[1]).ChannelKey, Is.EqualTo("POWER.DC_1"));
    }
}
