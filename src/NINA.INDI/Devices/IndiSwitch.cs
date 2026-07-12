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

using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

/// <summary>
/// INDI power-box / switch adapter. INDI has no single "Switch" interface;
/// power drivers (e.g. <c>indi_pegasus_upb</c>, <c>indi_pegasus_ppba</c>,
/// generic relay boards) expose their outlets as switch vectors and their
/// dew/PWM rails + sensors as number vectors. This adapter flattens those
/// vectors into the generic <see cref="ISwitchDevice"/> channel model:
///
/// <list type="bullet">
/// <item>each element of a (non-system) switch vector → a boolean channel;</item>
/// <item>each writable element of a number vector → an analog channel
///   (carrying the driver's min/max/step);</item>
/// <item>each read-only number element → a read-only sensor channel
///   (voltage / current / temperature / humidity …).</item>
/// </list>
///
/// <para>Channel ids are assigned once (on connect / refresh) from a stable
/// ordering — properties sorted by name, elements in their published order —
/// so the UI's toggle for "channel 3" keeps addressing the same outlet across
/// status ticks.</para>
/// </summary>
public class IndiSwitch : ISwitchDevice {
    private readonly IndiClient _client;

    // Standard INDI system vectors that are not user-facing power channels.
    private static readonly HashSet<string> SystemVectors = new(StringComparer.OrdinalIgnoreCase) {
        "CONNECTION", "DRIVER_INFO", "CONFIG_PROCESS", "POLLING_PERIOD",
        "DEBUG", "DEBUG_LEVEL", "LOGGING_LEVEL", "SIMULATION",
        "DEVICE_PORT", "DEVICE_BAUD_RATE", "DEVICE_AUTO_SEARCH",
        "DEVICE_PORT_SCAN", "DEVICE_LAN_SEARCH", "SYSTEM_PORTS",
        "CONNECTION_MODE", "ACTIVE_DEVICES", "FIRMWARE_INFO"
    };

    private readonly record struct ChannelMap(string Property, string Element, bool IsSwitch,
        string Name, double Min, double Max, double Step, bool Writable);

    private readonly List<ChannelMap> _map = new();

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected
        && _client.GetSwitch(DeviceName, "CONNECTION", "CONNECT");

    public IndiSwitch(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.ConnectDeviceAsync(DeviceName, ct);
        // Drivers stream their property defs asynchronously after CONNECT;
        // give them a beat, then build the channel map from the snapshot.
        await Task.Delay(500, ct);
        BuildMap();
    }

    public Task DisconnectAsync(CancellationToken ct = default)
        => _client.DisconnectDeviceAsync(DeviceName, ct);

    public Task RefreshAsync(CancellationToken ct = default) {
        BuildMap();
        return Task.CompletedTask;
    }

    /// <summary>Rebuild the stable channel map from the current device
    /// snapshot. Called on connect and on explicit refresh — not on every
    /// read, so channel ids stay put.</summary>
    private void BuildMap() {
        _map.Clear();
        if (!_client.Devices.TryGetValue(DeviceName, out var props)) return;

        foreach (var kv in props.OrderBy(p => p.Key, StringComparer.Ordinal)) {
            var name = kv.Key;
            if (SystemVectors.Contains(name)) continue;
            var prop = kv.Value;

            if (prop is IndiSwitchProperty sw) {
                foreach (var elem in sw.Values.Keys) {
                    var label = sw.Labels.TryGetValue(elem, out var l) && !string.IsNullOrWhiteSpace(l) ? l : elem;
                    _map.Add(new ChannelMap(name, elem, IsSwitch: true, label,
                        Min: 0, Max: 1, Step: 1, Writable: prop.Permission != IndiPropertyPermission.ReadOnly));
                }
            } else if (prop is IndiNumberProperty num) {
                bool writable = prop.Permission != IndiPropertyPermission.ReadOnly;
                foreach (var pair in num.Values) {
                    var elem = pair.Value;
                    var label = string.IsNullOrWhiteSpace(elem.Label) ? pair.Key : elem.Label;
                    _map.Add(new ChannelMap(name, pair.Key, IsSwitch: false, label,
                        elem.Min, elem.Max, elem.Step, writable));
                }
            }
        }
    }

    public IReadOnlyList<SwitchChannel> Channels {
        get {
            var list = new List<SwitchChannel>(_map.Count);
            for (int i = 0; i < _map.Count; i++) {
                var m = _map[i];
                double value = m.IsSwitch
                    ? (_client.GetSwitch(DeviceName, m.Property, m.Element) ? 1 : 0)
                    : _client.GetNumber(DeviceName, m.Property, m.Element);
                list.Add(new SwitchChannel(i, m.Name, m.IsSwitch, value, m.Min, m.Max, m.Step, m.Writable));
            }
            return list;
        }
    }

    public int SwitchCount => _map.Count;

    public async Task SetBoolAsync(int id, bool on, CancellationToken ct = default) {
        var m = Get(id);
        if (m.IsSwitch) {
            await WriteSwitchAsync(m, on, ct);
        } else {
            // Analog channel: on == max, off == min.
            await WriteNumberAsync(m, on ? m.Max : m.Min, ct);
        }
    }

    public async Task SetValueAsync(int id, double value, CancellationToken ct = default) {
        var m = Get(id);
        if (m.IsSwitch) {
            await WriteSwitchAsync(m, value != 0, ct);
        } else {
            var clamped = m.Max > m.Min ? Math.Clamp(value, m.Min, m.Max) : value;
            await WriteNumberAsync(m, clamped, ct);
        }
    }

    private ChannelMap Get(int id) {
        if (id < 0 || id >= _map.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Power box '{DeviceName}' has no channel {id} (have {_map.Count}).");
        return _map[id];
    }

    private async Task WriteSwitchAsync(ChannelMap m, bool on, CancellationToken ct) {
        // Power outlets are AnyOfMany, so writing the single target element
        // toggles just it. Ack-based so a driver rejection surfaces as an error.
        var ack = await _client.SetSwitchAsyncAck(DeviceName, m.Property,
            new Dictionary<string, bool> { [m.Element] = on }, ct: ct);
        if (ack.Rejected)
            throw new InvalidOperationException(
                $"Power box '{DeviceName}' rejected {m.Name} = {(on ? "On" : "Off")}: "
                + (string.IsNullOrEmpty(ack.AlertMessage) ? "(no message from driver)" : ack.AlertMessage));
    }

    private async Task WriteNumberAsync(ChannelMap m, double value, CancellationToken ct) {
        var ack = await _client.SetNumberAsyncAck(DeviceName, m.Property,
            new Dictionary<string, double> { [m.Element] = value }, ct: ct);
        if (ack.Rejected)
            throw new InvalidOperationException(
                $"Power box '{DeviceName}' rejected {m.Name} = {value:0.##}: "
                + (string.IsNullOrEmpty(ack.AlertMessage) ? "(no message from driver)" : ack.AlertMessage));
    }
}
