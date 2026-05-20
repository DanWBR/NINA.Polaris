using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiDome {
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

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

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
}
