using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiRotator {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public double Position => _client.GetNumber(DeviceName, "ABS_ROTATOR_ANGLE", "ANGLE");

    public bool IsMoving {
        get {
            var prop = _client.GetProperty(DeviceName, "ABS_ROTATOR_ANGLE");
            return prop?.State == IndiPropertyState.Busy;
        }
    }

    public bool IsReversed => _client.GetSwitch(DeviceName, "ROTATOR_REVERSE", "INDI_ENABLED");

    public IndiRotator(IndiClient client, string deviceName) {
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

    public async Task MoveToAsync(double degrees, CancellationToken ct = default) {
        // INDIROB-1: ack-based. Rotator rejects are common (limit
        // switches, mechanical end-stops, calibration drift) and need
        // to surface as toasts rather than silent no-ops.
        var ack = await _client.SetNumberAsyncAck(DeviceName, "ABS_ROTATOR_ANGLE",
            new Dictionary<string, double> { ["ANGLE"] = degrees }, ct: ct);
        if (ack.Rejected) {
            var detail = string.IsNullOrEmpty(ack.AlertMessage)
                ? "(no message from driver)"
                : ack.AlertMessage;
            throw new InvalidOperationException(
                $"Rotator '{DeviceName}' rejected move to {degrees:F2}°: {detail}");
        }
    }

    public async Task ReverseAsync(bool reversed, CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "ROTATOR_REVERSE",
            new Dictionary<string, bool> { ["INDI_ENABLED"] = reversed, ["INDI_DISABLED"] = !reversed }, ct);
    }

    public async Task AbortAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "ROTATOR_ABORT_MOTION",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
    }

    private void OnPropertyChanged(string device, IndiProperty prop) {
        if (device != DeviceName) return;
        // Could raise events for UI updates here
    }
}
