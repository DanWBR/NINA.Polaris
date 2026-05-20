using NINA.Core.Enum;
using NINA.INDI.Client;

namespace NINA.INDI.Devices;

public class IndiTelescope {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public double RightAscension => _client.GetNumber(DeviceName, "EQUATORIAL_EOD_COORD", "RA");
    public double Declination => _client.GetNumber(DeviceName, "EQUATORIAL_EOD_COORD", "DEC");
    public double Altitude => _client.GetNumber(DeviceName, "HORIZONTAL_COORD", "ALT");
    public double Azimuth => _client.GetNumber(DeviceName, "HORIZONTAL_COORD", "AZ");
    public bool IsTracking => _client.GetSwitch(DeviceName, "TELESCOPE_TRACK_STATE", "TRACK_ON");
    public bool IsParked => _client.GetSwitch(DeviceName, "TELESCOPE_PARK", "PARK");
    public bool IsSlewing {
        get {
            var prop = _client.GetProperty(DeviceName, "EQUATORIAL_EOD_COORD");
            return prop?.State == Protocol.IndiPropertyState.Busy;
        }
    }

    public PierSide SideOfPier {
        get {
            if (_client.GetSwitch(DeviceName, "TELESCOPE_PIER_SIDE", "PIER_EAST")) return PierSide.pierEast;
            if (_client.GetSwitch(DeviceName, "TELESCOPE_PIER_SIDE", "PIER_WEST")) return PierSide.pierWest;
            return PierSide.pierUnknown;
        }
    }

    public IndiTelescope(IndiClient client, string deviceName) {
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

    public async Task SlewAsync(double ra, double dec, CancellationToken ct = default) {
        // Set coord mode to SLEW
        await _client.SetSwitchAsync(DeviceName, "ON_COORD_SET",
            new Dictionary<string, bool> { ["TRACK"] = true, ["SLEW"] = false, ["SYNC"] = false }, ct);

        await _client.SetNumberAsync(DeviceName, "EQUATORIAL_EOD_COORD",
            new Dictionary<string, double> { ["RA"] = ra, ["DEC"] = dec }, ct);
    }

    public async Task SyncAsync(double ra, double dec, CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "ON_COORD_SET",
            new Dictionary<string, bool> { ["TRACK"] = false, ["SLEW"] = false, ["SYNC"] = true }, ct);

        await _client.SetNumberAsync(DeviceName, "EQUATORIAL_EOD_COORD",
            new Dictionary<string, double> { ["RA"] = ra, ["DEC"] = dec }, ct);
    }

    public async Task ParkAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_PARK",
            new Dictionary<string, bool> { ["PARK"] = true, ["UNPARK"] = false }, ct);
    }

    public async Task UnparkAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_PARK",
            new Dictionary<string, bool> { ["PARK"] = false, ["UNPARK"] = true }, ct);
    }

    public async Task SetTrackingAsync(bool enabled, CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_TRACK_STATE",
            new Dictionary<string, bool> { ["TRACK_ON"] = enabled, ["TRACK_OFF"] = !enabled }, ct);
    }

    public async Task AbortSlewAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_ABORT_MOTION",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
    }

    public async Task MoveNorthAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_MOTION_NS",
            new Dictionary<string, bool> { ["MOTION_NORTH"] = true, ["MOTION_SOUTH"] = false }, ct);
    }

    public async Task MoveSouthAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_MOTION_NS",
            new Dictionary<string, bool> { ["MOTION_NORTH"] = false, ["MOTION_SOUTH"] = true }, ct);
    }

    public async Task MoveEastAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_MOTION_WE",
            new Dictionary<string, bool> { ["MOTION_WEST"] = false, ["MOTION_EAST"] = true }, ct);
    }

    public async Task MoveWestAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_MOTION_WE",
            new Dictionary<string, bool> { ["MOTION_WEST"] = true, ["MOTION_EAST"] = false }, ct);
    }

    public async Task StopMotionAsync(CancellationToken ct = default) {
        await AbortSlewAsync(ct);
    }
}
