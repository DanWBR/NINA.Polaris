using NINA.Core.Enum;
using NINA.Image.Interfaces;
using NINA.INDI.Client;

namespace NINA.INDI.Devices;

public class IndiTelescope : ITelescope {
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

    /// <summary>INDI mounts come in all shapes. Default to the
    /// GEM capability profile, for the common WiFi alt-az bodies
    /// (AZ-GTi, NexStar SE) the pier-side indicator simply stays
    /// "unknown" and the UI tolerates it. A future refinement can
    /// inspect the INDI driver name and switch to <see cref="MountCapabilities.AltAz"/>
    /// when it's clearly an alt-az.</summary>
    public MountCapabilities Capabilities => MountCapabilities.GermanEquatorial;

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
        // 1. Unpark if parked. Several INDI mount drivers (ZWO AM3,
        //    iOptron, EQMod) silently swallow EQUATORIAL_EOD_COORD
        //    writes while TELESCOPE_PARK.PARK is on: the new target
        //    coords are accepted into the property but no motion is
        //    issued and no error comes back, so from the UI it looks
        //    like "I clicked slew and nothing happened". Wait briefly
        //    for the driver to flip the park state before writing the
        //    coords -- otherwise the writes race the unpark and hit
        //    a still-parked driver.
        if (IsParked) {
            await UnparkAsync(ct);
            for (int i = 0; i < 30 && IsParked; i++) {
                try { await Task.Delay(100, ct); } catch (TaskCanceledException) { break; }
            }
        }

        // 2. Force tracking on. INDI's TELESCOPE_TRACK_STATE gates
        //    motion on some drivers; setting it unconditionally is
        //    cheap (no-op when already on) and removes another silent-
        //    failure mode where the user toggled tracking off in INDI
        //    Control Panel and then expected slew to still work.
        await SetTrackingAsync(true, ct);

        // 3. Tell the driver the next coord write is a slew-and-track,
        //    not a sync or coord-only update. OneOfMany rule: pass
        //    only the on-switch as true; the other two stay false.
        await _client.SetSwitchAsync(DeviceName, "ON_COORD_SET",
            new Dictionary<string, bool> { ["TRACK"] = true, ["SLEW"] = false, ["SYNC"] = false }, ct);

        // 4. Issue the slew via the coord write.
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

    /// <summary>Drive the mount to its mechanical home. The INDI
    /// standard property is <c>TELESCOPE_HOME</c> with a single
    /// <c>FIND</c> switch (named that way in the INDI v1.9+ spec; some
    /// older drivers used <c>GO</c> instead). We send both keys in the
    /// same vector so either driver convention picks it up; harmless
    /// when a driver only knows one of the two names.</summary>
    public async Task FindHomeAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_HOME",
            new Dictionary<string, bool> {
                ["FIND"] = true,
                ["GO"] = true
            }, ct);
    }

    /// <summary>Push the observer's geographic coordinates into the
    /// mount via the INDI standard <c>GEOGRAPHIC_COORD</c> number
    /// vector. The spec says <c>LONG</c> is degrees east in 0..360,
    /// so a western hemisphere longitude (-37° for Brazil) becomes
    /// 360 - 37 = 323°. Latitudes pass through unchanged. Elevation
    /// is metres above sea level.
    ///
    /// Why this matters: the mount uses its internal lat/long for
    /// every RA/Dec → alt/az conversion (slew calc, horizon limit,
    /// LST). A wrong site location causes systematic GoTo errors
    /// that look like alignment drift but are actually a coordinate
    /// bias. Polaris already knows the observatory position from
    /// the profile; pushing it once after connect saves the user
    /// from poking the mount's hand controller menus.</summary>
    public async Task SetSiteLocationAsync(double latitudeDeg, double longitudeDeg,
            double elevationMetres, CancellationToken ct = default) {
        // Normalise longitude to the INDI 0..360 east convention.
        // Western hemisphere (negative) wraps to the equivalent
        // positive value. Eastern positive values pass through;
        // 0..360 inputs from a different convention also work.
        var lonNorm = longitudeDeg % 360.0;
        if (lonNorm < 0) lonNorm += 360.0;
        await _client.SetNumberAsync(DeviceName, "GEOGRAPHIC_COORD",
            new Dictionary<string, double> {
                ["LAT"]  = latitudeDeg,
                ["LONG"] = lonNorm,
                ["ELEV"] = elevationMetres
            }, ct);
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
