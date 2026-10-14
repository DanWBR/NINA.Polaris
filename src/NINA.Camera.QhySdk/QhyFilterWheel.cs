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
using static NINA.Camera.QhySdk.Native.QhyNative;

namespace NINA.Camera.QhySdk;

/// <summary>
/// A QHY filter wheel through the camera it hangs on: a CFW on the camera's
/// 4-pin port, or one built into the camera as on the MiniCAM8. The QHYCCD SDK
/// has no separate wheel device; the wheel is driven through the camera's own
/// handle (IsQHYCCDCFWPlugged, CONTROL_CFWSLOTSNUM, SendOrder2QHYCCDCFW,
/// GetQHYCCDCFWStatus), so the device id is the camera's SDK id and the handle
/// is shared with <see cref="QhySdkCamera"/> through <see cref="QhyRegistry"/>.
/// Either can connect first.
///
/// <para>Positions are zero-based, as IFilterWheel defines them and as the SDK
/// counts: the order for the first slot is "0". This follows the same calls
/// and status codes as N.I.N.A.'s QHY filter wheel.</para>
/// </summary>
public sealed class QhyFilterWheel : IFilterWheel {
    private readonly string _cameraId;
    private readonly object _gate = new();
    private object _sdk = new();
    private IntPtr _handle;
    private volatile bool _connected;
    private string[] _names = Array.Empty<string>();
    private int _lastPosition;
    private int? _target;

    /// <summary>How long a move may take before it counts as stuck. A CFW3
    /// crossing the whole wheel takes a few seconds; one that initialises
    /// after power-up takes longer.</summary>
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(60);

    public QhyFilterWheel(string cameraId) {
        QhyRegistry.EnsureResolver();
        _cameraId = cameraId ?? "";
        DeviceName = WheelName(QhyRegistry.ModelFromId(_cameraId), 0);
    }

    public string DeviceName { get; private set; }
    public bool IsConnected => _connected;
    public string[] FilterNames => _names;
    public int FilterCount => _names.Length;

    public string CurrentFilterName {
        get {
            var p = Position;
            return p >= 0 && p < _names.Length ? _names[p] : "";
        }
    }

    /// <summary>The slot the wheel stands on. While it moves this stays on the
    /// last slot it stood on, so a caller never sees a transient -1.</summary>
    public int Position {
        get {
            Refresh();
            return _lastPosition;
        }
    }

    public bool IsMoving => _connected && Refresh() != WheelState.Stopped;

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() => {
        lock (_gate) {
            if (_connected) return;
            var (h, model) = QhyRegistry.Open(_cameraId);
            if (h == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Failed to open the QHY camera that carries the filter wheel (OpenQHYCCD). " +
                    "If an INDI driver is connected to this camera, disconnect it before using the native SDK.");
            try {
                _sdk = QhyRegistry.CallLock(h);
                int slots;
                lock (_sdk) {
                    // The camera's own connect runs InitQHYCCD; a wheel
                    // connected on its own has to, or the CFW port does not
                    // answer.
                    if (!QhyRegistry.IsCameraInitialized(h)) {
                        SetQHYCCDStreamMode(h, SingleFrameMode);
                        var rc = InitQHYCCD(h);
                        if (rc != QHYCCD_SUCCESS)
                            throw new InvalidOperationException($"QHY InitQHYCCD failed: 0x{rc:X8}");
                        QhyRegistry.MarkCameraInitialized(h);
                    }
                    if (IsQHYCCDCFWPlugged(h) != QHYCCD_SUCCESS)
                        throw new InvalidOperationException(
                            $"The QHY camera {model} reports no filter wheel. Check the wheel's cable and power.");
                    slots = (int)Math.Round(GetQHYCCDParam(h, CONTROL_ID.CONTROL_CFWSLOTSNUM));
                }
                if (slots is <= 0 or > 16)
                    throw new InvalidOperationException(
                        $"The QHY camera {model} reports a filter wheel with {slots} slots.");
                _handle = h;
                _names = Enumerable.Range(1, slots).Select(i => $"Filter {i}").ToArray();
                DeviceName = WheelName(model, slots);
                _target = null;
                _connected = true;
                Refresh();
            } catch {
                _handle = IntPtr.Zero;
                QhyRegistry.Close(h);
                throw;
            }
        }
    }, ct);

    public Task DisconnectAsync(CancellationToken ct = default) => Task.Run(() => {
        lock (_gate) {
            _connected = false;
            _target = null;
            var h = _handle;
            _handle = IntPtr.Zero;
            QhyRegistry.Close(h);
        }
    }, ct);

    public async Task SetPositionAsync(int position, CancellationToken ct = default) {
        if (!_connected) throw new InvalidOperationException("The QHY filter wheel is not connected.");
        if (position < 0 || position >= _names.Length)
            throw new ArgumentOutOfRangeException(nameof(position),
                $"Slot {position} is outside this {_names.Length}-slot wheel.");
        if (Refresh() == WheelState.Stopped && _lastPosition == position) return;

        var order = new[] { (byte)OrderFor(position) };
        uint rc;
        lock (_sdk) {
            rc = _handle == IntPtr.Zero ? QHYCCD_ERROR : SendOrder2QHYCCDCFW(_handle, order, 1);
        }
        if (rc != QHYCCD_SUCCESS)
            throw new InvalidOperationException($"The QHY filter wheel refused the move to slot {position} (0x{rc:X8}).");
        _target = position;

        var deadline = DateTime.UtcNow + MoveTimeout;
        while (DateTime.UtcNow < deadline) {
            await Task.Delay(250, ct);
            if (Refresh() == WheelState.Stopped && _lastPosition == position) return;
        }
        throw new TimeoutException($"The QHY filter wheel did not reach slot {position} within {MoveTimeout.TotalSeconds:0} s.");
    }

    public Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(filterName)) return Task.CompletedTask;
        var idx = Array.FindIndex(_names, n => string.Equals(n, filterName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new ArgumentException(
                $"Filter '{filterName}' not found in wheel (have: {string.Join(", ", _names)}).", nameof(filterName));
        return SetPositionAsync(idx, ct);
    }

    public enum WheelState { Stopped, Moving, Unknown }

    /// <summary>Read the wheel's status into <see cref="_lastPosition"/>.
    /// A camera reading out a frame holds the handle's lock for the length
    /// of the readout; the wheel then answers with what it last knew.</summary>
    private WheelState Refresh() {
        if (!_connected || _handle == IntPtr.Zero) return WheelState.Unknown;
        if (!Monitor.TryEnter(_sdk, 250)) return _target.HasValue ? WheelState.Moving : WheelState.Unknown;
        byte first;
        try {
            var buf = new byte[64];
            if (GetQHYCCDCFWStatus(_handle, buf) != QHYCCD_SUCCESS) return WheelState.Unknown;
            first = buf[0];
        } finally {
            Monitor.Exit(_sdk);
        }
        var state = ParseStatus(first, out var slot);
        if (state != WheelState.Stopped) return state;
        // An A-series wheel reports the slot it is passing while it turns, so
        // until the target shows up the move is still under way.
        if (_target is int t && slot != t) return WheelState.Moving;
        _target = null;
        _lastPosition = slot;
        return WheelState.Stopped;
    }

    /// <summary>What a GetQHYCCDCFWStatus byte means: a hex digit is the slot
    /// the wheel stands on, "N" (CFW2/CFW3) and "/" (A-series) mean it is
    /// moving, anything else is not understood.</summary>
    public static WheelState ParseStatus(byte status, out int slot) {
        slot = -1;
        char c = (char)status;
        if (c is 'N' or '/') return WheelState.Moving;
        if (c is >= '0' and <= '9') { slot = c - '0'; return WheelState.Stopped; }
        if (c is >= 'A' and <= 'F') { slot = c - 'A' + 10; return WheelState.Stopped; }
        if (c is >= 'a' and <= 'f') { slot = c - 'a' + 10; return WheelState.Stopped; }
        return WheelState.Unknown;
    }

    /// <summary>The SendOrder2QHYCCDCFW order for a zero-based slot: one hex
    /// digit.</summary>
    public static char OrderFor(int slot) => slot.ToString("X1")[0];

    private static string WheelName(string model, int slots) =>
        (string.IsNullOrWhiteSpace(model) ? "QHY" : model) + (slots > 0 ? $" {slots}-slot filter wheel" : " filter wheel");
}
