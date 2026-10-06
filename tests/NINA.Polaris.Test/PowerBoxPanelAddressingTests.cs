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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NINA.Polaris.Services.Sequencer.Instructions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The RIGS power box panel: where its channels come from, and what a toggle
/// addresses.
///
/// <para>Reported from the field on an SV241 Pro: renaming one channel put the
/// typed text into every name box at once. The panel reads its channels from
/// the websocket status block, not from GET /api/switch/status, and the
/// websocket projection was missing <c>key</c>. Every rename input binds to
/// <c>powerBoxNameDraft[ch.key]</c>, so with no key they all bound to the one
/// <c>undefined</c> slot. The same omission meant an operator-assigned name
/// could never render, because the panel shows <c>displayName || name</c>.</para>
///
/// <para>The second half is addressing. The sequencer was hardened against the
/// positional channel id years before the panel was: a saved sequence resolves
/// its outlet by the stable key and refuses to act when the key is gone. The
/// panel still posted the raw index, which is a position in whatever channel
/// map the host holds at that instant. The rule is now the same on both
/// paths.</para>
/// </summary>
[TestFixture]
public class PowerBoxPanelAddressingTests {

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Repo()
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));

    private static string Read(params string[] parts) {
        var path = Path.GetFullPath(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));
        Assert.That(File.Exists(path), $"nao achei {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Field names of the anonymous channel projection that follows
    /// <paramref name="anchor"/>, read to the first line that closes it.</summary>
    private static List<string> ProjectedFields(string source, string anchor) {
        var start = source.IndexOf(anchor, System.StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"anchor '{anchor}' is gone");
        var end = source.IndexOf("}).ToList()", start, System.StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start), "the projection no longer ends in }).ToList()");
        var body = source.Substring(start, end - start);
        return Regex.Matches(body, @"^\s*(\w+)\s*=", RegexOptions.Multiline)
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .ToList();
    }

    /// <summary>The panel reads the websocket block and the REST endpoint is
    /// what everything else reads. A field present in one and not the other is
    /// a field that silently works on one surface only, which is how the rename
    /// box ended up bound to a single shared slot.</summary>
    [Test]
    public void TheWebsocketChannelBlockCarriesEverythingTheRestOneDoes() {
        var ws = ProjectedFields(
            Read("src", "NINA.Polaris", "Services", "EquipmentManager.cs"),
            "channels = Switch.Channels.Select(c => new {");
        var rest = ProjectedFields(
            Read("src", "NINA.Polaris", "Endpoints", "SwitchEndpoints.cs"),
            "channels = equip.Switch.Channels.Select(c => new {");

        Assert.That(rest, Is.Not.Empty);
        Assert.That(ws, Is.SupersetOf(rest),
            "the RIGS panel takes its channels from the websocket block: "
            + "missing " + string.Join(", ", rest.Except(ws)));
    }

    [Test]
    public void TheWebsocketChannelBlockCarriesTheStableKeyAndTheOperatorName() {
        var ws = ProjectedFields(
            Read("src", "NINA.Polaris", "Services", "EquipmentManager.cs"),
            "channels = Switch.Channels.Select(c => new {");

        Assert.Multiple(() => {
            // Without this every rename input binds to powerBoxNameDraft[undefined].
            Assert.That(ws, Does.Contain("key"));
            // The panel renders displayName || name.
            Assert.That(ws, Does.Contain("displayName"));
            Assert.That(ws, Does.Contain("sensor"));
        });
    }

    /// <summary>A rename box is bound per channel key, so the key has to reach
    /// the browser or they all share one slot.</summary>
    [Test]
    public void TheRenameInputsAreBoundPerChannelKey() {
        var html = Read("src", "NINA.Polaris", "wwwroot", "index.html");
        Assert.That(Regex.Matches(html, @"x-model=""powerBoxNameDraft\[ch\.key\]""").Count,
            Is.EqualTo(2), "the controls list and the Readings block each have one");
    }

    /// <summary>EVERY write carries the key, from the RIGS panel and from a
    /// Control Panels widget alike. The id alone is a position in the host's
    /// current channel map, and on a power box the cost of addressing the wrong
    /// row is a mount losing power.</summary>
    [Test]
    public void EveryPowerBoxWriteCarriesTheChannelKey() {
        var js = Read("src", "NINA.Polaris", "wwwroot", "js", "app.js");

        var calls = Regex.Matches(js,
            @"'/api/switch/(set-bool|set-value|set-selected)',\s*\r?\n?\s*\{([^}]*)\}");
        Assert.That(calls.Count, Is.GreaterThanOrEqualTo(5),
            "expected the three RIGS panel writes plus the Control Panels pair");

        foreach (Match m in calls) {
            Assert.That(m.Groups[2].Value, Does.Contain("key:"),
                $"a /api/switch/{m.Groups[1].Value} call still addresses the channel "
                + "by position only: " + m.Groups[2].Value.Trim());
        }
    }

    // ---- The resolver the panel and the sequencer now share ---------------

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
    public void TryResolve_FollowsTheKeyWhenThePositionMoved() {
        var pb = new FakeSwitch(("USB.PORT_1", "USB 1"),
                                ("POWER.DC_1", "DC 1"),
                                ("POWER.DC_2", "DC 2"));
        // The row was rendered when DC 2 sat at index 1.
        Assert.That(PowerBoxTarget.TryResolve(pb, "POWER.DC_2", fallbackId: 1), Is.EqualTo(2));
    }

    /// <summary>Null, not the fallback index: a key that is gone means the map
    /// changed under the panel, and switching whatever now sits at that
    /// position is the failure being prevented. The endpoint turns this into a
    /// 409 telling the operator to refresh.</summary>
    [Test]
    public void TryResolve_RefusesAKeyThatIsNoLongerPublished() {
        var pb = new FakeSwitch(("POWER.DC_1", "DC 1"));
        Assert.That(PowerBoxTarget.TryResolve(pb, "POWER.DC_9", fallbackId: 0), Is.Null);
    }

    [Test]
    public void TryResolve_FallsBackToTheIdWhenThereIsNoKey() {
        var pb = new FakeSwitch(("POWER.DC_1", "DC 1"), ("POWER.DC_2", "DC 2"));
        Assert.Multiple(() => {
            Assert.That(PowerBoxTarget.TryResolve(pb, null, fallbackId: 1), Is.EqualTo(1));
            Assert.That(PowerBoxTarget.TryResolve(pb, "", fallbackId: 1), Is.EqualTo(1));
            Assert.That(PowerBoxTarget.TryResolve(pb, "   ", fallbackId: 0), Is.EqualTo(0));
        });
    }

    /// <summary>An unresolvable channel is a 409, not a 500: nothing failed on
    /// the device.</summary>
    [Test]
    public void TheEndpointsRefuseAnUnresolvableChannelWithoutTouchingTheDevice() {
        var src = Read("src", "NINA.Polaris", "Endpoints", "SwitchEndpoints.cs");
        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("PowerBoxTarget.TryResolve"),
                "one resolver for the panel and the sequencer, not two");
            Assert.That(src, Does.Contain("statusCode: 409"));
            Assert.That(Regex.Matches(src, @"GoneChannel\(equip\.Switch").Count, Is.EqualTo(3),
                "set-bool, set-value and set-selected each guard their target");
        });
    }

    /// <summary>The heading has to carry the number printed on the hardware.
    /// ASI Power numbers its vectors from zero (DEV0 is port 1); a box whose
    /// outlets are DC1..DC6 numbers from one, and adding 1 unconditionally put
    /// DC1 under a heading that read "Port 2".</summary>
    [Test]
    public void PortHeadingsOnlyShiftWhenTheDriverNumbersFromZero() {
        var js = Read("src", "NINA.Polaris", "wwwroot", "js", "app.js");
        Assert.Multiple(() => {
            Assert.That(js, Does.Contain("const zeroBased = order.length > 0 && order[0] === 0;"));
            Assert.That(js, Does.Contain("'Port ' + (zeroBased ? g + 1 : g)"));
            Assert.That(js, Does.Not.Contain("label: 'Port ' + (g + 1)"),
                "the unconditional +1 is what mislabelled a 1-based box");
        });
    }
}
