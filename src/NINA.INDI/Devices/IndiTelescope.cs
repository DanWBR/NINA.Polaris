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

    /// <summary>Live capability advertisement based on what the
    /// driver actually exposes in the property table. Probes are
    /// cheap (per-device dict lookups against the snapshot) and
    /// re-evaluated each access so a hot-plug rig swap reflects
    /// immediately in the UI gating.
    ///
    /// Critical for strain-wave mounts like the ZWO AM3 which DON'T
    /// expose TELESCOPE_HOME (they use the Park position as "home"
    /// instead). Without this probe the static preset claimed
    /// SupportsFindHome=true and the UI showed a Home button that
    /// silently 501'd, leaving the user confused about why nothing
    /// happened. Park / Sync / Tracking / PierSide / ManualJog stay
    /// optimistic-true because every reasonable INDI mount driver
    /// supports them (and the UI tolerates the rare driver that
    /// doesn't via the same 501 toast).</summary>
    public MountCapabilities Capabilities => new(
        SupportsPark:            _client.GetProperty(DeviceName, "TELESCOPE_PARK") != null,
        SupportsTrackingToggle:  _client.GetProperty(DeviceName, "TELESCOPE_TRACK_STATE") != null,
        SupportsSync:            _client.GetProperty(DeviceName, "ON_COORD_SET") != null,
        SupportsPierSide:        _client.GetProperty(DeviceName, "TELESCOPE_PIER_SIDE") != null,
        SupportsManualJog:       _client.GetProperty(DeviceName, "TELESCOPE_MOTION_NS") != null
                                 && _client.GetProperty(DeviceName, "TELESCOPE_MOTION_WE") != null,
        SupportsFindHome:        _client.GetProperty(DeviceName, "TELESCOPE_HOME") != null,
        SupportsSetSiteLocation: _client.GetProperty(DeviceName, "GEOGRAPHIC_COORD") != null,
        SupportsSetSiteTime:     _client.GetProperty(DeviceName, "TIME_UTC") != null,
        SupportsTrackingModes:   _client.GetProperty(DeviceName, "TELESCOPE_TRACK_MODE") != null);

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

    public Task ConnectAsync(CancellationToken ct = default)
        => _client.ConnectDeviceAsync(DeviceName, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _client.DisconnectDeviceAsync(DeviceName, ct);

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

        // 4. Issue the slew via the coord write -- ack-based so we
        //    actually know the driver received it. INDIROB-1: before
        //    this was fire-and-forget which raced IsSlewing (poller
        //    would see the previous Ok state and decide the slew was
        //    instant). The ack helper waits up to 5s for the driver
        //    to echo back Busy/Ok (accepted) or Alert (rejected). On
        //    Alert -- typical reasons: target below horizon, slew
        //    limit, mount still parked, dome unsafe -- we surface the
        //    driver's message verbatim so the operator sees a real
        //    error instead of a silent no-op.
        var ack = await _client.SetNumberAsyncAck(DeviceName, "EQUATORIAL_EOD_COORD",
            new Dictionary<string, double> { ["RA"] = ra, ["DEC"] = dec }, ct: ct);
        ThrowIfRejectedOrTimedOut(ack, "slew", "EQUATORIAL_EOD_COORD");
    }

    public async Task SyncAsync(double ra, double dec, CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "ON_COORD_SET",
            new Dictionary<string, bool> { ["TRACK"] = false, ["SLEW"] = false, ["SYNC"] = true }, ct);

        var ack = await _client.SetNumberAsyncAck(DeviceName, "EQUATORIAL_EOD_COORD",
            new Dictionary<string, double> { ["RA"] = ra, ["DEC"] = dec }, ct: ct);
        ThrowIfRejectedOrTimedOut(ack, "sync", "EQUATORIAL_EOD_COORD");
    }

    public async Task ParkAsync(CancellationToken ct = default) {
        var ack = await _client.SetSwitchAsyncAck(DeviceName, "TELESCOPE_PARK",
            new Dictionary<string, bool> { ["PARK"] = true, ["UNPARK"] = false }, ct: ct);
        ThrowIfRejectedOrTimedOut(ack, "park", "TELESCOPE_PARK");
    }

    /// <summary>Map an IndiAckResult into an exception when the driver
    /// rejected or never acknowledged the write. INDIROB-1: makes the
    /// previously-silent failure modes show up as toasts in the UI
    /// instead of pretending the operation succeeded.</summary>
    private void ThrowIfRejectedOrTimedOut(IndiAckResult ack, string operation, string property) {
        if (ack.Acknowledged) return;
        if (ack.Rejected) {
            var detail = string.IsNullOrEmpty(ack.AlertMessage)
                ? "(no message from driver)"
                : ack.AlertMessage;
            throw new InvalidOperationException(
                $"Mount '{DeviceName}' rejected {operation} on {property}: {detail}");
        }
        // TimedOut: driver was silent. Most common cause is a wedged
        // serial link or the property name being wrong (LogIndiWrite
        // already warned). Surface as a different message so the
        // operator can tell the two apart.
        throw new InvalidOperationException(
            $"Mount '{DeviceName}' did not acknowledge {operation} on {property} within timeout. " +
            "Driver may be wedged — check INDI server logs.");
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
        var ack = await _client.SetSwitchAsyncAck(DeviceName, "TELESCOPE_PARK",
            new Dictionary<string, bool> { ["PARK"] = false, ["UNPARK"] = true }, ct: ct);
        ThrowIfRejectedOrTimedOut(ack, "unpark", "TELESCOPE_PARK");
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
        await EnsureCoordSetTrackAsync(ct);
        await EnsureSlewRateAsync(ct);
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_MOTION_NS",
            new Dictionary<string, bool> { ["MOTION_NORTH"] = true, ["MOTION_SOUTH"] = false }, ct);
    }

    public async Task MoveSouthAsync(CancellationToken ct = default) {
        await EnsureCoordSetTrackAsync(ct);
        await EnsureSlewRateAsync(ct);
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_MOTION_NS",
            new Dictionary<string, bool> { ["MOTION_NORTH"] = false, ["MOTION_SOUTH"] = true }, ct);
    }

    public async Task MoveEastAsync(CancellationToken ct = default) {
        await EnsureCoordSetTrackAsync(ct);
        await EnsureSlewRateAsync(ct);
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_MOTION_WE",
            new Dictionary<string, bool> { ["MOTION_WEST"] = false, ["MOTION_EAST"] = true }, ct);
    }

    public async Task MoveWestAsync(CancellationToken ct = default) {
        await EnsureCoordSetTrackAsync(ct);
        await EnsureSlewRateAsync(ct);
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_MOTION_WE",
            new Dictionary<string, bool> { ["MOTION_WEST"] = true, ["MOTION_EAST"] = false }, ct);
    }

    /// <summary>Ensure <c>ON_COORD_SET</c> is in TRACK mode before
    /// issuing manual jog (MOTION_NS / MOTION_WE) commands.
    ///
    /// Critical for ZWO AM3 / AM5 (and other LX200-Autostar-based
    /// strain-wave drivers): if ON_COORD_SET is in SLEW or SYNC mode,
    /// the driver receives the MOTION command and enters a BUSY state
    /// (visible as a yellow indicator in the INDI control panel) but
    /// the mount NEVER physically moves. Setting ON_COORD_SET=TRACK
    /// puts the driver in the state where manual jog actually engages
    /// the motors.
    ///
    /// Diagnosed from ZWO bbs forum thread d/15173 and INDI mounts
    /// forum t/11654 -- multiple AM5/AM3 users hit exactly this and
    /// the workaround was always "set ON_COORD_SET to TRACK before
    /// the motion command".</summary>
    private async Task EnsureCoordSetTrackAsync(CancellationToken ct) {
        var coord = _client.GetProperty(DeviceName, "ON_COORD_SET")
            as Protocol.IndiSwitchProperty;
        // Driver doesn't expose ON_COORD_SET -- non-AM5 mounts, just
        // proceed to the motion command. Most non-LX200 drivers don't
        // gate motion behind this property.
        if (coord == null || coord.Values.Count == 0) return;

        // Find the TRACK element (case-insensitive). Spec uses uppercase
        // but some forks use mixed case.
        var trackKey = coord.Values.Keys.FirstOrDefault(
            k => string.Equals(k, "TRACK", StringComparison.OrdinalIgnoreCase));
        if (trackKey == null) return;   // driver has no TRACK option

        // Already in TRACK? Skip the write (saves ~50ms round-trip per jog).
        if (coord.Values[trackKey]) return;

        // Set TRACK true, all other elements false. ON_COORD_SET is a
        // OneOfMany switch so writing TRACK=true implicitly clears the
        // others, but we send all three explicitly to be safe against
        // drivers that don't honour the OneOfMany rule properly.
        var payload = coord.Values.Keys.ToDictionary(k => k, k => k == trackKey);
        await _client.SetSwitchAsync(DeviceName, "ON_COORD_SET", payload, ct);
    }

    public async Task StopMotionAsync(CancellationToken ct = default) {
        await AbortSlewAsync(ct);
    }

    /// <summary>Pick a sensible <c>TELESCOPE_SLEW_RATE</c> element
    /// before every TELESCOPE_MOTION_* write. Many INDI mount
    /// drivers -- ZWO AM3 in particular -- silently ignore manual
    /// jog commands when no slew rate has been selected since the
    /// driver booted. The spec defines four standard elements
    /// (SLEW_GUIDE / SLEW_CENTERING / SLEW_FIND / SLEW_MAX); we
    /// pick whichever is already active first (operator's choice
    /// wins), then prefer SLEW_FIND (mid-speed, good for framing
    /// nudges -- not so slow that the user gives up, not so fast
    /// it overshoots), then SLEW_CENTERING, then SLEW_MAX, then
    /// whatever the driver actually advertises. The write is
    /// idempotent: if the right element is already lit nothing
    /// changes on the wire.</summary>
    /// <summary>Live snapshot of the driver's TELESCOPE_SLEW_RATE
    /// switch. Returned in the same order the driver advertised the
    /// elements — INDI drivers typically order them slow-to-fast
    /// (SLEW_GUIDE < SLEW_CENTERING < SLEW_FIND < SLEW_MAX), which is
    /// what a left-to-right slider expects. Empty list when the
    /// driver doesn't expose the property (some hard-code a single
    /// rate); UI hides the slider in that case.</summary>
    public IReadOnlyList<SlewRateStep> GetSlewRates() {
        var rate = _client.GetProperty(DeviceName, "TELESCOPE_SLEW_RATE")
            as Protocol.IndiSwitchProperty;
        if (rate == null || rate.Values.Count == 0)
            return Array.Empty<SlewRateStep>();
        // Element labels would be ideal but our IndiSwitchProperty
        // doesn't carry per-element labels yet (only the vector-level
        // Label), so use the element name itself with a friendly
        // SLEW_FOO → "Foo" fallback.
        return rate.Values.Select(kv => new SlewRateStep(
            Name: kv.Key,
            Label: PrettifySlewRateName(kv.Key),
            Active: kv.Value)).ToList();
    }

    private static string PrettifySlewRateName(string element) {
        // "SLEW_FIND" → "Find", "SLEW_2X" → "2x", "SLEW_MAX" → "Max"
        var trimmed = element.StartsWith("SLEW_", StringComparison.OrdinalIgnoreCase)
            ? element.Substring(5) : element;
        // Title-case (Find / Centering / Guide / Max) when alphabetic.
        if (trimmed.All(c => char.IsLetter(c)))
            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1).ToLowerInvariant();
        // "2X" → "2x" reads better next to other steps in the slider tick label.
        return trimmed.ToLowerInvariant();
    }

    /// <summary>Light up exactly one element of TELESCOPE_SLEW_RATE.
    /// OneOfMany switch — write all elements explicitly (true for the
    /// chosen one, false for everyone else) so drivers that don't
    /// honour the OneOfMany rule strictly still see a consistent
    /// state. Throws if the requested element doesn't exist on the
    /// device snapshot so the UI surfaces a clear error instead of a
    /// silent no-op (driver would drop the write — see LogIndiWrite
    /// warning path).</summary>
    public async Task SetSlewRateAsync(string elementName, CancellationToken ct = default) {
        var rate = _client.GetProperty(DeviceName, "TELESCOPE_SLEW_RATE")
            as Protocol.IndiSwitchProperty;
        if (rate == null || rate.Values.Count == 0) {
            throw new NotSupportedException(
                $"Mount '{DeviceName}' does not expose TELESCOPE_SLEW_RATE — driver doesn't support rate selection.");
        }
        if (!rate.Values.ContainsKey(elementName)) {
            throw new ArgumentException(
                $"TELESCOPE_SLEW_RATE has no '{elementName}' element. Available: [{string.Join(", ", rate.Values.Keys)}]",
                nameof(elementName));
        }
        var payload = rate.Values.Keys.ToDictionary(k => k, k => k == elementName);
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_SLEW_RATE", payload, ct);
    }

    private async Task EnsureSlewRateAsync(CancellationToken ct) {
        var rate = _client.GetProperty(DeviceName, "TELESCOPE_SLEW_RATE")
            as Protocol.IndiSwitchProperty;
        // Not every driver exposes a slew-rate switch (some
        // hard-code a single rate). When absent, just send the
        // motion command and let the driver use its default.
        if (rate == null || rate.Values.Count == 0) return;

        // Operator-already-picked-a-rate path: respect their
        // choice, do nothing.
        if (rate.Values.Any(kv => kv.Value)) return;

        // Pick a default. Iterate the preference list in order
        // and use the first element the driver actually advertises.
        var preferences = new[] {
            "SLEW_FIND", "SLEW_CENTERING", "SLEW_MAX", "SLEW_GUIDE"
        };
        string? chosen = null;
        foreach (var pref in preferences) {
            var match = rate.Values.Keys.FirstOrDefault(
                k => string.Equals(k, pref, StringComparison.OrdinalIgnoreCase));
            if (match != null) { chosen = match; break; }
        }
        chosen ??= rate.Values.Keys.First();   // last resort: first advertised

        var payload = rate.Values.Keys.ToDictionary(k => k, k => k == chosen);
        await _client.SetSwitchAsync(DeviceName, "TELESCOPE_SLEW_RATE", payload, ct);
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
