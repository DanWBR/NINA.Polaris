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
/// <item>each element of a (non-system) switch vector → a boolean channel,
///   named "&lt;vector&gt; · &lt;element&gt;" so identically-labelled elements of
///   different vectors (four "Camera" entries, one per port) stay apart;</item>
/// <item>a two-element OneOfMany Off/On vector → a SINGLE boolean channel
///   named after the vector, writing both members on toggle;</item>
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

    // OffElement is set only for a collapsed OneOfMany Off/On pair: the
    // element that has to be driven to the opposite state so the vector keeps
    // exactly one member on (a 1OfMany vector with nothing on is invalid).
    private readonly record struct ChannelMap(string Property, string Element, bool IsSwitch,
        string Name, double Min, double Max, double Step, bool Writable,
        string? OffElement = null);

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

            // The vector's own label is the only thing that says WHICH outlet
            // an element belongs to: indi_asi_power publishes four identical
            // 9-element type selectors whose elements are all labelled
            // "Camera", "Focuser", … and are only distinguished by their
            // vector ("Port 1" … "Port 4"). Without the prefix the UI is an
            // undifferentiated wall of toggles.
            var vector = string.IsNullOrWhiteSpace(prop.Label) ? name : prop.Label;

            if (prop is IndiSwitchProperty sw) {
                bool writable = prop.Permission != IndiPropertyPermission.ReadOnly;
                var elems = sw.Values.Keys.ToList();

                // A genuine Off/On pair under the OneOfMany rule is ONE physical
                // outlet, not two channels — publish a single toggle bound to
                // the "on" member (indi_asi_power's ONOFF<n>, and the same shape
                // in several relay/dust-cap drivers). Require BOTH an on-labelled
                // and an off-labelled member, so a two-value *selector* (e.g. a
                // "None / Camera" port-type vector) is NOT mistaken for a toggle.
                if (sw.Rule == IndiSwitchRule.OneOfMany && elems.Count == 2) {
                    var onElem = elems.FirstOrDefault(e => IsOnLabel(ElementLabel(sw, e)));
                    var offElem = elems.FirstOrDefault(e => IsOffLabel(ElementLabel(sw, e)));
                    if (onElem != null && offElem != null
                        && !string.Equals(onElem, offElem, StringComparison.Ordinal)) {
                        _map.Add(new ChannelMap(name, onElem, IsSwitch: true, vector,
                            Min: 0, Max: 1, Step: 1, Writable: writable, OffElement: offElem));
                        continue;
                    }
                }

                foreach (var elem in elems) {
                    _map.Add(new ChannelMap(name, elem, IsSwitch: true,
                        Qualify(vector, ElementLabel(sw, elem)),
                        Min: 0, Max: 1, Step: 1, Writable: writable));
                }
            } else if (prop is IndiNumberProperty num) {
                bool writable = prop.Permission != IndiPropertyPermission.ReadOnly;
                foreach (var pair in num.Values) {
                    var elem = pair.Value;
                    var label = string.IsNullOrWhiteSpace(elem.Label) ? pair.Key : elem.Label;
                    _map.Add(new ChannelMap(name, pair.Key, IsSwitch: false,
                        Qualify(vector, label),
                        elem.Min, elem.Max, elem.Step, writable));
                }
            }
        }

        DisambiguateNames();
    }

    /// <summary>Vector labels are not unique either: indi_asi_power labels all
    /// four of its on/off vectors "On/Off" and all four PWM vectors "Duty
    /// Cycle". Whatever is still colliding after qualification gets the INDI
    /// property name appended, so every row is addressable before the operator
    /// renames it.</summary>
    private void DisambiguateNames() {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _map)
            counts[m.Name] = counts.TryGetValue(m.Name, out var c) ? c + 1 : 1;

        for (int i = 0; i < _map.Count; i++) {
            if (counts[_map[i].Name] > 1)
                _map[i] = _map[i] with { Name = $"{_map[i].Name} ({_map[i].Property})" };
        }
    }

    private static string ElementLabel(IndiSwitchProperty sw, string elem)
        => sw.Labels.TryGetValue(elem, out var l) && !string.IsNullOrWhiteSpace(l) ? l : elem;

    /// <summary>"Port 1" + "Camera" → "Port 1 · Camera"; collapses to the bare
    /// element name when the vector adds nothing (single-element vectors like
    /// a dew PWM rail, or a vector labelled the same as its element).</summary>
    private static string Qualify(string vector, string element)
        => string.IsNullOrWhiteSpace(vector)
           || vector.Equals(element, StringComparison.OrdinalIgnoreCase)
            ? element
            : $"{vector} · {element}";

    private static bool IsOnLabel(string label) => label.Trim() switch {
        var s when s.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("enable", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("enabled", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
        _ => false,
    };

    private static bool IsOffLabel(string label) => label.Trim() switch {
        var s when s.Equals("off", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("disable", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("disabled", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("false", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("no", StringComparison.OrdinalIgnoreCase) => true,
        _ => false,
    };

    public IReadOnlyList<SwitchChannel> Channels {
        get {
            var list = new List<SwitchChannel>(_map.Count);
            for (int i = 0; i < _map.Count; i++) {
                var m = _map[i];
                double value = m.IsSwitch
                    ? (_client.GetSwitch(DeviceName, m.Property, m.Element) ? 1 : 0)
                    : _client.GetNumber(DeviceName, m.Property, m.Element);
                // PROPERTY.ELEMENT is the closest thing INDI has to a durable
                // identity for a channel, and unlike the positional id it holds
                // across reconnects -- which is what operator-assigned names are
                // stored against.
                list.Add(new SwitchChannel(i, m.Name, m.IsSwitch, value,
                    m.Min, m.Max, m.Step, m.Writable, $"{m.Property}.{m.Element}"));
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
        // AnyOfMany outlets: writing the single target element toggles just it.
        // A collapsed OneOfMany pair also needs its sibling driven to the
        // opposite state, or the vector would end up with no member on and the
        // driver would reject (or silently keep) the write.
        var payload = new Dictionary<string, bool> { [m.Element] = on };
        if (m.OffElement is { } off) payload[off] = !on;
        // Ack-based so a driver rejection surfaces as an error.
        var ack = await _client.SetSwitchAsyncAck(DeviceName, m.Property, payload, ct: ct);
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
