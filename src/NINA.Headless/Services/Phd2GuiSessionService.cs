using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NINA.Headless.Services;

/// <summary>
/// Manages a long-lived xpra session that hosts PHD2's GUI under a
/// detachable Xorg-dummy display, so users can interact with PHD2's
/// native window (Profile Wizard, Brain dialog, Guiding Assistant)
/// via xpra's HTML5 client embedded in the Polaris UI.
///
/// Linux-only feature. On Windows / macOS this service short-circuits
/// to <c>XpraInstalled = false</c> and all start/stop ops return
/// "not supported on this OS". The Settings/UI surface that explicitly.
///
/// Lifecycle:
/// - Eager (Phd2Gui:AutoStart=true): spawn the session shortly after
///   Polaris startup so the GUI tab is instant.
/// - Lazy (default): user clicks "Start session" in the UI; we spawn
///   xpra + PHD2 on demand.
///
/// xpra invocation (from PHD2 maintainer's recommendation in issue #683):
/// <code>
///   xpra start :100 --start=phd2 --html=on --bind-tcp=127.0.0.1:14600 \
///        --daemon=yes --systemd-run=no --no-pulseaudio
/// </code>
/// Bind on 127.0.0.1 only — Polaris reverse-proxies /phd2-gui/* to it,
/// so the public surface (auth, TLS via Relay) stays unified.
/// </summary>
public class Phd2GuiSessionService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly ILogger<Phd2GuiSessionService> _logger;

    public bool XpraInstalled { get; private set; }
    public string? XpraVersion { get; private set; }
    public string? XpraPath { get; private set; }
    public bool SessionRunning { get; private set; }
    public int DisplayNumber { get; }
    public int BindPort { get; }
    public string OperatingSystem => Environment.OSVersion.Platform.ToString();
    public bool IsSupportedOs => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public DateTime? LastHealthCheckAt { get; private set; }
    public string? LastError { get; private set; }

    public Phd2GuiSessionService(IConfiguration config,
                                 ILogger<Phd2GuiSessionService> logger) {
        _config = config;
        _logger = logger;
        DisplayNumber = _config.GetValue("Phd2Gui:DisplayNumber", 100);
        BindPort      = _config.GetValue("Phd2Gui:BindPort", 14600);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!IsSupportedOs) {
            _logger.LogInformation("Phd2GuiSessionService: OS {Os} not supported (Linux only) — service idle",
                RuntimeInformation.OSDescription);
            return;
        }

        await DetectXpraAsync(stoppingToken);
        if (!XpraInstalled) {
            _logger.LogInformation("Phd2GuiSessionService: xpra not found — install via 'sudo apt install xpra' to enable embedded PHD2 GUI");
        }

        // 3-second stagger so PHD2AutoStartService gets a head start (it may
        // be launching PHD2 itself; if we beat it, the xpra session would
        // launch its own PHD2 and we'd end up with two).
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (TaskCanceledException) { return; }

        var autoStart = _config.GetValue("Phd2Gui:AutoStart", false);
        if (XpraInstalled && autoStart) {
            _logger.LogInformation("Phd2GuiSessionService: AutoStart enabled — starting xpra session");
            try { await StartSessionAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-start of xpra session failed"); }
        }

        // Periodic health check (every 15s) — refreshes SessionRunning
        // so the UI indicator stays accurate without polling.
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (XpraInstalled) await ProbeHealthAsync(stoppingToken);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "xpra health probe failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task DetectXpraAsync(CancellationToken ct) {
        try {
            var which = await RunCommandAsync("which", "xpra", ct);
            if (string.IsNullOrWhiteSpace(which.stdout)) return;
            XpraPath = which.stdout.Trim();

            var ver = await RunCommandAsync("xpra", "--version", ct);
            // `xpra --version` prints "xpra v6.0" or "xpra version 6.0" — grab first token after "v"
            var line = (ver.stdout + " " + ver.stderr).Trim();
            var idx = line.IndexOf('v');
            XpraVersion = idx >= 0 ? line[(idx + 1)..].Split(new[] { ' ', '\n', '\r' })[0] : "unknown";
            XpraInstalled = true;
            _logger.LogInformation("Phd2GuiSessionService: detected xpra v{Ver} at {Path}", XpraVersion, XpraPath);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "xpra detection failed");
            XpraInstalled = false;
        }
    }

    public async Task<bool> StartSessionAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (!XpraInstalled) { LastError = "xpra not installed"; return false; }
        if (await ProbeHealthAsync(ct)) {
            _logger.LogDebug("StartSessionAsync: already running");
            return true;
        }
        var args = string.Join(' ', new[] {
            "start",
            $":{DisplayNumber}",
            "--start=phd2",
            "--html=on",
            $"--bind-tcp=127.0.0.1:{BindPort}",
            "--daemon=yes",
            "--systemd-run=no",
            "--no-pulseaudio"
        });
        _logger.LogInformation("Spawning xpra: {Args}", args);
        var res = await RunCommandAsync("xpra", args, ct, timeoutMs: 30000);
        if (res.exitCode != 0) {
            LastError = $"xpra start exited {res.exitCode}: {res.stderr}";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }
        // xpra forks into the background — give it a moment then probe
        for (int i = 0; i < 20; i++) {
            try { await Task.Delay(500, ct); } catch (TaskCanceledException) { return false; }
            if (await ProbeHealthAsync(ct)) {
                _logger.LogInformation("xpra session on :{Display} listening on 127.0.0.1:{Port}",
                    DisplayNumber, BindPort);
                LastError = null;
                return true;
            }
        }
        LastError = "xpra start succeeded but TCP probe never responded";
        return false;
    }

    public async Task<bool> StopSessionAsync(CancellationToken ct = default) {
        if (!IsSupportedOs || !XpraInstalled) return false;
        var res = await RunCommandAsync("xpra", $"stop :{DisplayNumber}", ct, timeoutMs: 15000);
        if (res.exitCode != 0) {
            LastError = $"xpra stop exited {res.exitCode}: {res.stderr}";
            return false;
        }
        SessionRunning = false;
        LastError = null;
        return true;
    }

    public async Task<bool> RestartSessionAsync(CancellationToken ct = default) {
        await StopSessionAsync(ct);
        // Brief wait for xpra to release the port.
        try { await Task.Delay(2000, ct); } catch (TaskCanceledException) { return false; }
        return await StartSessionAsync(ct);
    }

    /// <summary>TCP probe — returns true if something is listening on 127.0.0.1:BindPort.</summary>
    private async Task<bool> ProbeHealthAsync(CancellationToken ct) {
        try {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", BindPort, ct).AsTask();
            var timeout = Task.Delay(500, ct);
            var winner = await Task.WhenAny(connect, timeout);
            LastHealthCheckAt = DateTime.UtcNow;
            SessionRunning = winner == connect && tcp.Connected;
            return SessionRunning;
        } catch {
            SessionRunning = false;
            return false;
        }
    }

    private static async Task<(int exitCode, string stdout, string stderr)>
        RunCommandAsync(string file, string args, CancellationToken ct, int timeoutMs = 5000) {
        var psi = new ProcessStartInfo {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var waitTask = p.WaitForExitAsync(ct);
        var winner = await Task.WhenAny(waitTask, Task.Delay(timeoutMs, ct));
        if (winner != waitTask) {
            try { p.Kill(true); } catch { }
            return (-1, "", "Process timed out");
        }
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}
