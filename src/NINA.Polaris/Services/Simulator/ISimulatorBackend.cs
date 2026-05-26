namespace NINA.Polaris.Services.Simulator;

/// <summary>
/// Per-platform launcher for a synthetic-equipment stack. Lets the
/// user test the whole Polaris pipeline without real cameras + mounts.
///
/// Two concrete backends today: <c>IndiSimulatorBackend</c> for
/// Linux/macOS (spawns indiserver with the indi_simulator_* drivers)
/// and <c>AscomSimulatorBackend</c> for Windows (drives the Alpaca
/// Omni Simulator binary that exposes the ASCOM simulator devices
/// over a local Alpaca HTTP server).
///
/// SimulatorService picks one at startup based on the host OS;
/// callers (auto-start, endpoints, UI) talk to the interface and
/// never need to know which one is live.
/// </summary>
public interface ISimulatorBackend {
    /// <summary>Short tag for the picked backend. "indi" on
    /// Linux/macOS, "ascom" on Windows. Used by the WS payload so
    /// the UI can render the right install-hint banner when the
    /// stack isn't detected.</summary>
    string Kind { get; }

    /// <summary>True when the current OS can in principle host this
    /// backend. Returns false on a Windows host for the INDI backend
    /// and on a Linux host for the ASCOM backend, the orchestrator
    /// uses this to refuse to register a backend it can't drive.</summary>
    bool IsSupported { get; }

    /// <summary>URL the UI links to when <see cref="DetectInstallAsync"/>
    /// reports the binaries aren't on the host yet.</summary>
    string DownloadInstructionsUrl { get; }

    /// <summary>Probe the host for the simulator binaries. Cheap,
    /// shells out to <c>which</c> / reads a registry key. Cached by
    /// <c>SimulatorService.LastDetect</c>; only re-run on user click
    /// or service restart.</summary>
    Task<SimulatorInstall> DetectInstallAsync(CancellationToken ct = default);

    /// <summary>Spawn the simulator stack. Returns true on success;
    /// false when the binaries aren't installed, when something
    /// already owns the requested port, or when launch failed for
    /// any reason. Errors land on the service's LastError property
    /// (not thrown) so the WS payload + UI surface them cleanly.</summary>
    Task<bool> LaunchAsync(SimulatorLaunchRequest req, CancellationToken ct = default);

    /// <summary>Graceful shutdown, SIGTERM (or equivalent) first
    /// with a short timeout, then force-kill. Idempotent: safe to
    /// call when nothing's running. Doesn't error.</summary>
    Task ShutdownAsync(CancellationToken ct = default);

    /// <summary>Cheap TCP probe, does the simulator service answer
    /// on its expected port? Used by the orchestrator's periodic
    /// health check to surface crashes ("was running, now isn't")
    /// without us polling subprocess exit codes.</summary>
    Task<bool> IsRunningAsync(CancellationToken ct = default);

    /// <summary>Add one driver to a running simulator stack without
    /// restarting it (SIM-8). For INDI this writes a <c>start</c>
    /// command to the indiserver FIFO; for Alpaca it asks the Omni
    /// Simulator to enable a device. Returns false when the backend
    /// isn't running yet (call <see cref="LaunchAsync"/> first), when
    /// the device tag isn't supported on this host, or when the
    /// backend rejected the request.</summary>
    Task<bool> AddDeviceAsync(string device, CancellationToken ct = default);

    /// <summary>Remove one driver from a running simulator stack
    /// without restarting it. INDI <c>stop</c> via FIFO. Returns
    /// false when nothing is running or when the device wasn't
    /// already started.</summary>
    Task<bool> RemoveDeviceAsync(string device, CancellationToken ct = default);
}

/// <summary>Snapshot of what the host's simulator install looks like.
/// Built once per detection pass and cached on the orchestrator.</summary>
/// <param name="Installed">True iff the launcher could find every
/// binary it needs to start a useful stack.</param>
/// <param name="Version">Human-readable version string parsed from
/// <c>indiserver --version</c> / equivalent. Null when undetectable.</param>
/// <param name="Path">Resolved path to the primary binary
/// (<c>indiserver</c> on Linux, <c>AlpacaOmniSimulator.exe</c> on
/// Windows). Null when not installed.</param>
/// <param name="AvailableDevices">Subset of {ccd, telescope, focus,
/// wheel, guide, dome, weather} that the host actually has driver
/// binaries for. UI shows checkboxes only for these.</param>
/// <param name="Error">When <c>Installed</c> is false, a short
/// human-readable reason ("indiserver not in PATH", "ASCOM Platform
/// missing", ...). Null on success.</param>
public record SimulatorInstall(
    bool Installed,
    string? Version,
    string? Path,
    IReadOnlyList<string> AvailableDevices,
    string? Error);

/// <summary>Per-launch request. Which device drivers to start +
/// which port to expose the simulator service on.</summary>
/// <param name="Devices">Whitelist of device tags to start. Each
/// must appear in <see cref="SimulatorInstall.AvailableDevices"/>
/// from the latest detection. Unknown tags are silently dropped.</param>
/// <param name="Port">TCP port the simulator service listens on.
/// 7624 (INDI default) on Linux; 32323 (Alpaca default) on Windows.
/// Pass through unchanged to the subprocess args.</param>
public record SimulatorLaunchRequest(
    IReadOnlyList<string> Devices,
    int Port = 7624);

/// <summary>Canonical device tags Polaris accepts in
/// <see cref="SimulatorLaunchRequest.Devices"/>. Matches the
/// suffixes in the indi_simulator_* binary family one-for-one and
/// covers everything an ASCOM Platform install exposes.</summary>
public static class SimulatorDeviceTags {
    public const string Ccd = "ccd";
    public const string Telescope = "telescope";
    public const string Focus = "focus";
    public const string Wheel = "wheel";
    public const string Guide = "guide";
    public const string Dome = "dome";
    public const string Weather = "weather";

    public static readonly IReadOnlyList<string> All = new[] {
        Ccd, Telescope, Focus, Wheel, Guide, Dome, Weather
    };

    /// <summary>Default selection for a fresh install, covers
    /// every workflow that doesn't involve guide-cam dithering
    /// or dome slaving. Keeps RAM usage on a Pi 2 sane.</summary>
    public static readonly IReadOnlyList<string> Defaults = new[] {
        Ccd, Telescope, Focus, Wheel
    };
}
