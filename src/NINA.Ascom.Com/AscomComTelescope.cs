using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NINA.Core.Enum;
using NINA.Image.Interfaces;

namespace NINA.Ascom.Com;

/// <summary>
/// ASCOM Platform Telescope (ITelescopeV3) adapter exposed through
/// <see cref="ITelescope"/>. Late-binds via dynamic COM; no compile-
/// time reference to the ASCOM Platform.
///
/// <para>Covers the surface Polaris's slew &amp; center, meridian-flip,
/// polar-alignment, and sequencer features actually call: connect,
/// RA/Dec/Alt/Az readout, slew, sync, tracking toggle, park /
/// unpark, pier side, manual jog (PulseGuide via MoveAxis), abort.
/// Out of scope for v1: GPS readout, alignment-mode reporting,
/// custom tracking rates, equatorial-system selection.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComTelescope : ITelescope, IDisposable {
    private readonly string _progId;
    private readonly AscomComStaDispatcher _disp;
    private dynamic? _driver;
    private string _deviceName = "ASCOM Telescope";
    private bool _canPark, _canUnpark, _canSync, _canSetTracking,
                 _canPierSide, _canMoveAxis;

    public AscomComTelescope(string progId) {
        _progId = progId ?? throw new ArgumentNullException(nameof(progId));
        _disp = new AscomComStaDispatcher($"ASCOM-Telescope-{progId}");
    }

    public string DeviceName => _deviceName;
    public bool IsConnected => _driver != null
        && _disp.Invoke<bool>(() => SafeGet(() => (bool)_driver!.Connected)).Result;

    public double RightAscension => Read(() => (double)_driver!.RightAscension, double.NaN);
    public double Declination    => Read(() => (double)_driver!.Declination, double.NaN);
    public double Altitude       => Read(() => (double)_driver!.Altitude, double.NaN);
    public double Azimuth        => Read(() => (double)_driver!.Azimuth, double.NaN);
    public bool IsTracking       => Read(() => (bool)_driver!.Tracking);
    public bool IsParked         => Read(() => (bool)_driver!.AtPark);
    public bool IsSlewing        => Read(() => (bool)_driver!.Slewing);

    public PierSide SideOfPier {
        get {
            if (!_canPierSide) return PierSide.pierUnknown;
            var v = Read(() => (int)_driver!.SideOfPier, -1);
            return v switch { 0 => PierSide.pierEast, 1 => PierSide.pierWest, _ => PierSide.pierUnknown };
        }
    }

    public MountCapabilities Capabilities => new(
        SupportsPark: _canPark,
        SupportsTrackingToggle: _canSetTracking,
        SupportsSync: _canSync,
        SupportsPierSide: _canPierSide,
        SupportsManualJog: _canMoveAxis);

    public Task ConnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        var t = Type.GetTypeFromProgID(_progId)
            ?? throw new InvalidOperationException(
                $"ASCOM driver '{_progId}' is not registered.");
        _driver = Activator.CreateInstance(t)
            ?? throw new InvalidOperationException(
                $"ASCOM driver '{_progId}' failed to instantiate.");
        _driver!.Connected = true;
        try { _deviceName = (string)_driver.Name; } catch { _deviceName = _progId; }
        _canPark         = SafeGet(() => (bool)_driver.CanPark);
        _canUnpark       = SafeGet(() => (bool)_driver.CanUnpark);
        _canSync         = SafeGet(() => (bool)_driver.CanSync);
        _canSetTracking  = SafeGet(() => (bool)_driver.CanSetTracking);
        _canPierSide     = SafeGet(() => (bool)_driver.CanPulseGuide); // proxy
        _canMoveAxis     = SafeGet(() => (bool)_driver.CanMoveAxis(0));
    });

    public Task DisconnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        try { _driver.Connected = false; } catch { }
        try { Marshal.FinalReleaseComObject(_driver); } catch { }
        _driver = null;
    });

    public Task SlewAsync(double ra, double dec, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (_driver == null) return;
            // SlewToCoordinatesAsync returns immediately; the mount
            // sets Slewing=true and clears it when the move completes.
            // Polaris's slew-center loop polls IsSlewing so we don't
            // need to block here.
            SafeSet(() => _driver!.Tracking = true);
            _driver!.SlewToCoordinatesAsync(ra, dec);
        });

    public Task SyncAsync(double ra, double dec, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (!_canSync || _driver == null) return;
            _driver!.SyncToCoordinates(ra, dec);
        });

    public Task ParkAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (!_canPark || _driver == null) return;
        _driver!.Park();
    });

    public Task UnparkAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (!_canUnpark || _driver == null) return;
        _driver!.Unpark();
    });

    public Task SetTrackingAsync(bool enabled, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (!_canSetTracking || _driver == null) return;
            _driver!.Tracking = enabled;
        });

    public Task AbortSlewAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        try { _driver.AbortSlew(); } catch { /* state-dependent */ }
    });

    // ICameraV3.MoveAxis takes an axis index (0=Primary/RA, 1=Secondary/Dec)
    // and a rate in deg/s. Polaris jog uses the mount's tracking rate
    // as a default, ~1× sidereal ≈ 0.00417 deg/s on Primary.
    private const double JogRate = 0.5;            // half-degree per second

    public Task MoveNorthAsync(CancellationToken ct = default) => Jog(1, +JogRate);
    public Task MoveSouthAsync(CancellationToken ct = default) => Jog(1, -JogRate);
    public Task MoveEastAsync (CancellationToken ct = default) => Jog(0, +JogRate);
    public Task MoveWestAsync (CancellationToken ct = default) => Jog(0, -JogRate);
    public Task StopMotionAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (!_canMoveAxis || _driver == null) return;
        try { _driver.MoveAxis(0, 0.0); } catch { }
        try { _driver.MoveAxis(1, 0.0); } catch { }
    });

    private Task Jog(int axis, double rate) => _disp.Invoke(() => {
        if (!_canMoveAxis || _driver == null) return;
        try { _driver.MoveAxis(axis, rate); } catch { }
    });

    public void Dispose() {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        _disp.Dispose();
    }

    private T Read<T>(Func<T> read, T fallback = default!) =>
        _driver == null ? fallback
        : _disp.Invoke(() => SafeGet(read, fallback)).GetAwaiter().GetResult();

    private static T SafeGet<T>(Func<T> read, T fallback = default!) {
        try { return read(); } catch { return fallback; }
    }
    private static void SafeSet(Action write) { try { write(); } catch { } }
}
