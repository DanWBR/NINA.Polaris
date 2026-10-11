// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using NINA.Core.Enum;
using NINA.Image.Interfaces;
using static NINA.Mount.SynScanWifi.SkyWatcherMotorCodec;

namespace NINA.Mount.SynScanWifi;

/// <summary>
/// <see cref="ITelescope"/> backed by a direct UDP link to a Sky-Watcher
/// mount's own Wi-Fi (AZ-GTi built in, or the SynScan Wi-Fi adapter on an
/// EQ6-R Pro / EQ8-R Pro / AZ-EQ6). No <c>indiserver</c>, no ASCOM, no
/// SynScan App: the driver speaks the motor-controller protocol on
/// <c>UDP/11880</c> (see <see cref="SkyWatcherMotorCodec"/>) and does the
/// astronomy itself.
///
/// <para>Equatorial mounts only for now: an AZ-GTi has to be in EQ mode, on a
/// wedge. The motor controller has no notion of the mode, so an AZ-GTi
/// standing in alt-az would be read as if it were equatorial and every
/// position would be wrong.</para>
///
/// <para>The motor controller knows axis positions and nothing else. Site
/// and time make them sky coordinates (<see cref="EqAxisGeometry"/>), so
/// RA / Dec read NaN until <see cref="SetSiteLocationAsync"/> has been
/// called, which Polaris does right after connecting. Time is this host's
/// clock.</para>
///
/// <para>State model: the mount pushes nothing. Positions and axis status
/// come from a 1 s poll. A GoTo runs in the background: the command returns
/// once the axes are moving, a second short pass corrects for the sky that
/// turned during the slew, and tracking starts when both axes stop.</para>
/// </summary>
public sealed class SynScanWifiTelescope : ITelescope, IDisposable {
    private const int Ra = 1, Dec = 2;
    private const double SiderealArcsecPerSec = 15.041067;
    private const double SolarArcsecPerSec = 15.0;
    private const double LunarArcsecPerSec = 14.685;
    private const double ArcsecPerRev = 1296000.0;
    /// <summary>Distance before the target where a GoTo starts to brake
    /// (counts). Close to what INDI and GSS use.</summary>
    private const int BrakeSteps = 3200;
    /// <summary>Guide pulses move the sky at half the sidereal rate, the
    /// usual default. The guider's calibration measures what it really is.</summary>
    private const double GuideRateMultiple = 0.5;

    private static readonly (string Name, string Label, double Multiple)[] Rates = {
        ("SLEW_16X", "16x", 16), ("SLEW_64X", "64x", 64),
        ("SLEW_256X", "256x", 256), ("SLEW_800X", "800x", 800),
    };

    private readonly string _host;
    private readonly int _port;
    private SynScanUdpClient? _client;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _isConnected;

    // Read at connect.
    private int _cprRa, _cprDec, _timerFreq, _highRatioRa = 1, _highRatioDec = 1;
    private bool _freshPowerOn;

    private double? _latitude, _longitude;

    // From the poll.
    private int _posRa = PositionOffset, _posDec = PositionOffset;
    private AxisStatus _statusRa, _statusDec;
    private bool _havePosition;

    // Sync: degrees added to the raw axis angles.
    private double _raOffset, _decOffset;

    private bool _trackingWanted;
    private double _trackingRate = SiderealArcsecPerSec;
    private bool _isParked;
    private string _slewRate = "SLEW_256X";

    private int _pulsesActive;
    private volatile bool _gotoInProgress;
    private CancellationTokenSource? _gotoCts;
    private Task? _gotoTask;

    public string DeviceName { get; }
    public string? Firmware { get; private set; }
    public bool IsConnected => _isConnected;

    private bool South => (_latitude ?? 0) < 0;
    private bool Ready => _isConnected && _havePosition && _latitude.HasValue && _longitude.HasValue;

    public double RightAscension {
        get {
            if (!Ready) return double.NaN;
            var (ha, _, _) = Sky();
            double ra = (Lst() - ha) % 24;
            return ra < 0 ? ra + 24 : ra;
        }
    }

    public double Declination => Ready ? Sky().DecDeg : double.NaN;
    public double Altitude => Ready ? EqAxisGeometry.Altitude(Sky().HaHours, Sky().DecDeg, _latitude!.Value) : double.NaN;
    public double Azimuth => Ready ? EqAxisGeometry.Azimuth(Sky().HaHours, Sky().DecDeg, _latitude!.Value) : double.NaN;
    public bool IsTracking => _trackingWanted && !_gotoInProgress
                              && _statusRa.Running && _statusRa.ConstantSpeed;
    public bool IsParked => _isParked;
    public bool IsSlewing => _gotoInProgress
                             || (_statusRa.Running && !_statusRa.ConstantSpeed)
                             || (_statusDec.Running && !_statusDec.ConstantSpeed);
    public PierSide SideOfPier => Ready ? Sky().Side : PierSide.pierUnknown;

    public MountCapabilities Capabilities { get; } = new(
        SupportsPark: true, SupportsTrackingToggle: true, SupportsSync: true,
        SupportsPierSide: true, SupportsManualJog: true, SupportsFindHome: true,
        SupportsSetSiteLocation: true, SupportsSetSiteTime: true, SupportsTrackingModes: true,
        SupportsPulseGuide: true);

    public bool IsPulseGuiding => Volatile.Read(ref _pulsesActive) > 0;

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

    public async Task ConnectAsync(CancellationToken ct = default) {
        if (_isConnected) return;
        _client = new SynScanUdpClient(_host, _port);
        try {
            string fw;
            try {
                fw = await AskAsync('e', Ra, ct: ct);
            } catch (TimeoutException) {
                throw new InvalidOperationException(
                    $"No reply from a Sky-Watcher mount at {_host}:{_port}. Check the address, that this "
                    + "machine is on the mount's network, and that the SynScan App is not connected to it.");
            }
            int v = DecodeUInt(fw);
            Firmware = $"{v & 0xFF}.{(v >> 8) & 0xFF:D2}.{(v >> 16) & 0xFF:X2}";

            _cprRa = DecodeUInt(await AskAsync('a', Ra, ct: ct));
            _cprDec = DecodeUInt(await AskAsync('a', Dec, ct: ct));
            _timerFreq = DecodeUInt(await AskAsync('b', Ra, ct: ct));
            _highRatioRa = Math.Max(1, DecodeUInt(await AskAsync('g', Ra, ct: ct)));
            _highRatioDec = Math.Max(1, DecodeUInt(await AskAsync('g', Dec, ct: ct)));
            if (_cprRa <= 0 || _cprDec <= 0 || _timerFreq <= 0)
                throw new InvalidOperationException($"The mount at {_host}:{_port} reported no gearing.");

            // A motor controller that was never initialised since power-on
            // takes no motion commands until it is.
            foreach (var axis in new[] { Ra, Dec }) {
                if (!ParseStatus(await AskAsync('f', axis, ct: ct)).Initialized)
                    await AskAsync('F', axis, ct: ct);
            }

            await PollOnceAsync(ct);
            // Both axes exactly at the controller's zero: powered on and
            // never set. The SynScan App defines that pose as home; so does
            // this driver, once it knows the hemisphere.
            _freshPowerOn = _posRa == PositionOffset && _posDec == PositionOffset;
            // Already following the sky (left tracking by the SynScan App).
            _trackingWanted = _statusRa.Running && _statusRa.ConstantSpeed && !_statusDec.Running;
        } catch {
            _client.Dispose();
            _client = null;
            throw;
        }

        _isConnected = true;
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        if (!_isConnected) return;
        await CancelGotoAsync();
        _isConnected = false;
        _pollCts?.Cancel();
        try { if (_pollTask != null) await _pollTask; } catch { /* expected on cancel */ }
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
        _client?.Dispose();
        _client = null;
        _havePosition = false;
    }

    public void Dispose() {
        DisconnectAsync().GetAwaiter().GetResult();
    }

    // ---- Site and time ----------------------------------------------

    public async Task SetSiteLocationAsync(double latitudeDeg, double longitudeDeg,
            double elevationMetres, CancellationToken ct = default) {
        _latitude = latitudeDeg;
        _longitude = longitudeDeg;
        if (_freshPowerOn && _isConnected) {
            _freshPowerOn = false;
            var (ra, dec) = EqAxisGeometry.Home(South);
            await AskAsync('E', Ra, EncodeUInt24(EqAxisGeometry.DegreesToSteps(ra, _cprRa)), ct);
            await AskAsync('E', Dec, EncodeUInt24(EqAxisGeometry.DegreesToSteps(dec, _cprDec)), ct);
            await PollOnceAsync(ct);
        }
    }

    /// <summary>The mount keeps no clock of its own here: sidereal time comes
    /// from this host's clock, so there is nothing to send.</summary>
    public Task SetSiteTimeAsync(DateTime utc, double offsetHoursFromUtc, CancellationToken ct = default)
        => Task.CompletedTask;

    // ---- Polled status --------------------------------------------

    private async Task PollLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                await PollOnceAsync(ct);
            } catch (OperationCanceledException) {
                break;
            } catch {
                // Single-poll failures are normal on a Wi-Fi link
                // (packet loss, brief mount busy state). The next tick retries.
            }
            try {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            } catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct) {
        _posRa = DecodeUInt(await AskAsync('j', Ra, ct: ct));
        _posDec = DecodeUInt(await AskAsync('j', Dec, ct: ct));
        _statusRa = ParseStatus(await AskAsync('f', Ra, ct: ct));
        _statusDec = ParseStatus(await AskAsync('f', Dec, ct: ct));
        _havePosition = true;
    }

    // ---- Slew / sync / park / track --------------------------------

    public async Task SlewAsync(double ra, double dec, CancellationToken ct = default) {
        EnsureReady();
        double ha = EqAxisGeometry.WrapHours(Lst() - ra);
        double alt = EqAxisGeometry.Altitude(ha, dec, _latitude!.Value);
        if (alt < 0)
            throw new InvalidOperationException($"Target is below the horizon (altitude {alt:F1}°).");
        var side = EqAxisGeometry.SideFor(ha);
        _isParked = false;
        _trackingWanted = true;
        await StartGotoAsync(
            () => EqAxisGeometry.SkyToAxes(EqAxisGeometry.WrapHours(Lst() - ra), dec, South, side),
            onArrived: null, ct);
    }

    public async Task SyncAsync(double ra, double dec, CancellationToken ct = default) {
        EnsureReady();
        int posRa = DecodeUInt(await AskAsync('j', Ra, ct: ct));
        int posDec = DecodeUInt(await AskAsync('j', Dec, ct: ct));
        double rawRa = EqAxisGeometry.StepsToDegrees(posRa, _cprRa);
        double rawDec = EqAxisGeometry.StepsToDegrees(posDec, _cprDec);
        var side = EqAxisGeometry.AxesToSky(rawRa + _raOffset, rawDec + _decOffset, South).Side;
        var (wantRa, wantDec) = EqAxisGeometry.SkyToAxes(
            EqAxisGeometry.WrapHours(Lst() - ra), dec, South, side);
        _raOffset = EqAxisGeometry.Wrap180(wantRa - rawRa);
        _decOffset = EqAxisGeometry.Wrap180(wantDec - rawDec);
        _posRa = posRa;
        _posDec = posDec;
    }

    public Task ParkAsync(CancellationToken ct = default) {
        EnsureReady();
        _trackingWanted = false;
        return StartGotoAsync(() => EqAxisGeometry.Home(South), onArrived: () => _isParked = true, ct);
    }

    public Task UnparkAsync(CancellationToken ct = default) {
        _isParked = false;
        return Task.CompletedTask;
    }

    public Task FindHomeAsync(CancellationToken ct = default) {
        EnsureReady();
        _trackingWanted = false;
        return StartGotoAsync(() => EqAxisGeometry.Home(South), onArrived: null, ct);
    }

    public async Task SetTrackingAsync(bool enabled, CancellationToken ct = default) {
        EnsureConnected();
        _trackingWanted = enabled;
        // A GoTo in flight starts (or leaves off) tracking when it arrives.
        if (_gotoInProgress) return;
        if (enabled) await StartTrackingAsync(ct);
        else await StopAxisAsync(Ra, ct);
    }

    public async Task SetTrackingModeAsync(TrackingMode mode, CancellationToken ct = default) {
        _trackingRate = mode switch {
            TrackingMode.Solar => SolarArcsecPerSec,
            TrackingMode.Lunar => LunarArcsecPerSec,
            _ => SiderealArcsecPerSec,
        };
        if (_isConnected && _trackingWanted && !_gotoInProgress) await StartTrackingAsync(ct);
    }

    public async Task AbortSlewAsync(CancellationToken ct = default) {
        EnsureConnected();
        await CancelGotoAsync();
        await StopAxisAsync(Ra, ct);
        await StopAxisAsync(Dec, ct);
        if (_trackingWanted) await StartTrackingAsync(ct);
    }

    // ---- Manual jog ------------------------------------------------

    public IReadOnlyList<SlewRateStep> GetSlewRates()
        => Rates.Select(r => new SlewRateStep(r.Name, r.Label, r.Name == _slewRate)).ToList();

    public Task SetSlewRateAsync(string elementName, CancellationToken ct = default) {
        if (!Rates.Any(r => r.Name == elementName))
            throw new ArgumentException($"Unknown slew rate '{elementName}'", nameof(elementName));
        _slewRate = elementName;
        return Task.CompletedTask;
    }

    public Task MoveNorthAsync(CancellationToken ct = default)
        => JogAsync(Dec, EqAxisGeometry.NorthIsForward(South, SideOfPier), ct);
    public Task MoveSouthAsync(CancellationToken ct = default)
        => JogAsync(Dec, !EqAxisGeometry.NorthIsForward(South, SideOfPier), ct);
    public Task MoveWestAsync(CancellationToken ct = default)
        => JogAsync(Ra, EqAxisGeometry.WestIsForward(South), ct);
    public Task MoveEastAsync(CancellationToken ct = default)
        => JogAsync(Ra, !EqAxisGeometry.WestIsForward(South), ct);

    public async Task StopMotionAsync(CancellationToken ct = default) {
        EnsureConnected();
        await CancelGotoAsync();
        await StopAxisAsync(Ra, ct);
        await StopAxisAsync(Dec, ct);
        if (_trackingWanted) await StartTrackingAsync(ct);
    }

    /// <summary>A d-pad release: stop that one axis, and give the RA axis
    /// back to tracking.</summary>
    public async Task StopMotionAsync(MountJogDirection direction, CancellationToken ct = default) {
        EnsureConnected();
        int axis = direction is MountJogDirection.North or MountJogDirection.South ? Dec : Ra;
        await StopAxisAsync(axis, ct);
        if (axis == Ra && _trackingWanted) await StartTrackingAsync(ct);
    }

    private async Task JogAsync(int axis, bool forward, CancellationToken ct) {
        EnsureReady();
        await CancelGotoAsync();
        double multiple = Rates.First(r => r.Name == _slewRate).Multiple;
        int cpr = axis == Ra ? _cprRa : _cprDec;
        bool high = multiple > 128;
        int ratio = high ? (axis == Ra ? _highRatioRa : _highRatioDec) : 1;
        int period = Period(cpr, multiple * SiderealArcsecPerSec, ratio);
        await StopAxisAsync(axis, ct);
        await AskAsync('G', axis, (high ? "3" : "1") + (forward ? "0" : "1"), ct);
        await AskAsync('I', axis, EncodeUInt24(period), ct);
        await AskAsync('J', axis, ct: ct);
    }

    // ---- Pulse guiding -------------------------------------------------

    /// <summary>
    /// A guide pulse, done the way EQMod does it on this protocol, which has
    /// no ST-4 style pulse command: for the length of the pulse the RA axis
    /// tracks at (1 ± guide rate) times its rate, or the Dec axis turns at the
    /// guide rate, and then everything goes back. Returns when the pulse is
    /// over, like the simulator, so the guider's RA and Dec pulses run one
    /// after the other.
    /// </summary>
    public async Task PulseGuideAsync(GuideDirections direction, int durationMs, CancellationToken ct = default) {
        EnsureReady();
        if (durationMs <= 0 || _gotoInProgress) return;
        Interlocked.Increment(ref _pulsesActive);
        try {
            if (direction is GuideDirections.guideEast or GuideDirections.guideWest)
                await PulseRaAsync(direction == GuideDirections.guideWest, durationMs, ct);
            else
                await PulseDecAsync(direction == GuideDirections.guideNorth, durationMs, ct);
        } finally {
            Interlocked.Decrement(ref _pulsesActive);
        }
    }

    private async Task PulseRaAsync(bool west, int durationMs, CancellationToken ct) {
        var status = ParseStatus(await AskAsync('f', Ra, ct: ct));
        bool tracking = status.Running && status.ConstantSpeed && !status.HighSpeed;
        if (!tracking) {
            // Nothing to modulate: turn the axis at the guide rate alone.
            await TimedAxisMoveAsync(Ra, west == EqAxisGeometry.WestIsForward(South), durationMs, ct);
            return;
        }
        // West is the direction the sky already turns the axis, so a west
        // pulse speeds tracking up and an east one slows it down; at half
        // rate it never has to reverse, so the speed changes on the fly.
        double factor = west ? 1 + GuideRateMultiple : 1 - GuideRateMultiple;
        int normal = Period(_cprRa, _trackingRate, 1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await AskAsync('I', Ra, EncodeUInt24(Period(_cprRa, _trackingRate * factor, 1)), ct);
        try {
            await DelayRemainingAsync(clock, durationMs, ct);
        } finally {
            await AskAsync('I', Ra, EncodeUInt24(normal), CancellationToken.None);
        }
    }

    private async Task PulseDecAsync(bool north, int durationMs, CancellationToken ct)
        => await TimedAxisMoveAsync(Dec, north == EqAxisGeometry.NorthIsForward(South, SideOfPier), durationMs, ct);

    /// <summary>Turn one axis at the guide rate for <paramref name="durationMs"/>,
    /// timed from the moment it starts.</summary>
    private async Task TimedAxisMoveAsync(int axis, bool forward, int durationMs, CancellationToken ct) {
        int cpr = axis == Ra ? _cprRa : _cprDec;
        if (ParseStatus(await AskAsync('f', axis, ct: ct)).Running)
            await StopAxisAsync(axis, ct);
        await AskAsync('G', axis, "1" + (forward ? "0" : "1"), ct);
        await AskAsync('I', axis, EncodeUInt24(Period(cpr, SiderealArcsecPerSec * GuideRateMultiple, 1)), ct);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await AskAsync('J', axis, ct: ct);
        try {
            await DelayRemainingAsync(clock, durationMs, ct);
        } finally {
            await AskAsync('K', axis, ct: CancellationToken.None);
        }
    }

    private static async Task DelayRemainingAsync(System.Diagnostics.Stopwatch clock, int durationMs, CancellationToken ct) {
        int left = durationMs - (int)clock.ElapsedMilliseconds;
        if (left > 0) await Task.Delay(left, ct);
    }

    // ---- Motion primitives -------------------------------------------

    /// <summary>Timer ticks per step for an axis turning at this many
    /// arcseconds per second (times the high-speed ratio in fast mode).</summary>
    private int Period(int cpr, double arcsecPerSec, int highSpeedRatio)
        => Math.Max(1, (int)Math.Round(_timerFreq * (double)highSpeedRatio / (cpr * arcsecPerSec / ArcsecPerRev)));


    private async Task StartTrackingAsync(CancellationToken ct) {
        int period = Period(_cprRa, _trackingRate, 1);
        await StopAxisAsync(Ra, ct);
        await AskAsync('G', Ra, "1" + (EqAxisGeometry.TrackingForward(South) ? "0" : "1"), ct);
        await AskAsync('I', Ra, EncodeUInt24(period), ct);
        await AskAsync('J', Ra, ct: ct);
    }

    /// <summary>Decelerate one axis and wait until it has stopped: the
    /// controller refuses a new motion mode on a moving axis.</summary>
    private async Task StopAxisAsync(int axis, CancellationToken ct) {
        await AskAsync('K', axis, ct: ct);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (ParseStatus(await AskAsync('f', axis, ct: ct)).Running) {
            if (DateTime.UtcNow > deadline) {
                await AskAsync('L', axis, ct: ct);   // instant stop
                break;
            }
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Send both axes towards these axis angles (degrees, after
    /// sync). Returns once they are moving.</summary>
    private async Task IssueGotoAsync((double RaAxis, double DecAxis) target, CancellationToken ct) {
        await StopAxisAsync(Ra, ct);
        await StopAxisAsync(Dec, ct);
        int toRa = EqAxisGeometry.DegreesToSteps(target.RaAxis - _raOffset, _cprRa);
        int toDec = EqAxisGeometry.DegreesToSteps(target.DecAxis - _decOffset, _cprDec);
        int fromRa = DecodeUInt(await AskAsync('j', Ra, ct: ct));
        int fromDec = DecodeUInt(await AskAsync('j', Dec, ct: ct));
        await StartAxisGotoAsync(Ra, toRa - fromRa, _cprRa, ct);
        await StartAxisGotoAsync(Dec, toDec - fromDec, _cprDec, ct);
    }

    private async Task StartAxisGotoAsync(int axis, int offset, int cpr, CancellationToken ct) {
        if (Math.Abs(offset) < 2) return;
        int steps = Math.Abs(offset);
        // Below about ten minutes of sidereal motion a fast GoTo would only
        // accelerate to brake again; the slow one lands more precisely.
        int lowSpeedMargin = (int)(640 * cpr * SiderealArcsecPerSec / ArcsecPerRev);
        char mode = steps > lowSpeedMargin ? '0' : '2';
        await AskAsync('G', axis, $"{mode}{(offset > 0 ? '0' : '1')}", ct);
        await AskAsync('H', axis, EncodeUInt24(steps), ct);
        await AskAsync('M', axis, EncodeUInt24(BrakeSteps), ct);
        await AskAsync('J', axis, ct: ct);
    }

    private async Task WaitForAxesStoppedAsync(TimeSpan limit, CancellationToken ct) {
        var deadline = DateTime.UtcNow + limit;
        while (true) {
            var ra = ParseStatus(await AskAsync('f', Ra, ct: ct));
            var dec = ParseStatus(await AskAsync('f', Dec, ct: ct));
            if (!ra.Running && !dec.Running) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The GoTo did not finish in time.");
            await Task.Delay(300, ct);
        }
    }

    /// <summary>Start a GoTo and return once the axes move. The rest runs in
    /// the background: wait for arrival, a short second pass for the sky
    /// that turned meanwhile, then tracking if it is wanted. A failure stops
    /// both axes.</summary>
    private async Task StartGotoAsync(Func<(double RaAxis, double DecAxis)> target,
            Action? onArrived, CancellationToken ct) {
        await CancelGotoAsync();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_pollCts?.Token ?? CancellationToken.None);
        _gotoCts = cts;
        _gotoInProgress = true;
        try {
            await IssueGotoAsync(target(), ct);
        } catch {
            _gotoInProgress = false;
            await StopBothQuietlyAsync();
            throw;
        }
        _gotoTask = Task.Run(async () => {
            try {
                await WaitForAxesStoppedAsync(TimeSpan.FromMinutes(4), cts.Token);
                await IssueGotoAsync(target(), cts.Token);
                await WaitForAxesStoppedAsync(TimeSpan.FromMinutes(1), cts.Token);
                onArrived?.Invoke();
                if (_trackingWanted) await StartTrackingAsync(cts.Token);
            } catch (OperationCanceledException) {
                // Aborted, or superseded by another motion.
            } catch {
                await StopBothQuietlyAsync();
            } finally {
                _gotoInProgress = false;
            }
        });
    }

    private async Task CancelGotoAsync() {
        var cts = _gotoCts;
        var task = _gotoTask;
        _gotoCts = null;
        _gotoTask = null;
        if (cts == null) return;
        cts.Cancel();
        try { if (task != null) await task; } catch { /* cancelled */ }
        cts.Dispose();
        _gotoInProgress = false;
    }

    private async Task StopBothQuietlyAsync() {
        try { await AskAsync('K', Ra); } catch { /* best effort */ }
        try { await AskAsync('K', Dec); } catch { /* best effort */ }
    }

    // ---- Helpers -----------------------------------------------------

    /// <summary>One command and its reply data. UDP drops packets, so a
    /// missing reply is retried; every command here is safe to repeat.</summary>
    private async Task<string> AskAsync(char command, int axis, string data = "", CancellationToken ct = default) {
        var client = _client ?? throw new InvalidOperationException("SynScan Wi-Fi mount is not connected.");
        var text = Command(command, axis, data);
        for (int attempt = 1; ; attempt++) {
            try {
                return ParseReply(await client.SendQueryAsync(text, ct));
            } catch (TimeoutException) when (attempt < 3) {
                // retry
            }
        }
    }

    private (double HaHours, double DecDeg, PierSide Side) Sky()
        => EqAxisGeometry.AxesToSky(
            EqAxisGeometry.StepsToDegrees(_posRa, _cprRa) + _raOffset,
            EqAxisGeometry.StepsToDegrees(_posDec, _cprDec) + _decOffset,
            South);

    private double Lst() => EqAxisGeometry.LocalSiderealHours(DateTime.UtcNow, _longitude ?? 0);

    private void EnsureConnected() {
        if (!_isConnected || _client == null)
            throw new InvalidOperationException(
                "SynScan Wi-Fi mount is not connected. Call ConnectAsync first.");
    }

    private void EnsureReady() {
        EnsureConnected();
        if (!_latitude.HasValue || !_longitude.HasValue)
            throw new InvalidOperationException(
                "The site location has not been sent to the mount yet; set it in the mount panel (Sync Site).");
    }
}
