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

using System.Runtime.Versioning;
using NINA.Image.Interfaces;

namespace NINA.Ascom.Com;

/// <summary>
/// WINEXIT-2 (#650): ASCOM FilterWheel adapter that runs the driver in an
/// out-of-process <see cref="AscomHostChannel"/> instead of in-process. Same
/// <see cref="IFilterWheel"/> surface as <see cref="AscomComFilterWheel"/>;
/// used for 32-bit-only wheels (which a 64-bit host cannot load in-process)
/// and, as a side effect, isolates a crashing wheel driver from the host.
///
/// <para>Position semantics match the in-process adapter: ASCOM's -1 "still
/// moving" sentinel is folded to the last known slot so pollers never see a
/// transient negative, and <see cref="IsMoving"/> is true while it settles.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComFilterWheelHosted : IFilterWheel, IDisposable {
    private readonly string _progId;
    private readonly bool _x86;
    private AscomHostChannel? _channel;
    private string _deviceName = "ASCOM Filter Wheel";
    private string[] _names = Array.Empty<string>();
    private int _lastPosition;

    public AscomComFilterWheelHosted(string progId, bool x86) {
        _progId = progId ?? throw new ArgumentNullException(nameof(progId));
        _x86 = x86;
    }

    public string DeviceName => _deviceName;

    public bool IsConnected =>
        _channel is { Dead: false } && Read(() => _channel!.GetBoolAsync("Connected"), false);

    public int Position {
        get {
            var raw = Read(() => _channel!.GetIntAsync("Position"), -1);
            if (raw < 0) return _lastPosition;
            _lastPosition = raw;
            return raw;
        }
    }

    public bool IsMoving => Read(() => _channel!.GetIntAsync("Position"), -1) < 0;

    public string[] FilterNames => _names;
    public int FilterCount => _names.Length;
    public string CurrentFilterName {
        get {
            var p = Position;
            return (p >= 0 && p < _names.Length) ? _names[p] : "";
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        var channel = AscomHostChannel.Start(_x86);
        _channel = channel;
        await channel.ActivateAsync(_progId, ct).ConfigureAwait(false);
        AscomComActivation.Note($"hosted filterwheel about to set Connected=true progId={_progId}");
        try {
            await channel.SetAsync("Connected", true, "bool", ct).ConfigureAwait(false);
        } catch (Exception ex) {
            throw AscomComActivation.ConnectFailed(_progId, ex);
        }
        AscomComActivation.Note($"hosted filterwheel Connected=true OK progId={_progId}");

        try { _deviceName = await channel.GetStringAsync("Name", ct).ConfigureAwait(false); }
        catch { _deviceName = _progId; }
        try { _names = await channel.GetStringArrayAsync("Names", ct).ConfigureAwait(false); }
        catch { _names = Array.Empty<string>(); }
        try {
            var p = await channel.GetIntAsync("Position", ct).ConfigureAwait(false);
            _lastPosition = p < 0 ? 0 : p;
        } catch { _lastPosition = 0; }
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        var channel = _channel;
        if (channel == null) return;
        if (!channel.Dead) {
            try { await channel.SetAsync("Connected", false, "bool", ct).ConfigureAwait(false); } catch { }
        }
        channel.Dispose();
        _channel = null;
    }

    public Task SetPositionAsync(int position, CancellationToken ct = default) {
        var channel = _channel;
        if (channel == null || channel.Dead) return Task.CompletedTask;
        var slot = Math.Clamp(position, 0, Math.Max(0, _names.Length - 1));
        // ASCOM Position is VT_I2 — tag it so the child sends a short.
        return channel.SetAsync("Position", slot, "i2", ct);
    }

    public Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(filterName)) return Task.CompletedTask;
        var idx = Array.FindIndex(_names, n =>
            string.Equals(n, filterName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new ArgumentException(
                $"Filter '{filterName}' not found in wheel (have: {string.Join(", ", _names)}).",
                nameof(filterName));
        return SetPositionAsync(idx, ct);
    }

    public void Dispose() {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        try { _channel?.Dispose(); } catch { }
        _channel = null;
    }

    /// <summary>Block a synchronous <see cref="IFilterWheel"/> property getter
    /// on an async channel call, swallowing failures to the fallback — same
    /// contract as the in-process adapter's Read helper.</summary>
    private T Read<T>(Func<Task<T>> read, T fallback) {
        var channel = _channel;
        if (channel == null || channel.Dead) return fallback;
        try { return read().GetAwaiter().GetResult(); }
        catch { return fallback; }
    }
}
