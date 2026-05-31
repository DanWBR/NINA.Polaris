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
    /// standard <c>TELESCOPE_HOME</c> property has THREE OneOfMany
    /// switches: <c>FIND</c> (search for home using limit switches /
    /// encoder reset), <c>SET</c> (mark current position AS home —
    /// destructive, never call from this flow), <c>GO</c> (drive to
    /// stored home position).
    ///
    /// Earlier impl sent FIND=true AND GO=true together, which is
    /// invalid for a OneOfMany switch and confused drivers — some
    /// silently did nothing. Now we inspect the live property to see
    /// which elements the driver actually advertises and pick the
    /// best available action: FIND if present (true homing routine),
    /// else GO (drive to saved position), never SET.</summary>
    public async Task FindHomeAsync(CancellationToken ct = default) {
        var existing = _client.GetProperty(DeviceName, "TELESCOPE_HOME") as Protocol.IndiSwitchProperty;
        // Drivers that don't expose TELESCOPE_HOME at all: surface as
        // a clean NotSupportedException so the endpoint returns 501
        // and the toast tells the user the mount can't home.
        if (existing == null) {
            throw new NotSupportedException(
                $"Mount '{DeviceName}' does not expose TELESCOPE_HOME — driver doesn't support Find Home.");
        }
        // OneOfMany payload: pre-fill ALL elements as false, then
        // light up exactly one. SET is intentionally never chosen
        // here (it would overwrite the stored home with whatever
        // garbage position the mount is currently at).
        var payload = existing.Values.Keys.ToDictionary(k => k, _ => false);
        string? chosen = null;
        foreach (var preferred in new[] { "FIND", "GO" }) {
            var match = payload.Keys.FirstOrDefault(
                k => string.Equals(k, preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null) { chosen = match; break; }
        }
        if (chosen == null) {
            throw new NotSupportedException(
                $"Mount '{DeviceName}' TELESCOPE_HOME exposes only [{string.Join(", ", payload.Keys)}] — no FIND or GO element available.");
        }
        payload[chosen] = true;
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_HOME", payload, ct);
    }

    /// <summary>Push the observer's geographic coordinates into the
    /// mount. The INDI standard property is <c>GEOGRAPHIC_COORD</c>
    /// with elements <c>LAT</c> / <c>LONG</c> / <c>ELEV</c>, and
    /// longitude in the 0..360 east convention -- so western
    /// hemisphere (-37° for Brazil) becomes 360 - 37 = 323°.
    ///
    /// In practice several driver families don't follow the spec
    /// strictly:
    /// <list type="bullet">
    /// <item>Element name <c>LONG</c> vs <c>LON</c> -- some drivers
    ///   accept only the short form.</item>
    /// <item>Longitude in -180..+180 east instead of 0..360 -- the
    ///   driver silently clamps or wraps and you end up at the
    ///   wrong place on Earth.</item>
    /// </list>
    /// To make Sync Location actually work everywhere, we:
    ///   1. Read the existing GEOGRAPHIC_COORD off the device
    ///      snapshot (populated by INDI getProperties) to learn
    ///      which element names this driver advertises.
    ///   2. Try to detect the longitude convention by inspecting
    ///      the CURRENT element range -- if min/max look like
    ///      -180..+180 we keep the user's signed value, otherwise
    ///      wrap to 0..360.
    ///   3. Send the write using the discovered element names.
    /// All this is logged so the operator can see exactly what was
    /// negotiated. If the property doesn't exist at all on this
    /// driver, IndiClient.LogIndiWrite surfaces a WARNING with the
    /// list of available properties so the user can find the right
    /// one (some drivers use SITE_COORD, SITE_LOCATION, etc.).</summary>
    public async Task SetSiteLocationAsync(double latitudeDeg, double longitudeDeg,
            double elevationMetres, CancellationToken ct = default) {
        // 1) Discover element names + longitude convention from the
        //    currently-announced property snapshot.
        var existing = _client.GetProperty(DeviceName, "GEOGRAPHIC_COORD") as Protocol.IndiNumberProperty;
        string latKey = "LAT";
        string lonKey = "LONG";
        string elevKey = "ELEV";
        bool useSignedLongitude = false;
        if (existing != null) {
            // Find the elements case-insensitively + pick the
            // longest matching name (LONG wins over LON if both
            // exist on the same vector, which they don't, but just
            // in case).
            string? FindKey(params string[] candidates) {
                foreach (var c in candidates) {
                    var hit = existing.Values.Keys.FirstOrDefault(
                        k => string.Equals(k, c, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return hit;
                }
                return null;
            }
            latKey  = FindKey("LAT", "LATITUDE")           ?? latKey;
            lonKey  = FindKey("LONG", "LON", "LONGITUDE")  ?? lonKey;
            elevKey = FindKey("ELEV", "ELEVATION", "HEIGHT") ?? elevKey;
            // If the existing longitude element has a min that's
            // negative, the driver wants signed -180..+180 east.
            // Otherwise default to 0..360 east per the spec.
            if (existing.Values.TryGetValue(lonKey, out var lonEl) && lonEl.Min < 0) {
                useSignedLongitude = true;
            }
        }
        // 2) Convert to the discovered convention.
        double lonOut;
        if (useSignedLongitude) {
            // -180..+180: input might be a wrapped 0..360 value
            // (e.g. 323° meaning -37°). Normalise either way.
            lonOut = longitudeDeg;
            while (lonOut >  180) lonOut -= 360;
            while (lonOut < -180) lonOut += 360;
        } else {
            // 0..360 east: wrap negatives.
            lonOut = longitudeDeg % 360.0;
            if (lonOut < 0) lonOut += 360.0;
        }
        // 3) Send. IndiClient.LogIndiWrite handles the "property
        //    not found" diagnostic warning for free.
        await _client.SetNumberAsync(DeviceName, "GEOGRAPHIC_COORD",
            new Dictionary<string, double> {
                [latKey]  = latitudeDeg,
                [lonKey]  = lonOut,
                [elevKey] = elevationMetres
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

    /// <summary>Push UTC + offset into the mount via the INDI standard
    /// <c>TIME_UTC</c> text property. Elements <c>UTC</c> (ISO-8601
    /// UTC timestamp) and <c>OFFSET</c> (local timezone offset in
    /// hours east of UTC).
    ///
    /// Mount uses (lat, lon, utc) together to compute local sidereal
    /// time — without it the GoTo math goes off by ~15 arcseconds per
    /// wall-clock second of error. Pair with SetSiteLocation after
    /// connect.</summary>
    public async Task SetSiteTimeAsync(DateTime utc, double offsetHoursFromUtc,
            CancellationToken ct = default) {
        // INDI wants ISO-8601 with no timezone marker (it's implicit
        // UTC per the property name). The standard format used by
        // libindi internally is yyyy-MM-ddTHH:mm:ss; we add fractional
        // seconds since most drivers tolerate them and they help when
        // the offset is itself a non-integer (e.g. India UTC+5:30).
        var utcStr = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture);
        var offsetStr = offsetHoursFromUtc.ToString("F2",
            System.Globalization.CultureInfo.InvariantCulture);
        await _client.SetTextAsync(DeviceName, "TIME_UTC",
            new Dictionary<string, string> {
                ["UTC"]    = utcStr,
                ["OFFSET"] = offsetStr
            }, ct);
    }

    /// <summary>Select tracking rate model via the INDI standard
    /// <c>TELESCOPE_TRACK_MODE</c> switch (OneOfMany). Elements per
    /// spec: <c>TRACK_SIDEREAL</c>, <c>TRACK_SOLAR</c>,
    /// <c>TRACK_LUNAR</c>, plus an optional <c>TRACK_CUSTOM</c> that
    /// requires a separate TELESCOPE_TRACK_RATE write. Drivers may
    /// not implement every mode — we pre-fill ALL advertised elements
    /// as false, then light up the one matching the user's choice.
    /// If the chosen mode isn't on this driver, NotSupportedException
    /// surfaces as a 501 + actionable toast.</summary>
    public async Task SetTrackingModeAsync(NINA.Image.Interfaces.TrackingMode mode,
            CancellationToken ct = default) {
        var existing = _client.GetProperty(DeviceName, "TELESCOPE_TRACK_MODE") as Protocol.IndiSwitchProperty;
        if (existing == null) {
            throw new NotSupportedException(
                $"Mount '{DeviceName}' does not expose TELESCOPE_TRACK_MODE — driver doesn't support tracking-mode selection.");
        }
        var wanted = mode switch {
            NINA.Image.Interfaces.TrackingMode.Solar  => "TRACK_SOLAR",
            NINA.Image.Interfaces.TrackingMode.Lunar  => "TRACK_LUNAR",
            _                                          => "TRACK_SIDEREAL"
        };
        var match = existing.Values.Keys.FirstOrDefault(
            k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
        if (match == null) {
            throw new NotSupportedException(
                $"Mount '{DeviceName}' TELESCOPE_TRACK_MODE has no '{wanted}' element. Available: [{string.Join(", ", existing.Values.Keys)}]");
        }
        var payload = existing.Values.Keys.ToDictionary(k => k, k => k == match);
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_TRACK_MODE", payload, ct);
    }
}
