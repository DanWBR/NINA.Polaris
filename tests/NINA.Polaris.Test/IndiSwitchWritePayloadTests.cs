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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// What a power box write puts on the wire.
///
/// <para>INDI's <c>PowerInterface</c> is the base class under the SVBONY
/// SV241 Pro, Pegasus and the other power boxes on current INDI core. Its
/// switch handler walks the vector by element index while indexing the array
/// of states the client sent (libs/indibase/indipowerinterface.cpp):</para>
///
/// <code>
/// for (i = 0; i &lt; PowerChannelsSP.size(); i++)
///     if (PowerChannelsSP[i].getState() != states[i])
///         SetPowerPort(i, states[i] == ISS_ON);
/// </code>
///
/// <para>With a one-element write, n is 1: states[0] is applied to channel 0
/// whichever channel was named, and indices 1 and up read past the end of the
/// array. Reported on an SV241 Pro: DC 1 on brought DC 2 with it, and DC 2 off
/// switched DC 1 off instead. The same handlers index the client's values[]
/// array by position for dew duty cycle and variable voltage.</para>
///
/// <para>Sending the whole vector makes states[i] line up with element i,
/// which is what Ekos does and why upstream never saw this. These pin that
/// down, because a payload trimmed back to one element for tidiness would
/// bring the fault back.</para>
/// </summary>
[TestFixture]
public class IndiSwitchWritePayloadTests {

    /// <summary>The SV241 Pro's POWER_CHANNELS: ISR_NOFMANY, one element per
    /// DC outlet.</summary>
    private static Dictionary<string, bool> Outlets(params bool[] on)
        => on.Select((state, i) => (Key: $"DC_{i + 1}", state))
             .ToDictionary(x => x.Key, x => x.state);

    [Test]
    public void AnyOfMany_SendsEveryElementSoTheDriverIndicesLineUp() {
        var current = Outlets(false, false, false, false, false);

        var payload = IndiSwitch.SwitchPayload(current, IndiSwitchRule.AnyOfMany,
            element: "DC_2", offElement: null, on: true);

        Assert.Multiple(() => {
            Assert.That(payload.Keys, Is.EquivalentTo(current.Keys),
                "a short payload makes the driver apply states[0] to channel 0");
            Assert.That(payload["DC_2"], Is.True);
            foreach (var other in new[] { "DC_1", "DC_3", "DC_4", "DC_5" })
                Assert.That(payload[other], Is.False, other + " was not asked to change");
        });
    }

    /// <summary>The reported sequence, as the driver sees it. DC 1 and DC 2 are
    /// on; the operator switches DC 2 off. Only element 2 differs from the
    /// device's current state, so SetPowerPort fires once, for DC 2. Before the
    /// fix the single-element payload made element 0 the one that differed, and
    /// DC 1 went off instead.</summary>
    [Test]
    public void TurningOneOutletOffLeavesTheOthersWhereTheyWere() {
        var current = Outlets(true, true, false, false, false);

        var payload = IndiSwitch.SwitchPayload(current, IndiSwitchRule.AnyOfMany,
            element: "DC_2", offElement: null, on: false);

        var differing = current.Where(kv => payload[kv.Key] != kv.Value)
                               .Select(kv => kv.Key).ToList();
        Assert.That(differing, Is.EqualTo(new[] { "DC_2" }),
            "exactly one channel may differ from the device state, the one pressed");
    }

    /// <summary>A collapsed Off/On pair is a OneOfMany vector, which has to come
    /// back with exactly one member on. Both members are named, and nothing
    /// else is added.</summary>
    [Test]
    public void CollapsedOffOnPair_DrivesBothMembersAndNothingElse() {
        var current = new Dictionary<string, bool> { ["ONOFF0_OFF"] = true, ["ONOFF0_ON"] = false };

        var payload = IndiSwitch.SwitchPayload(current, IndiSwitchRule.OneOfMany,
            element: "ONOFF0_ON", offElement: "ONOFF0_OFF", on: true);

        Assert.Multiple(() => {
            Assert.That(payload, Has.Count.EqualTo(2));
            Assert.That(payload["ONOFF0_ON"], Is.True);
            Assert.That(payload["ONOFF0_OFF"], Is.False);
        });
    }

    /// <summary>A OneOfMany vector never gets the whole-vector treatment: with
    /// more than one member already on in the snapshot it would hand the driver
    /// an invalid vector.</summary>
    [Test]
    public void OneOfMany_IsNeverFilledOutFromTheSnapshot() {
        var current = Outlets(true, true, false);

        var payload = IndiSwitch.SwitchPayload(current, IndiSwitchRule.OneOfMany,
            element: "DC_3", offElement: null, on: true);

        Assert.Multiple(() => {
            Assert.That(payload, Has.Count.EqualTo(1));
            Assert.That(payload.Values.Count(v => v), Is.EqualTo(1));
        });
    }

    /// <summary>No snapshot (the property is not in the device yet) degrades to
    /// the single element rather than throwing.</summary>
    [Test]
    public void WithNoSnapshot_TheTargetElementIsStillWritten() {
        var payload = IndiSwitch.SwitchPayload(null, IndiSwitchRule.AnyOfMany,
            element: "DC_1", offElement: null, on: true);

        Assert.That(payload, Is.EqualTo(new Dictionary<string, bool> { ["DC_1"] = true }));
    }

    /// <summary>The helper being right is no use if the write path stops
    /// calling it, or if the number path goes back to one element.</summary>
    [Test]
    public void BothWritePathsSendTheWholeVector() {
        var here = Path.GetDirectoryName(Here())!;
        var src = File.ReadAllText(Path.GetFullPath(Path.Combine(here, "..", "..",
            "src", "NINA.INDI", "Devices", "IndiSwitch.cs")));

        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("var payload = SwitchPayload("));
            Assert.That(src, Does.Contain("foreach (var kv in prop.Values)"),
                "the number write fills the vector from the snapshot too");
            Assert.That(src, Does.Not.Contain("new Dictionary<string, double> { [m.Element] = value }"),
                "a single-element number write lands on element 0 of the vector");
        });
    }

    private static string Here([CallerFilePath] string p = "") => p;
}
