using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiWeather {
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

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

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
}
