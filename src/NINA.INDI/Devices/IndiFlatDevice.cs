using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiFlatDevice {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public bool IsLightOn => _client.GetSwitch(DeviceName, "FLAT_LIGHT_CONTROL", "FLAT_LIGHT_ON");

    public int Brightness => (int)_client.GetNumber(DeviceName, "FLAT_LIGHT_INTENSITY", "FLAT_LIGHT_INTENSITY_VALUE");

    public bool IsCoverOpen {
        get {
            var prop = _client.GetProperty(DeviceName, "DUSTCAP_PARK");
            if (prop == null) return false;
            return _client.GetSwitch(DeviceName, "DUSTCAP_PARK", "UNPARK");
        }
    }

    public bool IsCoverMoving {
        get {
            var prop = _client.GetProperty(DeviceName, "DUSTCAP_PARK");
            return prop?.State == IndiPropertyState.Busy;
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
        await _client.SetSwitchAsync(DeviceName, "DUSTCAP_PARK",
            new Dictionary<string, bool> { ["PARK"] = false, ["UNPARK"] = true }, ct);
    }

    public async Task CloseCoverAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "DUSTCAP_PARK",
            new Dictionary<string, bool> { ["PARK"] = true, ["UNPARK"] = false }, ct);
    }

    private void OnPropertyChanged(string device, IndiProperty prop) {
        if (device != DeviceName) return;
        // Could raise events for UI updates here
    }
}
