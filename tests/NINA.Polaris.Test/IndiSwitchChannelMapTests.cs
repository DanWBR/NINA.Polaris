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
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// Channel-map construction in <see cref="IndiSwitch"/>, using
/// <c>indi_asi_power</c> (ASIAIR Pro power ports) as the reference shape:
/// one vector PER OUTLET, element labels repeated across outlets, and
/// on/off published as a two-member OneOfMany pair rather than a single
/// toggle. The device snapshot is injected straight into
/// <see cref="IndiClient.Devices"/> — no socket, no driver.
/// </summary>
[TestFixture]
public class IndiSwitchChannelMapTests {
    private const string Dev = "ASI Power";

    private static IndiClient ClientWith(params IndiProperty[] props) {
        var client = new IndiClient("localhost", 7624);
        var bag = new System.Collections.Concurrent.ConcurrentDictionary<string, IndiProperty>();
        foreach (var p in props) {
            p.Device = Dev;
            bag[p.Name] = p;
        }
        client.Devices[Dev] = bag;
        return client;
    }

    private static IndiSwitchProperty Selector(string name, string label) => new() {
        Name = name,
        Label = label,
        Rule = IndiSwitchRule.OneOfMany,
        Permission = IndiPropertyPermission.ReadWrite,
        Values = { [name + "0"] = true, [name + "1"] = false },
        Labels = { [name + "0"] = "None", [name + "1"] = "Camera" },
    };

    private static IndiSwitchProperty OnOff(string name, bool on) => new() {
        Name = name,
        Label = "On/Off",
        Rule = IndiSwitchRule.OneOfMany,
        Permission = IndiPropertyPermission.ReadWrite,
        Values = { [name + "OFF"] = !on, [name + "ON"] = on },
        Labels = { [name + "OFF"] = "Off", [name + "ON"] = "On" },
    };

    /// <summary>Element labels repeat across the four port vectors ("Camera"
    /// four times); the vector label is the only thing that tells them apart,
    /// so it has to end up in the channel name.</summary>
    [Test]
    public async Task PerPortSelectors_AreQualifiedByVectorLabel() {
        using var client = ClientWith(Selector("DEV0", "Port 1"), Selector("DEV1", "Port 2"));
        var sw = new IndiSwitch(client, Dev);
        await sw.RefreshAsync();

        var names = sw.Channels.Select(c => c.Name).ToList();
        Assert.That(names, Does.Contain("Port 1 · Camera"));
        Assert.That(names, Does.Contain("Port 2 · Camera"));
        Assert.That(names.Distinct().Count(), Is.EqualTo(names.Count),
            "every channel must be distinguishable in the UI");
    }

    /// <summary>An Off/On pair under the OneOfMany rule is one outlet: one
    /// channel, bound to the "on" member, reporting the outlet's state.</summary>
    [Test]
    public async Task OneOfManyOffOnPair_CollapsesToOneChannel() {
        using var client = ClientWith(OnOff("ONOFF0", on: true));
        var sw = new IndiSwitch(client, Dev);
        await sw.RefreshAsync();

        Assert.That(sw.SwitchCount, Is.EqualTo(1));
        var ch = sw.Channels[0];
        Assert.That(ch.Boolean, Is.True);
        Assert.That(ch.Value, Is.EqualTo(1).Within(0.001));
        Assert.That(ch.Key, Is.EqualTo("ONOFF0.ONOFF0ON"));
    }

    /// <summary>indi_asi_power labels all four on/off vectors "On/Off", so
    /// qualification alone is not enough — the property name breaks the tie.</summary>
    [Test]
    public async Task CollidingVectorLabels_FallBackToPropertyName() {
        using var client = ClientWith(OnOff("ONOFF0", on: false), OnOff("ONOFF1", on: false));
        var sw = new IndiSwitch(client, Dev);
        await sw.RefreshAsync();

        var names = sw.Channels.Select(c => c.Name).ToList();
        Assert.That(names, Is.EquivalentTo(new[] { "On/Off (ONOFF0)", "On/Off (ONOFF1)" }));
    }

    /// <summary>AnyOfMany vectors (Pegasus-style hubs, one vector holding all
    /// outlets) keep one channel per element — the previous behaviour.</summary>
    [Test]
    public async Task AnyOfManyVector_KeepsOneChannelPerElement() {
        var upb = new IndiSwitchProperty {
            Name = "POWER_CONTROL",
            Label = "Power",
            Rule = IndiSwitchRule.AnyOfMany,
            Permission = IndiPropertyPermission.ReadWrite,
            Values = { ["PORT_1"] = true, ["PORT_2"] = false },
            Labels = { ["PORT_1"] = "Mount", ["PORT_2"] = "Camera" },
        };
        using var client = ClientWith(upb);
        var sw = new IndiSwitch(client, Dev);
        await sw.RefreshAsync();

        Assert.That(sw.SwitchCount, Is.EqualTo(2));
        Assert.That(sw.Channels.Select(c => c.Name),
            Is.EquivalentTo(new[] { "Power · Mount", "Power · Camera" }));
        Assert.That(sw.Channels.Select(c => c.Key),
            Is.EquivalentTo(new[] { "POWER_CONTROL.PORT_1", "POWER_CONTROL.PORT_2" }));
    }

    /// <summary>Read-only number elements (the driver's I2C voltage/current
    /// readouts) stay read-only analog channels and keep their identity.</summary>
    [Test]
    public async Task ReadOnlyNumbers_BecomeSensorChannels() {
        var sensor = new IndiNumberProperty {
            Name = "OUT_1",
            Label = "Port 1",
            Permission = IndiPropertyPermission.ReadOnly,
            Values = {
                ["OUT1_V"] = new IndiNumberElement { Label = "Voltage (V)", Value = 12.1, Min = 0, Max = 100, Step = 1 },
                ["OUT1_A"] = new IndiNumberElement { Label = "Current (A)", Value = 0.8, Min = 0, Max = 100, Step = 1 },
            },
        };
        using var client = ClientWith(sensor);
        var sw = new IndiSwitch(client, Dev);
        await sw.RefreshAsync();

        Assert.That(sw.Channels.All(c => !c.Writable && !c.Boolean));
        Assert.That(sw.Channels.Select(c => c.Name),
            Is.EquivalentTo(new[] { "Port 1 · Voltage (V)", "Port 1 · Current (A)" }));
    }
}
