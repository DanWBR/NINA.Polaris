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

using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiDome : IDisposable {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public double Azimuth => _client.GetNumber(DeviceName, "ABS_DOME_POSITION", "DOME_ABSOLUTE_POSITION");

    public bool IsMoving {
        get {
            var prop = _client.GetProperty(DeviceName, "ABS_DOME_POSITION");
            return prop?.State == IndiPropertyState.Busy;
        }
    }

    public bool IsParked => _client.GetSwitch(DeviceName, "DOME_PARK", "PARK");
    public bool IsSlaved => _client.GetSwitch(DeviceName, "DOME_AUTOSYNC", "DOME_AUTOSYNC_ENABLE");

    public enum ShutterState {
        Open,
        Closed,
        Opening,
        Closing,
        Unknown
    }

    public ShutterState ShutterStatus {
        get {
            var prop = _client.GetProperty(DeviceName, "DOME_SHUTTER");
            if (prop == null) return ShutterState.Unknown;

            var isOpen = _client.GetSwitch(DeviceName, "DOME_SHUTTER", "SHUTTER_OPEN");
            var isClosed = _client.GetSwitch(DeviceName, "DOME_SHUTTER", "SHUTTER_CLOSE");

            if (prop.State == IndiPropertyState.Busy) {
                return isOpen ? ShutterState.Opening : ShutterState.Closing;
            }

            if (isOpen) return ShutterState.Open;
            if (isClosed) return ShutterState.Closed;
            return ShutterState.Unknown;
        }
    }

    public IndiDome(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;

        _client.PropertyChanged += OnPropertyChanged;
    }

    public Task ConnectAsync(CancellationToken ct = default)
        => _client.ConnectDeviceAsync(DeviceName, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _client.DisconnectDeviceAsync(DeviceName, ct);

    public async Task SlewToAzimuthAsync(double degrees, CancellationToken ct = default) {
        await _client.SetNumberAsync(DeviceName, "ABS_DOME_POSITION",
            new Dictionary<string, double> { ["DOME_ABSOLUTE_POSITION"] = degrees }, ct);
    }

    public async Task OpenShutterAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "DOME_SHUTTER",
            new Dictionary<string, bool> { ["SHUTTER_OPEN"] = true, ["SHUTTER_CLOSE"] = false }, ct);
    }

    public async Task CloseShutterAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "DOME_SHUTTER",
            new Dictionary<string, bool> { ["SHUTTER_OPEN"] = false, ["SHUTTER_CLOSE"] = true }, ct);
    }

    public async Task ParkAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "DOME_PARK",
            new Dictionary<string, bool> { ["PARK"] = true, ["UNPARK"] = false }, ct);
    }

    public async Task UnparkAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "DOME_PARK",
            new Dictionary<string, bool> { ["PARK"] = false, ["UNPARK"] = true }, ct);
    }

    public async Task AbortAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "DOME_ABORT_MOTION",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
    }

    private void OnPropertyChanged(string device, IndiProperty prop) {
        if (device != DeviceName) return;
        // Could raise events for UI updates here
    }

    /// <summary>Detach from the shared IndiClient. The client outlives every
    /// device object, so without this the instance stays in its delegate list
    /// for the process lifetime, and a device that is re-selected (driver
    /// recovery does exactly that) has its events handled by every past
    /// instance as well as the live one.</summary>
    public void Dispose() {
        _client.PropertyChanged -= OnPropertyChanged;
    }

}