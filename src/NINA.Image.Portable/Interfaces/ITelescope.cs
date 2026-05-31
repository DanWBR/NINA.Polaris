using NINA.Core.Enum;

namespace NINA.Image.Interfaces;

/// <summary>
/// Common contract every mount backend honours so the rest of Polaris
/// (EquipmentManager, status broadcaster, Slew &amp; Center workflow,
/// Sequencer, meridian-flip service) can stay backend-agnostic.
///
/// <para>
/// Today the only implementation is <c>IndiTelescope</c> which talks to
/// any mount the running INDI server exposes (covers most WiFi mounts
/// already, Sky-Watcher SynScan via <c>indi_skywatcherAltAzMount</c>,
/// Celestron via <c>indi_celestron_aux</c>, iOptron via
/// <c>indi_ioptron_v3</c>, etc.). Direct WiFi / Bluetooth drivers
/// (SynScan UDP, NexStar TCP, ...) plug in here without touching the
/// existing capture / sequencing code.
/// </para>
///
/// <para>
/// Capability flags on <see cref="Capabilities"/> let the UI hide
/// features that don't apply to a given mount (e.g. pier-flip
/// notifications, GPS readout, slew-rate selection).
/// </para>
/// </summary>
public interface ITelescope {
    string DeviceName { get; }
    bool IsConnected { get; }

    /// <summary>Current pointing in JNow equatorial coordinates.
    /// RA in hours (0..24), Dec in degrees (-90..+90).</summary>
    double RightAscension { get; }
    double Declination { get; }

    /// <summary>Current pointing in topocentric horizontal coordinates.
    /// Altitude + azimuth in degrees. Backends that can't derive these
    /// directly may return NaN; the status broadcaster recomputes from
    /// RA / Dec + observer location when that happens.</summary>
    double Altitude { get; }
    double Azimuth { get; }

    bool IsTracking { get; }
    bool IsParked { get; }
    bool IsSlewing { get; }

    /// <summary>Pier side for GEM mounts. <see cref="PierSide.pierUnknown"/>
    /// for alt-az / fork mounts and for drivers that don't expose it.</summary>
    PierSide SideOfPier { get; }

    /// <summary>Which optional features the backend supports. Drives
    /// UI affordances (Park button hidden when the driver can't park,
    /// pier-side indicator hidden when SideOfPier is always unknown,
    /// etc.).</summary>
    MountCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Slew to the given JNow coordinates and start tracking
    /// at the mount's current tracking rate (typically sidereal).</summary>
    Task SlewAsync(double ra, double dec, CancellationToken ct = default);

    /// <summary>Sync, tell the mount that its current physical pointing
    /// equals the given JNow coordinates. Used by the plate-solve
    /// Slew &amp; Center loop after a successful solve.</summary>
    Task SyncAsync(double ra, double dec, CancellationToken ct = default);

    Task ParkAsync(CancellationToken ct = default);
    Task UnparkAsync(CancellationToken ct = default);
    Task SetTrackingAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Cancel any in-progress slew. Tracking state is
    /// backend-dependent, most mounts leave the tracking switch
    /// untouched on abort.</summary>
    Task AbortSlewAsync(CancellationToken ct = default);

    // Manual jog, N/S/E/W. The button stays pressed until the
    // corresponding "stop" overload is called (or StopMotion which
    // halts every axis at once). Most UIs use mouse-down + mouse-up
    // bindings on top of these.
    Task MoveNorthAsync(CancellationToken ct = default);
    Task MoveSouthAsync(CancellationToken ct = default);
    Task MoveEastAsync(CancellationToken ct = default);
    Task MoveWestAsync(CancellationToken ct = default);
    Task StopMotionAsync(CancellationToken ct = default);

    /// <summary>Send the mount to its mechanical home position. Most
    /// GoTo mounts have a defined "home" pose (CW down, RA/Dec hard
    /// stops) used for unattended power-up, dawn dew-cap close, polar
    /// alignment routines, etc. Default impl throws so that the
    /// endpoint surfaces a clean 501 on backends without home support
    /// rather than silently no-op; UI checks
    /// <see cref="MountCapabilities.SupportsFindHome"/> before showing
    /// the button.</summary>
    Task FindHomeAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("FindHome not supported by this mount driver");

    /// <summary>Push the observer's geographic position into the mount.
    /// Critical for GoTo accuracy: every mount internally converts
    /// RA/Dec → alt/az using its own configured lat/long, so a wrong
    /// site location causes systematic slew errors that look like
    /// alignment drift but are actually coordinate-system bias.
    ///
    /// Latitude in degrees (+N / -S), longitude in degrees
    /// (+E / -W per IAU; INDI uses 0..360 but accepts negatives),
    /// elevation in metres above sea level. Default impl throws so
    /// backends opt in by overriding; UI checks
    /// <see cref="MountCapabilities.SupportsSetSiteLocation"/> before
    /// showing the button.</summary>
    Task SetSiteLocationAsync(double latitudeDeg, double longitudeDeg,
            double elevationMetres, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "SetSiteLocation not supported by this mount driver");

    /// <summary>Push UTC time + offset-from-UTC into the mount.
    /// Companion to <see cref="SetSiteLocationAsync"/>: mounts use
    /// (lat, lon, utc) together to compute local sidereal time, which
    /// drives every RA → alt/az conversion. A correct location with a
    /// stale clock causes the same systematic GoTo error as a wrong
    /// location with a correct clock — both inputs need to be right.
    ///
    /// utc is the wall clock in UTC; offsetHoursFromUtc is the local
    /// timezone offset in hours east of UTC (INDI standard, e.g.
    /// -3.0 for Brasília UTC-3, +1.0 for CET).</summary>
    Task SetSiteTimeAsync(DateTime utc, double offsetHoursFromUtc,
            CancellationToken ct = default) =>
        throw new NotSupportedException(
            "SetSiteTime not supported by this mount driver");

    /// <summary>Select the tracking rate model. Sidereal follows the
    /// stars (default), Lunar follows the Moon's mean motion, Solar
    /// follows the Sun. Required by some INDI drivers BEFORE
    /// <see cref="SetTrackingAsync"/> can actually engage — without
    /// a mode selected they silently ignore TRACK_ON. Throws on
    /// backends without the capability; UI checks
    /// <see cref="MountCapabilities.SupportsTrackingModes"/>.</summary>
    Task SetTrackingModeAsync(TrackingMode mode, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "SetTrackingMode not supported by this mount driver");
}

/// <summary>Tracking rate models defined by the INDI
/// TELESCOPE_TRACK_MODE standard property.</summary>
public enum TrackingMode {
    Sidereal,
    Solar,
    Lunar
}

/// <summary>Optional-feature flags. Used by the UI to decide which
/// controls to render for the currently-selected mount.</summary>
public record MountCapabilities(
    bool SupportsPark,
    bool SupportsTrackingToggle,
    bool SupportsSync,
    bool SupportsPierSide,
    bool SupportsManualJog,
    bool SupportsFindHome = false,
    bool SupportsSetSiteLocation = false,
    bool SupportsSetSiteTime = false,
    bool SupportsTrackingModes = false) {
    /// <summary>Typical equatorial GEM profile (INDI / ASCOM / direct
    /// WiFi serial-protocol mounts), everything available.
    /// FindHome defaults true here -- most GEM mounts expose it via
    /// TELESCOPE_HOME (INDI) or CanFindHome (ASCOM). Backends that
    /// can't honour it will surface 501 from the endpoint, which the
    /// UI shows as an actionable toast.</summary>
    public static readonly MountCapabilities GermanEquatorial = new(
        SupportsPark: true, SupportsTrackingToggle: true,
        SupportsSync: true, SupportsPierSide: true, SupportsManualJog: true,
        SupportsFindHome: true, SupportsSetSiteLocation: true,
        SupportsSetSiteTime: true, SupportsTrackingModes: true);

    /// <summary>Typical alt-az / fork profile (Sky-Watcher AZ-GTi,
    /// Celestron NexStar SE, iOptron MiniTower). No pier side; the
    /// rest applies.</summary>
    public static readonly MountCapabilities AltAz = new(
        SupportsPark: true, SupportsTrackingToggle: true,
        SupportsSync: true, SupportsPierSide: false, SupportsManualJog: true,
        SupportsFindHome: true, SupportsSetSiteLocation: true,
        SupportsSetSiteTime: true, SupportsTrackingModes: true);
}
