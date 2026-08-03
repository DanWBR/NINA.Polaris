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

public class IndiWeather : IDisposable {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public double Temperature => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_TEMPERATURE");
    public double Humidity => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_HUMIDITY");
    public double DewPoint => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_DEWPOINT");
    public double WindSpeed => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_WIND_SPEED");
    public double WindGust => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_WIND_GUST");
    public double Pressure => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_PRESSURE");
    public double CloudCover => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_CLOUD_COVER");
    public double RainRate => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_RAIN_HOUR");
    public double SkyQuality => _client.GetNumber(DeviceName, "WEATHER_PARAMETERS", "WEATHER_SQM");

    public bool IsSafe {
        get {
            var prop = _client.GetProperty(DeviceName, "WEATHER_STATUS");
            if (prop == null) return false;
            return prop.State == IndiPropertyState.Ok;
        }
    }

    public IndiWeather(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;

        _client.PropertyChanged += OnPropertyChanged;
    }

    public Task ConnectAsync(CancellationToken ct = default)
        => _client.ConnectDeviceAsync(DeviceName, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _client.DisconnectDeviceAsync(DeviceName, ct);

    public async Task RefreshAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "WEATHER_REFRESH",
            new Dictionary<string, bool> { ["REFRESH"] = true }, ct);
    }

    public IndiPropertyState GetStatus() {
        var prop = _client.GetProperty(DeviceName, "WEATHER_STATUS");
        return prop?.State ?? IndiPropertyState.Idle;
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