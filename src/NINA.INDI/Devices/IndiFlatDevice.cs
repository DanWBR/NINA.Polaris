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

public class IndiFlatDevice : IDisposable {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public bool IsLightOn => _client.GetSwitch(DeviceName, "FLAT_LIGHT_CONTROL", "FLAT_LIGHT_ON");

    public int Brightness => (int)_client.GetNumber(DeviceName, "FLAT_LIGHT_INTENSITY", "FLAT_LIGHT_INTENSITY_VALUE");

    /// <summary>The motorised cover's switch vector.
    ///
    /// INDI names it <c>CAP_PARK</c>, in <c>INDI::DustCapInterface</c>, with
    /// elements PARK and UNPARK. Polaris asked for <c>DUSTCAP_PARK</c>, which
    /// no driver publishes: opening and closing did nothing and the state on
    /// screen was whatever the default was, which is how a Gemini flat panel
    /// was reported from the field. <c>DUSTCAP_PARK</c> is kept as a fallback
    /// in case some out of tree driver really uses it; the standard name wins.
    ///
    /// Resolved per call rather than cached, because the property arrives
    /// asynchronously after connect and a cached null would stick.</summary>
    private string? CoverProp =>
        _client.GetProperty(DeviceName, "CAP_PARK") != null ? "CAP_PARK"
        : _client.GetProperty(DeviceName, "DUSTCAP_PARK") != null ? "DUSTCAP_PARK"
        : null;

    /// <summary>True when the driver has a cover at all. A plain light panel
    /// (no cap) has none, and the UI should not offer Open and Close.</summary>
    public bool HasCover => CoverProp != null;

    public bool IsCoverOpen {
        get {
            var name = CoverProp;
            if (name == null) return false;
            return _client.GetSwitch(DeviceName, name, "UNPARK");
        }
    }

    public bool IsCoverMoving {
        get {
            var name = CoverProp;
            if (name == null) return false;
            return _client.GetProperty(DeviceName, name)?.State == IndiPropertyState.Busy;
        }
    }

    public IndiFlatDevice(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;

        _client.PropertyChanged += OnPropertyChanged;
    }

    public Task ConnectAsync(CancellationToken ct = default)
        => _client.ConnectDeviceAsync(DeviceName, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _client.DisconnectDeviceAsync(DeviceName, ct);

    public async Task SetLightAsync(bool on, CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "FLAT_LIGHT_CONTROL",
            new Dictionary<string, bool> { ["FLAT_LIGHT_ON"] = on, ["FLAT_LIGHT_OFF"] = !on }, ct);
    }

    public async Task SetBrightnessAsync(int brightness, CancellationToken ct = default) {
        await _client.SetNumberAsync(DeviceName, "FLAT_LIGHT_INTENSITY",
            new Dictionary<string, double> { ["FLAT_LIGHT_INTENSITY_VALUE"] = brightness }, ct);
    }

    public async Task OpenCoverAsync(CancellationToken ct = default) {
        var name = CoverProp
            ?? throw new NotSupportedException(
                $"INDI device {DeviceName} publishes no dust cover (CAP_PARK). "
                + "A light panel without a cap cannot be opened or closed.");
        await _client.SetSwitchAsync(DeviceName, name,
            new Dictionary<string, bool> { ["PARK"] = false, ["UNPARK"] = true }, ct);
    }

    public async Task CloseCoverAsync(CancellationToken ct = default) {
        var name = CoverProp
            ?? throw new NotSupportedException(
                $"INDI device {DeviceName} publishes no dust cover (CAP_PARK). "
                + "A light panel without a cap cannot be opened or closed.");
        await _client.SetSwitchAsync(DeviceName, name,
            new Dictionary<string, bool> { ["PARK"] = true, ["UNPARK"] = false }, ct);
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