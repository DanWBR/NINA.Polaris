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
/// ASCOM Platform FilterWheel adapter built on <see cref="ASCOM.Com.DriverAccess.FilterWheel"/>,
/// the ASCOM Platform 7 library's COM DriverAccess wrapper — the same path NINA
/// uses. Going through DriverAccess (instead of raw
/// <c>Type.GetTypeFromProgID</c> + <c>IDispatch</c> + a hand-rolled STA message
/// pump) is what lets a .NET AnyCPU driver such as a DIY MilkyWheel load and
/// connect in the 64-bit host; the raw path fast-failed those drivers
/// (0xC0000409) on activate/connect.
///
/// <para>DriverAccess manages the COM apartment/threading itself, so no
/// per-driver STA dispatcher is needed here; calls run on the thread pool via
/// <see cref="Task.Run(System.Action)"/>, matching NINA. Position semantics
/// follow the ASCOM spec: -1 means "still moving", folded to the last known
/// slot so pollers never see the transient negative.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComFilterWheel : IFilterWheel, IDisposable {
    private readonly string _progId;
    private ASCOM.Com.DriverAccess.FilterWheel? _fw;
    private string _deviceName = "ASCOM Filter Wheel";
    private string[] _names = Array.Empty<string>();
    private int _lastPosition;

    public AscomComFilterWheel(string progId) {
        _progId = progId ?? throw new ArgumentNullException(nameof(progId));
    }

    public string DeviceName => _deviceName;

    public bool IsConnected {
        get { try { return _fw?.Connected ?? false; } catch { return false; } }
    }

    public int Position {
        get {
            try {
                int raw = _fw?.Position ?? -1;
                if (raw < 0) return _lastPosition;
                _lastPosition = raw;
                return raw;
            } catch { return _lastPosition; }
        }
    }

    public bool IsMoving {
        get { try { return (_fw?.Position ?? -1) < 0; } catch { return false; } }
    }

    public string[] FilterNames => _names;
    public int FilterCount => _names.Length;
    public string CurrentFilterName {
        get {
            var p = Position;
            return (p >= 0 && p < _names.Length) ? _names[p] : "";
        }
    }

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() => {
        var fw = new ASCOM.Com.DriverAccess.FilterWheel(_progId);
        AscomComActivation.Note($"driveraccess filterwheel about to set Connected=true progId={_progId}");
        try {
            fw.Connected = true;
        } catch (Exception ex) {
            try { fw.Dispose(); } catch { }
            throw AscomComActivation.ConnectFailed(_progId, ex);
        }
        AscomComActivation.Note($"driveraccess filterwheel Connected=true OK progId={_progId}");
        _fw = fw;

        try { _deviceName = fw.Name; } catch { _deviceName = _progId; }
        try { _names = fw.Names ?? Array.Empty<string>(); } catch { _names = Array.Empty<string>(); }
        try {
            int p = fw.Position;
            _lastPosition = p < 0 ? 0 : p;
        } catch { _lastPosition = 0; }
    }, ct);

    public Task DisconnectAsync(CancellationToken ct = default) => Task.Run(() => {
        var fw = _fw;
        if (fw == null) return;
        try { fw.Connected = false; } catch { }
        try { fw.Dispose(); } catch { }
        _fw = null;
    }, ct);

    public Task SetPositionAsync(int position, CancellationToken ct = default) => Task.Run(() => {
        var fw = _fw;
        if (fw == null) return;
        var slot = Math.Clamp(position, 0, Math.Max(0, _names.Length - 1));
        fw.Position = (short)slot;
    }, ct);

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
    }
}
