using NINA.INDI.Client;

namespace NINA.INDI.Devices;

public class IndiFocuser : NINA.Image.Interfaces.IFocuser {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    /// <summary>
    /// True only when the INDI client is up AND the device's per-device
    /// CONNECTION switch is in the CONNECT state. See
    /// <see cref="IndiCamera.IsConnected"/> for the rationale.
    /// </summary>
    public bool IsConnected
        => _client.IsConnected
           && _client.GetSwitch(DeviceName, "CONNECTION", "CONNECT");
    public int Position => (int)_client.GetNumber(DeviceName, "ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION");
    public double Temperature => _client.GetNumber(DeviceName, "FOCUS_TEMPERATURE", "TEMPERATURE");
    public int MaxPosition => (int)_client.GetNumber(DeviceName, "FOCUS_MAX", "FOCUS_MAX_VALUE");
    public bool IsMoving {
        get {
            var prop = _client.GetProperty(DeviceName, "ABS_FOCUS_POSITION");
            return prop?.State == Protocol.IndiPropertyState.Busy;
        }
    }

    public IndiFocuser(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

    public async Task MoveAbsoluteAsync(int position, CancellationToken ct = default) {
        await _client.SetNumberAsync(DeviceName, "ABS_FOCUS_POSITION",
            new Dictionary<string, double> { ["FOCUS_ABSOLUTE_POSITION"] = position }, ct);
    }

    public async Task MoveRelativeAsync(int steps, CancellationToken ct = default) {
        if (steps > 0) {
            await _client.SetSwitchAsync(DeviceName, "FOCUS_MOTION",
                new Dictionary<string, bool> { ["FOCUS_INWARD"] = false, ["FOCUS_OUTWARD"] = true }, ct);
        } else {
            await _client.SetSwitchAsync(DeviceName, "FOCUS_MOTION",
                new Dictionary<string, bool> { ["FOCUS_INWARD"] = true, ["FOCUS_OUTWARD"] = false }, ct);
        }

        await _client.SetNumberAsync(DeviceName, "REL_FOCUS_POSITION",
            new Dictionary<string, double> { ["FOCUS_RELATIVE_POSITION"] = Math.Abs(steps) }, ct);
    }

    public async Task AbortAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "FOCUS_ABORT_MOTION",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
    }
}
