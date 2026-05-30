using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;

namespace NINA.Polaris.Services;

/// <summary>
/// Windows-only sibling of <see cref="Phd2GuiSessionService"/>. Where
/// xpra forwards an Xorg display on Linux, on Windows we host the
/// PHD2 GUI via TightVNC: TightVNC's Windows service captures the
/// desktop on 127.0.0.1:5900, the <c>/phd2-vnc-ws</c> bridge pipes
/// the raw RFB stream to a noVNC HTML5 client embedded in the GUIDE
/// tab.
///
/// Lifecycle: TightVNC installs as a Windows Service that the host
/// OS manages. Polaris doesn't spawn a process — it only verifies
/// the service exists, is running, and is listening on the loopback
/// port. Start/Stop buttons call <see cref="ServiceController"/>;
/// those calls require Polaris to be running elevated (admin), so
/// they cleanly fail with an actionable error otherwise.
///
/// Cross-platform compile: the BCL types touched here
/// (<see cref="ServiceController"/>, <see cref="Registry"/>) compile
/// on Linux but throw <see cref="PlatformNotSupportedException"/> at
/// runtime. Every method that reaches into them is annotated with
/// <see cref="SupportedOSPlatformAttribute"/> and only invoked behind
/// an <see cref="OperatingSystem.IsWindows"/> guard, so the Linux
/// build never trips the unsupported paths.
/// </summary>
public class Phd2VncSessionService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly ILogger<Phd2VncSessionService> _logger;

    // ── Detection state (refreshed by ExecuteAsync) ──────────────────
    public bool TightVncInstalled { get; private set; }
    public string? TightVncVersion { get; private set; }
    public string? TightVncPath { get; private set; }
    public bool ServiceInstalled { get; private set; }
    public bool ServiceRunning { get; private set; }
    public bool Listening { get; private set; }
    public int Port { get; }
    public DateTime? LastHealthCheckAt { get; private set; }
    public string? LastError { get; private set; }

    public bool IsSupportedOs =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string OperatingSystemDescription => RuntimeInformation.OSDescription;

    /// <summary>One-line reason the embedded GUI via VNC is unavailable
    /// on this host, or null when it should work. UI surfaces this in
    /// the GUIDE tab banner so the user gets a specific "why" instead
    /// of a generic "not supported".</summary>
    public string? UnsupportedReason {
        get {
            if (!IsSupportedOs)
                return $"Embedded PHD2 GUI via VNC requires Windows. {RuntimeInformation.OSDescription} is not supported (use xpra on Linux instead).";
            return null;
        }
    }

    /// <summary>Public-facing service name expected on the host. The
    /// TightVNC installer registers <c>tvnserver</c>. We don't try
    /// to match alternate VNC servers (UltraVNC, RealVNC) here —
    /// users running those just stop the TightVNC card from appearing
    /// and connect to their server directly; the bridge still works
    /// against whatever listens on the configured port.</summary>
    public const string ServiceName = "tvnserver";

    public Phd2VncSessionService(IConfiguration config,
                                 ILogger<Phd2VncSessionService> logger) {
        _config = config;
        _logger = logger;
        Port = _config.GetValue("Phd2Vnc:Port", 5900);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!IsSupportedOs) {
            _logger.LogInformation("Phd2VncSessionService: OS {Os} not supported (Windows only), service idle",
                RuntimeInformation.OSDescription);
            LastError = UnsupportedReason;
            return;
        }

        await RefreshDetectionAsync(stoppingToken);
        if (!TightVncInstalled) {
            _logger.LogInformation("Phd2VncSessionService: TightVNC not detected, install from https://www.tightvnc.com/download.php to enable embedded PHD2 GUI on Windows");
        }

        // 15s loop, same cadence as the xpra service. Refreshes
        // service state + TCP probe so the UI stays in sync without
        // polling endpoints.
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (TightVncInstalled && OperatingSystem.IsWindows()) {
                    RefreshServiceStateWin();
                    await ProbeListeningAsync(stoppingToken);
                }
                LastHealthCheckAt = DateTime.UtcNow;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "TightVNC health probe failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Re-runs the install detection (registry + service +
    /// listening probe). Called by ExecuteAsync at startup and by the
    /// /api/guider/vnc-session/redetect endpoint when the user reports
    /// they just installed TightVNC.</summary>
    public async Task RefreshDetectionAsync(CancellationToken ct = default) {
        if (!OperatingSystem.IsWindows()) return;
        try {
            DetectInstallWin();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "TightVNC install detection failed");
            TightVncInstalled = false;
        }
        if (TightVncInstalled) {
            try { RefreshServiceStateWin(); }
            catch (Exception ex) { _logger.LogDebug(ex, "TightVNC service-state probe failed"); }
            try { await ProbeListeningAsync(ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "TightVNC listening probe failed"); }
        }
    }

    [SupportedOSPlatform("windows")]
    private void DetectInstallWin() {
        // TightVNC's installer writes HKLM\SOFTWARE\TightVNC\Server
        // (64-bit) and registers the install path. Same pattern works
        // for 32-bit installer via the Wow6432Node redirect — Registry
        // class handles the view automatically.
        //
        // SafeOpenHklm catches SecurityException / UnauthorizedAccessException
        // when the polaris user can't read the key (locked-down domain
        // box, AppLocker policy, running under a low-privilege service
        // account). Without it the outer RefreshDetectionAsync catch
        // still runs but the debugger flags it as a first-chance
        // exception every time the user opens Settings, which is
        // noisy in logs and confusing during debugging.
        using var key = SafeOpenHklm(@"SOFTWARE\TightVNC\Server")
                     ?? SafeOpenHklm(@"SOFTWARE\Wow6432Node\TightVNC\Server");
        if (key == null) {
            TightVncInstalled = false;
            TightVncPath = null;
            TightVncVersion = null;
            return;
        }
        // The installer writes either "InstallPath" or "Path"
        // depending on version. Fall back to the standard Program
        // Files location if neither is set.
        var installPath = key.GetValue("InstallPath") as string
                       ?? key.GetValue("Path") as string;
        string? exePath = null;
        if (!string.IsNullOrWhiteSpace(installPath)) {
            var candidate = Path.Combine(installPath, "tvnserver.exe");
            if (File.Exists(candidate)) exePath = candidate;
        }
        // Fallback: try the canonical 64-bit and 32-bit install dirs.
        // Useful when the registry was hand-edited / partially wiped.
        if (exePath == null) {
            foreach (var root in new[] {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)")
            }) {
                if (string.IsNullOrEmpty(root)) continue;
                var candidate = Path.Combine(root, "TightVNC", "tvnserver.exe");
                if (File.Exists(candidate)) { exePath = candidate; break; }
            }
        }

        if (exePath == null) {
            TightVncInstalled = false;
            TightVncPath = null;
            TightVncVersion = null;
            return;
        }

        TightVncPath = exePath;
        try {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            TightVncVersion = fvi.ProductVersion ?? fvi.FileVersion ?? "unknown";
        } catch {
            TightVncVersion = "unknown";
        }
        TightVncInstalled = true;
        _logger.LogInformation("Phd2VncSessionService: detected TightVNC v{Ver} at {Path}",
            TightVncVersion, TightVncPath);
    }

    /// <summary>Open an HKLM subkey returning null on missing OR on
    /// access-denied. Standard user accounts can read most of HKLM
    /// but locked-down corporate / domain machines may deny read on
    /// SOFTWARE entries; treating that as "not installed" lets the
    /// detection fall through cleanly to the Program Files probe.
    /// <para>[DebuggerNonUserCode] tells Visual Studio / Rider to
    /// treat this helper as library code so the SecurityException
    /// raised inside <c>OpenSubKey</c> does NOT trigger a first-
    /// chance debugger break under "Just My Code". Without the
    /// attribute the catch still runs in release and the user just
    /// sees clean fallback behaviour — but during development VS
    /// pops the "Exception thrown" balloon every single time the
    /// Settings panel reads PHD2 VNC state, which is noise.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    [System.Diagnostics.DebuggerNonUserCode]
    private static RegistryKey? SafeOpenHklm(string subkey) {
        try { return Registry.LocalMachine.OpenSubKey(subkey); }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private void RefreshServiceStateWin() {
        try {
            using var sc = new ServiceController(ServiceName);
            // Accessing Status throws InvalidOperationException when the
            // service doesn't exist — catch it as "not installed".
            var status = sc.Status;
            ServiceInstalled = true;
            ServiceRunning = status == ServiceControllerStatus.Running;
        } catch (InvalidOperationException) {
            ServiceInstalled = false;
            ServiceRunning = false;
        }
    }

    /// <summary>TCP probe against the local TightVNC server. 500 ms
    /// timeout is plenty for loopback — anything slower means the
    /// service is listening but the port handler is wedged, which the
    /// user needs to see as "not listening" so they restart the
    /// service.</summary>
    private async Task ProbeListeningAsync(CancellationToken ct) {
        try {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(IPAddress.Loopback, Port, ct).AsTask();
            var timeout = Task.Delay(500, ct);
            var winner = await Task.WhenAny(connect, timeout);
            Listening = winner == connect && tcp.Connected;
        } catch {
            Listening = false;
        }
    }

    /// <summary>Start the TightVNC Windows service. Requires Polaris
    /// to be running elevated (admin). Returns false + sets LastError
    /// on permission denial so the UI can surface the actionable
    /// "rerun Polaris as admin or start the service via services.msc"
    /// hint.</summary>
    public async Task<bool> StartServiceAsync(CancellationToken ct = default) {
        if (!OperatingSystem.IsWindows()) { LastError = "Not supported on this OS"; return false; }
        if (!TightVncInstalled) { LastError = "TightVNC not installed"; return false; }
        return await Task.Run(() => {
            // Re-check inside the Task.Run lambda so the platform
            // analyzer recognizes the guard at this call site too.
            return OperatingSystem.IsWindows() && TryControlServiceWin(start: true);
        }, ct);
    }

    /// <summary>Stop the TightVNC Windows service. Same admin
    /// requirement as <see cref="StartServiceAsync"/>.</summary>
    public async Task<bool> StopServiceAsync(CancellationToken ct = default) {
        if (!OperatingSystem.IsWindows()) { LastError = "Not supported on this OS"; return false; }
        if (!TightVncInstalled) { LastError = "TightVNC not installed"; return false; }
        return await Task.Run(() => {
            return OperatingSystem.IsWindows() && TryControlServiceWin(start: false);
        }, ct);
    }

    [SupportedOSPlatform("windows")]
    private bool TryControlServiceWin(bool start) {
        try {
            using var sc = new ServiceController(ServiceName);
            var target = start
                ? ServiceControllerStatus.Running
                : ServiceControllerStatus.Stopped;
            if (sc.Status == target) {
                LastError = null;
                return true;
            }
            if (start) sc.Start();
            else sc.Stop();
            sc.WaitForStatus(target, TimeSpan.FromSeconds(10));
            ServiceRunning = sc.Status == ServiceControllerStatus.Running;
            ServiceInstalled = true;
            LastError = null;
            return sc.Status == target;
        } catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 0x80004005 || ex.NativeErrorCode == 5) {
            // ERROR_ACCESS_DENIED (5) — Polaris not elevated.
            LastError = "Access denied. Run Polaris as administrator, " +
                        "or start/stop the TightVNC service via services.msc.";
            return false;
        } catch (InvalidOperationException) {
            LastError = $"Service '{ServiceName}' not installed.";
            ServiceInstalled = false;
            return false;
        } catch (Exception ex) {
            LastError = ex.Message;
            return false;
        }
    }
}
