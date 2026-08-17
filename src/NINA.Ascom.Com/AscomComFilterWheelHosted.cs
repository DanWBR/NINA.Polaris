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
/// WINEXIT-2: the only ASCOM COM <see cref="IFilterWheel"/>. Runs the driver
/// out-of-process in a minimal self-relaunched child (<see cref="AscomHostChannel"/>
/// + <see cref="AscomComHostRunner"/>, using ASCOM Platform DriverAccess), and
/// marshals every call to it. A driver that fast-fails on connect inside the
/// loaded server process connects (or throws a clean error) in the clean child;
/// and if it crashes anyway, only the child dies — the API server surfaces a
/// clean <see cref="AscomHostException"/> and stays up.
///
/// <para>Position semantics follow the ASCOM spec: -1 ("moving") is folded to
/// the last known slot so pollers never see the transient negative.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComFilterWheelHosted : IFilterWheel, IDisposable {
    private readonly string _progId;
    private AscomHostChannel? _channel;
    private string _deviceName = "ASCOM Filter Wheel";
    private string[] _names = Array.Empty<string>();
    private int _lastPosition;

    public AscomComFilterWheelHosted(string progId) {
        _progId = progId ?? throw new ArgumentNullException(nameof(progId));
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
        var channel = AscomHostChannel.Start();
        _channel = channel;
        await channel.ActivateAsync(_progId, ct).ConfigureAwait(false);
        try {
            await channel.SetAsync("Connected", true, ct).ConfigureAwait(false);
        } catch (AscomHostException ex) {
            channel.Dispose();
            _channel = null;
            // 0x800706BA == the isolated driver process died (crashed) rather
            // than returning a driver error. Point the user at Alpaca, which
            // hosts these old drivers in the desktop environment they need.
            if (ex.HResult == unchecked((int)0x800706BA)) {
                throw new InvalidOperationException(
                    $"The ASCOM driver '{_progId}' crashed while connecting. Polaris stayed up — only " +
                    "the isolated driver process was affected. This driver needs a Windows desktop " +
                    "app environment (like NINA) that a headless server does not provide. Connect this " +
                    "wheel over ASCOM Remote → Alpaca instead: in RIGS, choose the Alpaca driver for it.",
                    ex);
            }
            // A real driver error (e.g. wrong COM port) — surface it with its HRESULT.
            throw AscomComActivation.ConnectFailed(_progId, ex);
        }
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
            try { await channel.SetAsync("Connected", false, ct).ConfigureAwait(false); } catch { }
        }
        channel.Dispose();
        _channel = null;
    }

    public Task SetPositionAsync(int position, CancellationToken ct = default) {
        var channel = _channel;
        if (channel == null || channel.Dead) return Task.CompletedTask;
        var slot = Math.Clamp(position, 0, Math.Max(0, _names.Length - 1));
        return channel.SetAsync("Position", slot, ct);
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

    private T Read<T>(Func<Task<T>> read, T fallback) {
        var channel = _channel;
        if (channel == null || channel.Dead) return fallback;
        try { return read().GetAwaiter().GetResult(); }
        catch { return fallback; }
    }
}
