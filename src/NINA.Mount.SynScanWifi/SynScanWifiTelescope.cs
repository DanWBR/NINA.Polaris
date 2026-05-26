using NINA.Core.Enum;
using NINA.Image.Interfaces;

namespace NINA.Mount.SynScanWifi;

/// <summary>
/// <see cref="ITelescope"/> backed by a direct UDP link to a
/// SynScan-compatible mount. No <c>indiserver</c>, no ASCOM,
/// pure managed code over the Sky-Watcher LX200-ASCII compatibility
/// protocol on <c>UDP/11880</c>.
///
/// <para>
/// Verified-compatible bodies:
/// <list type="bullet">
/// <item>Sky-Watcher AZ-GTi (built-in Wi-Fi, factory-default AP).</item>
/// <item>EQ6-R Pro / EQ8-R Pro / AZ-EQ6 with the SynScan Wi-Fi
/// dongle.</item>
/// <item>AllView, EQM-35 Pro, GoTo Dobsonians with Wi-Fi handsets.</item>
/// </list>
/// </para>
///
/// <para>
/// Likely-compatible (mounts that re-export the SynScan command set):
/// ZWO AM5N / AM7 in SynScan compatibility mode.
/// </para>
///
/// <para>
/// State model: SynScan Wi-Fi mounts don't push events. The status
/// properties (RA / Dec / tracking / slewing) are populated by a
/// background poll loop that ticks every 1 s while connected.
/// Capture-side code reading <see cref="RightAscension"/> etc. gets
/// the most recent poll value, not a live read on every access,
/// which is the right behaviour for the existing status broadcaster
/// (also 1 Hz).
/// </para>
/// </summary>
public sealed class SynScanWifiTelescope : ITelescope, IDisposable {
    private readonly string _host;
    private readonly int _port;
    private SynScanUdpClient? _client;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _isConnected;

    // Cached state, populated by the poll loop, read by the
    // ITelescope properties. Volatile reads are fine for these
    // scalar field types under .NET's memory model.
    private double _ra;
    private double _dec;
    private bool _isTracking;
    private bool _isParked;
    private DateTime _lastSlewRequestUtc = DateTime.MinValue;

    public string DeviceName { get; }
    public bool IsConnected => _isConnected;

    public double RightAscension => _ra;
    public double Declination    => _dec;
    public double Altitude       => double.NaN;   // Driver intentionally omits Alt/Az,
    public double Azimuth        => double.NaN;   // StatusBroadcaster recomputes from RA/Dec + observer.
    public bool   IsTracking     => _isTracking;
    public bool   IsParked       => _isParked;

    /// <summary>The mount is "slewing" if we issued a SlewAsync less
    /// than ~30 seconds ago and tracking hasn't kicked back in.
    /// SynScan LX200 doesn't have a single "slew status" query that
    /// works across firmware versions, so we infer it client-side.</summary>
    public bool IsSlewing
        => (DateTime.UtcNow - _lastSlewRequestUtc) < TimeSpan.FromSeconds(30)
           && !_isTracking;

    public PierSide SideOfPier => PierSide.pierUnknown;

    public MountCapabilities Capabilities => MountCapabilities.GermanEquatorial;

    /// <summary>
    /// <paramref name="deviceId"/> is the mount endpoint as
    /// <c>host[:port]</c>. Empty / null → factory default
    /// <c>192.168.4.1:11880</c> which is what AZ-GTi advertises in AP
    /// mode. On a home network use the DHCP-assigned address.
    /// </summary>
    public SynScanWifiTelescope(string deviceId) {
        deviceId = string.IsNullOrWhiteSpace(deviceId)
            ? $"{SynScanUdpClient.DefaultHost}:{SynScanUdpClient.DefaultPort}"
            : deviceId.Trim();

        var colonIdx = deviceId.LastIndexOf(':');
        if (colonIdx > 0
            && int.TryParse(deviceId.AsSpan(colonIdx + 1), out var parsedPort)) {
            _host = deviceId.Substring(0, colonIdx);
            _port = parsedPort;
        } else {
            _host = deviceId;
            _port = SynScanUdpClient.DefaultPort;
        }
        DeviceName = $"SynScan Wi-Fi @ {_host}:{_port}";
    }

    // ---- Lifecycle ------------------------------------------------

    public Task ConnectAsync(CancellationToken ct = default) {
        if (_isConnected) return Task.CompletedTask;
        _client = new SynScanUdpClient(_host, _port);
        _isConnected = true;

        // Kick off the poll loop. The first poll happens immediately
        // so the status broadcaster doesn't show zeros for a second
        // after connect.
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        if (!_isConnected) return;
        _isConnected = false;
        _pollCts?.Cancel();
        try { if (_pollTask != null) await _pollTask; } catch { /* expected on cancel */ }
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
        _client?.Dispose();
        _client = null;
    }

    public void Dispose() {
        DisconnectAsync().GetAwaiter().GetResult();
    }

    // ---- Polled status --------------------------------------------

    private async Task PollLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                await PollOnceAsync(ct);
            } catch (OperationCanceledException) {
                break;
            } catch {
                // Single-poll failures are normal on a Wi-Fi link
                // (packet loss, brief mount busy state). Don't log
                // here, it'd spam the journal. The next tick retries.
            }
            try {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            } catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct) {
        if (_client == null) return;

        // RA + Dec. Two round-trips per second, ~5 ms each on a
        // local Wi-Fi link, well under the 1 s budget.
        var raResp  = await _client.SendQueryAsync(":GR#", ct);
        var decResp = await _client.SendQueryAsync(":GD#", ct);
        var ra  = SynScanCommandCodec.ParseRA(raResp);
        var dec = SynScanCommandCodec.ParseDec(decResp);
        if (ra  != null) _ra  = ra.Value;
        if (dec != null) _dec = dec.Value;

        // Tracking state. ":GT#" returns the current tracking rate
        // (e.g. "60.0#" for sidereal) on most SynScan firmware;
        // the mount answers ":U#" or non-numeric when tracking is
        // off. Use parse-ability as the on/off signal.
        try {
            var trk = await _client.SendQueryAsync(":GT#", ct);
            _isTracking = double.TryParse(trk.TrimEnd('#').Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rate)
                && rate > 0.01;
        } catch { /* some firmware doesn't support :GT, leave previous value */ }
    }

    // ---- Slew / sync / park / track --------------------------------

    public async Task SlewAsync(double ra, double dec, CancellationToken ct = default) {
        EnsureConnected();
        // Order is: set target RA, set target Dec, then issue slew.
        // SynScan firmware rejects :MS until both :Sr and :Sd have
        // succeeded since the last reset.
        await _client!.SendQueryAsync($":Sr{SynScanCommandCodec.FormatRA(ra)}#", ct);
        await _client!.SendQueryAsync($":Sd{SynScanCommandCodec.FormatDec(dec)}#", ct);
        var resp = await _client!.SendQueryAsync(":MS#", ct);
        _lastSlewRequestUtc = DateTime.UtcNow;
        // ":MS" returns "0" on success, or "1<reason>#" / "2<reason>#"
        // (object below horizon / object below higher limit).
        if (resp.Length > 0 && resp[0] != '0') {
            throw new InvalidOperationException(
                $"SynScan rejected slew: {resp.TrimEnd('#')}");
        }
    }

    public async Task SyncAsync(double ra, double dec, CancellationToken ct = default) {
        EnsureConnected();
        // Sync follows the same pre-condition as slew, set target
        // first, then :CM# (Calibrate Match).
        await _client!.SendQueryAsync($":Sr{SynScanCommandCodec.FormatRA(ra)}#", ct);
        await _client!.SendQueryAsync($":Sd{SynScanCommandCodec.FormatDec(dec)}#", ct);
        await _client!.SendQueryAsync(":CM#", ct);
        _ra = ra;
        _dec = dec;
    }

    public async Task ParkAsync(CancellationToken ct = default) {
        EnsureConnected();
        await _client!.SendOneWayAsync(":hP#", ct);
        _isParked = true;
        _isTracking = false;
    }

    public async Task UnparkAsync(CancellationToken ct = default) {
        EnsureConnected();
        // Sky-Watcher SynScan unpark is ":hP" toggled, or ":hC#" on
        // some firmwares. Sending both is harmless and improves
        // compat across the lineup.
        await _client!.SendOneWayAsync(":hC#", ct);
        _isParked = false;
    }

    public async Task SetTrackingAsync(bool enabled, CancellationToken ct = default) {
        EnsureConnected();
        // ":T+" / ":T-", start / stop the tracking motor at the
        // currently-selected rate (default sidereal).
        await _client!.SendOneWayAsync(enabled ? ":T+#" : ":T-#", ct);
        _isTracking = enabled;
    }

    public async Task AbortSlewAsync(CancellationToken ct = default) {
        EnsureConnected();
        await _client!.SendOneWayAsync(":Q#", ct);
        _lastSlewRequestUtc = DateTime.MinValue;
    }

    // ---- Manual jog ------------------------------------------------

    public async Task MoveNorthAsync(CancellationToken ct = default) {
        EnsureConnected();
        await _client!.SendOneWayAsync(":Mn#", ct);
    }
    public async Task MoveSouthAsync(CancellationToken ct = default) {
        EnsureConnected();
        await _client!.SendOneWayAsync(":Ms#", ct);
    }
    public async Task MoveEastAsync(CancellationToken ct = default) {
        EnsureConnected();
        await _client!.SendOneWayAsync(":Me#", ct);
    }
    public async Task MoveWestAsync(CancellationToken ct = default) {
        EnsureConnected();
        await _client!.SendOneWayAsync(":Mw#", ct);
    }
    public async Task StopMotionAsync(CancellationToken ct = default) {
        EnsureConnected();
        // ":Q#" halts every axis. Per-axis stop is ":Qn", ":Qs",
        // ":Qe", ":Qw" but our manual-jog UI only has a single Stop
        // button, so the broad-spectrum abort matches that semantic.
        await _client!.SendOneWayAsync(":Q#", ct);
    }

    private void EnsureConnected() {
        if (!_isConnected || _client == null)
            throw new InvalidOperationException(
                "SynScan Wi-Fi mount is not connected. Call ConnectAsync first.");
    }
}
