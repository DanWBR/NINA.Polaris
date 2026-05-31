using System.Globalization;
using NINA.Core.Enum;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.Alpaca;

/// <summary>
/// Alpaca/ASCOM Telescope v3 adapter implementing <see cref="ITelescope"/>.
///
/// <para>Wraps the standard <c>/api/v1/telescope/{n}/...</c> endpoint set
/// (see Alpaca API spec). All HTTP plumbing (URL build, ClientID /
/// ClientTransactionID query params, error envelope) is delegated to
/// <see cref="AlpacaClient"/>; this class just maps the property /
/// method surface to <see cref="ITelescope"/>.</para>
///
/// <para>The interface exposes synchronous properties (RA, Dec,
/// Slewing, ...) while Alpaca only speaks HTTP. To bridge the gap we
/// run a small background poll loop (500 ms) once connected and cache
/// the most recent values. ConnectAsync also probes the per-driver
/// Can* flags once and freezes them into <see cref="Capabilities"/>.</para>
///
/// <para>Mirrors the layout of <c>IndiTelescope</c> so the rest of
/// Polaris (EquipmentManager, Slew &amp; Center, meridian flip, the
/// sequencer) stays backend-agnostic.</para>
/// </summary>
public sealed class AlpacaTelescope : ITelescope, IDisposable {
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultSlewTimeout = TimeSpan.FromSeconds(60);

    private readonly AlpacaClient _client;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    // Cached identity / state, refreshed by the poll loop.
    private string _deviceName = "Alpaca Telescope";
    private volatile bool _isConnected;
    private double _ra = double.NaN, _dec = double.NaN;
    private double _alt = double.NaN, _az = double.NaN;
    private bool _isTracking, _isParked, _isSlewing;
    private PierSide _sideOfPier = PierSide.pierUnknown;

    // Capability flags, frozen on ConnectAsync.
    private bool _canSync, _canPark, _canUnpark, _canSetTracking, _canPulseGuide;

    public AlpacaTelescope(string host, int port, int deviceNumber) {
        _client = new AlpacaClient(host, port, "telescope", deviceNumber);
    }

    /// <summary>
    /// Factory for the colon-separated id used by EquipmentManager
    /// (<c>"host:port:deviceNumber"</c>, e.g. <c>"192.168.1.10:11111:0"</c>).
    /// </summary>
    public static AlpacaTelescope FromDeviceId(string deviceId) {
        if (string.IsNullOrWhiteSpace(deviceId)) {
            throw new ArgumentException("deviceId must be 'host:port:deviceNumber'", nameof(deviceId));
        }
        var parts = deviceId.Split(':');
        if (parts.Length != 3
            || string.IsNullOrWhiteSpace(parts[0])
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var deviceNumber)) {
            throw new ArgumentException(
                $"Invalid Alpaca telescope id '{deviceId}'. Expected 'host:port:deviceNumber'.",
                nameof(deviceId));
        }
        return new AlpacaTelescope(parts[0], port, deviceNumber);
    }

    // ---- ITelescope: identity / state ----

    public string DeviceName => _deviceName;
    public bool IsConnected => _isConnected;

    public double RightAscension => _ra;
    public double Declination => _dec;
    public double Altitude => _alt;
    public double Azimuth => _az;

    public bool IsTracking => _isTracking;
    public bool IsParked => _isParked;
    public bool IsSlewing => _isSlewing;
    public PierSide SideOfPier => _sideOfPier;

    public MountCapabilities Capabilities => new(
        SupportsPark: _canPark && _canUnpark,
        SupportsTrackingToggle: _canSetTracking,
        SupportsSync: _canSync,
        SupportsPierSide: _sideOfPier != PierSide.pierUnknown,
        SupportsManualJog: _canPulseGuide);

    // ---- ITelescope: lifecycle ----

    public async Task ConnectAsync(CancellationToken ct = default) {
        // 1. Tell the driver to connect.
        await _client.PutAsync("connected",
            new Dictionary<string, string> { ["Connected"] = "true" }, ct);

        // 2. Pull the friendly name (best-effort).
        try {
            var name = await _client.GetAsync<string>("name", ct);
            if (!string.IsNullOrWhiteSpace(name)) _deviceName = name!;
        } catch { /* keep default */ }

        // 3. Freeze capability flags. Failures default to "not supported".
        _canSync         = await SafeBool("cansync", ct);
        _canPark         = await SafeBool("canpark", ct);
        _canUnpark       = await SafeBool("canunpark", ct);
        _canSetTracking  = await SafeBool("cansettracking", ct);
        _canPulseGuide   = await SafeBool("canpulseguide", ct);

        // 4. Prime the cache before flipping the connected flag so
        //    callers reading the properties right after ConnectAsync
        //    see real values instead of NaN / false.
        await RefreshOnceAsync(ct);

        _isConnected = true;

        // 5. Start the poll loop.
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        _isConnected = false;

        var cts = _pollCts;
        _pollCts = null;
        if (cts != null) {
            try { cts.Cancel(); } catch { }
            try { if (_pollTask != null) await _pollTask.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
            cts.Dispose();
        }
        _pollTask = null;

        try {
            await _client.PutAsync("connected",
                new Dictionary<string, string> { ["Connected"] = "false" }, ct);
        } catch { /* best-effort */ }
    }

    // ---- ITelescope: slew / sync / abort ----

    /// <summary>
    /// Slew to the given JNow coordinates and start tracking. Issues
    /// <c>slewtocoordinatesasync</c>, then polls <c>slewing</c> every
    /// 500 ms until it goes false (success) OR the caller cancels
    /// (we send <c>abortslew</c> on cancel) OR the 60 s safety timeout
    /// fires.
    /// </summary>
    public async Task SlewAsync(double ra, double dec, CancellationToken ct = default) {
        await _client.PutAsync("slewtocoordinatesasync", new Dictionary<string, string> {
            ["RightAscension"] = ra.ToString(CultureInfo.InvariantCulture),
            ["Declination"] = dec.ToString(CultureInfo.InvariantCulture)
        }, ct);

        var deadline = DateTime.UtcNow + DefaultSlewTimeout;
        try {
            while (DateTime.UtcNow < deadline) {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(PollInterval, ct);
                var slewing = await SafeBool("slewing", ct);
                if (!slewing) return;
            }
            // Timed out: abort and surface the timeout.
            try { await AbortSlewInternalAsync(CancellationToken.None); } catch { }
            throw new TimeoutException(
                $"Alpaca slew did not complete within {DefaultSlewTimeout.TotalSeconds:F0}s.");
        } catch (OperationCanceledException) {
            try { await AbortSlewInternalAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    public Task SyncAsync(double ra, double dec, CancellationToken ct = default) =>
        _client.PutAsync("synctocoordinates", new Dictionary<string, string> {
            ["RightAscension"] = ra.ToString(CultureInfo.InvariantCulture),
            ["Declination"] = dec.ToString(CultureInfo.InvariantCulture)
        }, ct);

    public Task ParkAsync(CancellationToken ct = default) =>
        _client.PutAsync("park", null, ct);

    public Task UnparkAsync(CancellationToken ct = default) =>
        _client.PutAsync("unpark", null, ct);

    /// <summary>ASCOM Alpaca exposes home-find as the bare
    /// <c>findhome</c> action with no parameters. Drivers that don't
    /// implement it return a NotImplementedException which our
    /// PutAsync surfaces as an HTTP error -- caller sees the same
    /// 501-shaped response as if the capability flag had hidden the
    /// button. Caller should check Capabilities.SupportsFindHome
    /// first; some Alpaca drivers report CanFindHome=false even though
    /// the endpoint exists.</summary>
    public Task FindHomeAsync(CancellationToken ct = default) =>
        _client.PutAsync("findhome", null, ct);

    /// <summary>Push observer position via the three ASCOM Alpaca
    /// site-* PUT properties. Longitude convention differs from INDI:
    /// ASCOM uses degrees east in -180..+180, so western hemisphere
    /// stays negative (no wrap). Latitude in degrees (-90..+90),
    /// elevation in metres above sea level. The three PUTs are issued
    /// in sequence; a driver that rejects one (e.g. read-only
    /// elevation on some Celestron Alpaca implementations) still
    /// gets the other two written, then the failure surfaces.</summary>
    public async Task SetSiteLocationAsync(double latitudeDeg, double longitudeDeg,
            double elevationMetres, CancellationToken ct = default) {
        await _client.PutAsync("sitelatitude",
            new Dictionary<string, string> {
                ["SiteLatitude"] = latitudeDeg.ToString(CultureInfo.InvariantCulture)
            }, ct);
        await _client.PutAsync("sitelongitude",
            new Dictionary<string, string> {
                ["SiteLongitude"] = longitudeDeg.ToString(CultureInfo.InvariantCulture)
            }, ct);
        await _client.PutAsync("siteelevation",
            new Dictionary<string, string> {
                ["SiteElevation"] = elevationMetres.ToString(CultureInfo.InvariantCulture)
            }, ct);
    }

    public Task SetTrackingAsync(bool enabled, CancellationToken ct = default) =>
        _client.PutAsync("tracking",
            new Dictionary<string, string> { ["Tracking"] = enabled ? "true" : "false" }, ct);

    public Task AbortSlewAsync(CancellationToken ct = default) =>
        AbortSlewInternalAsync(ct);

    private Task AbortSlewInternalAsync(CancellationToken ct) =>
        _client.PutAsync("abortslew", null, ct);

    // ---- ITelescope: manual jog (Alpaca PulseGuide) ----
    //
    // Alpaca direction codes (GuideDirections enum, ASCOM Telescope v3):
    //   0 = guideNorth, 1 = guideSouth, 2 = guideEast, 3 = guideWest.
    // We pulse for a fixed slice and let the UI repeat by calling the
    // method again on mouse-down hold; StopMotion just aborts the slew.

    private const int JogPulseMs = 500;

    public Task MoveNorthAsync(CancellationToken ct = default) => PulseGuideAsync(0, JogPulseMs, ct);
    public Task MoveSouthAsync(CancellationToken ct = default) => PulseGuideAsync(1, JogPulseMs, ct);
    public Task MoveEastAsync(CancellationToken ct = default)  => PulseGuideAsync(2, JogPulseMs, ct);
    public Task MoveWestAsync(CancellationToken ct = default)  => PulseGuideAsync(3, JogPulseMs, ct);

    public Task StopMotionAsync(CancellationToken ct = default) => AbortSlewInternalAsync(ct);

    private Task PulseGuideAsync(int direction, int durationMs, CancellationToken ct) {
        if (!_canPulseGuide) return Task.CompletedTask;
        return _client.PutAsync("pulseguide", new Dictionary<string, string> {
            ["Direction"] = direction.ToString(CultureInfo.InvariantCulture),
            ["Duration"] = durationMs.ToString(CultureInfo.InvariantCulture)
        }, ct);
    }

    // ---- Poll loop / cache refresh ----

    private async Task PollLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                await RefreshOnceAsync(ct);
            } catch (OperationCanceledException) {
                return;
            } catch {
                // Transient HTTP failure -- swallow and keep polling so a
                // brief network blip doesn't tear the device down.
            }
            try { await Task.Delay(PollInterval, ct); } catch { return; }
        }
    }

    private async Task RefreshOnceAsync(CancellationToken ct) {
        _ra          = await SafeDouble("rightascension", ct);
        _dec         = await SafeDouble("declination", ct);
        _alt         = await SafeDouble("altitude", ct);
        _az          = await SafeDouble("azimuth", ct);
        _isSlewing   = await SafeBool("slewing", ct);
        _isTracking  = await SafeBool("tracking", ct);
        _isParked    = await SafeBool("atpark", ct);
        _sideOfPier  = await SafePierSide(ct);
    }

    private async Task<bool> SafeBool(string action, CancellationToken ct) {
        try { return await _client.GetAsync<bool>(action, ct); }
        catch { return false; }
    }

    private async Task<double> SafeDouble(string action, CancellationToken ct) {
        try { return await _client.GetAsync<double>(action, ct); }
        catch { return double.NaN; }
    }

    private async Task<PierSide> SafePierSide(CancellationToken ct) {
        try {
            var v = await _client.GetAsync<int>("sideofpier", ct);
            return v switch {
                0 => PierSide.pierEast,
                1 => PierSide.pierWest,
                _ => PierSide.pierUnknown
            };
        } catch {
            return PierSide.pierUnknown;
        }
    }

    public void Dispose() {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
    }

    // ---------------------------------------------------------------
    // Legacy thin wrappers kept so the existing /alpaca/* HTTP probe
    // endpoints in AlpacaEndpoints.cs (which predate the ITelescope
    // adoption) keep compiling. New callers should use the ITelescope
    // surface above.
    // ---------------------------------------------------------------

    public Task<string?> GetNameAsync(CancellationToken ct = default) =>
        SafeNullable(_client.GetAsync<string>("name", ct));

    public Task<bool> GetConnectedAsync(CancellationToken ct = default) =>
        SafeFlat<bool>(_client.GetAsync<bool>("connected", ct));

    public Task SetConnectedAsync(bool value, CancellationToken ct = default) =>
        _client.PutAsync("connected",
            new Dictionary<string, string> { ["Connected"] = value ? "true" : "false" }, ct);

    public Task<double> GetRightAscensionAsync(CancellationToken ct = default) =>
        SafeFlat(_client.GetAsync<double>("rightascension", ct), 0d);

    public Task<double> GetDeclinationAsync(CancellationToken ct = default) =>
        SafeFlat(_client.GetAsync<double>("declination", ct), 0d);

    public Task<double> GetAltitudeAsync(CancellationToken ct = default) =>
        SafeFlat(_client.GetAsync<double>("altitude", ct), 0d);

    public Task<double> GetAzimuthAsync(CancellationToken ct = default) =>
        SafeFlat(_client.GetAsync<double>("azimuth", ct), 0d);

    public Task<bool> GetSlewingAsync(CancellationToken ct = default) =>
        SafeFlat<bool>(_client.GetAsync<bool>("slewing", ct));

    public Task<bool> GetAtParkAsync(CancellationToken ct = default) =>
        SafeFlat<bool>(_client.GetAsync<bool>("atpark", ct));

    public Task<bool> GetTrackingAsync(CancellationToken ct = default) =>
        SafeFlat<bool>(_client.GetAsync<bool>("tracking", ct));

    private static async Task<T> SafeFlat<T>(Task<T> task, T fallback = default!) {
        try { return await task; } catch { return fallback; }
    }

    private static async Task<T?> SafeNullable<T>(Task<T?> task) where T : class {
        try { return await task; } catch { return null; }
    }
}
