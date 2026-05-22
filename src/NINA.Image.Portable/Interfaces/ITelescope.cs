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
/// already — Sky-Watcher SynScan via <c>indi_skywatcherAltAzMount</c>,
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

    /// <summary>Sync — tell the mount that its current physical pointing
    /// equals the given JNow coordinates. Used by the plate-solve
    /// Slew &amp; Center loop after a successful solve.</summary>
    Task SyncAsync(double ra, double dec, CancellationToken ct = default);

    Task ParkAsync(CancellationToken ct = default);
    Task UnparkAsync(CancellationToken ct = default);
    Task SetTrackingAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Cancel any in-progress slew. Tracking state is
    /// backend-dependent — most mounts leave the tracking switch
    /// untouched on abort.</summary>
    Task AbortSlewAsync(CancellationToken ct = default);

    // Manual jog — N/S/E/W. The button stays pressed until the
    // corresponding "stop" overload is called (or StopMotion which
    // halts every axis at once). Most UIs use mouse-down + mouse-up
    // bindings on top of these.
    Task MoveNorthAsync(CancellationToken ct = default);
    Task MoveSouthAsync(CancellationToken ct = default);
    Task MoveEastAsync(CancellationToken ct = default);
    Task MoveWestAsync(CancellationToken ct = default);
    Task StopMotionAsync(CancellationToken ct = default);
}

/// <summary>Optional-feature flags. Used by the UI to decide which
/// controls to render for the currently-selected mount.</summary>
public record MountCapabilities(
    bool SupportsPark,
    bool SupportsTrackingToggle,
    bool SupportsSync,
    bool SupportsPierSide,
    bool SupportsManualJog) {
    /// <summary>Typical equatorial GEM profile (INDI / ASCOM / direct
    /// WiFi serial-protocol mounts) — everything available.</summary>
    public static readonly MountCapabilities GermanEquatorial = new(
        SupportsPark: true, SupportsTrackingToggle: true,
        SupportsSync: true, SupportsPierSide: true, SupportsManualJog: true);

    /// <summary>Typical alt-az / fork profile (Sky-Watcher AZ-GTi,
    /// Celestron NexStar SE, iOptron MiniTower). No pier side; the
    /// rest applies.</summary>
    public static readonly MountCapabilities AltAz = new(
        SupportsPark: true, SupportsTrackingToggle: true,
        SupportsSync: true, SupportsPierSide: false, SupportsManualJog: true);
}
