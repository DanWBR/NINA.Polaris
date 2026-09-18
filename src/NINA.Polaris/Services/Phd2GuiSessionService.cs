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

using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NINA.Polaris.Services;

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
/// Bind on 127.0.0.1 only, Polaris reverse-proxies /phd2-gui/* to it,
/// so the public surface (auth, TLS via Relay) stays unified.
/// </summary>
public class Phd2GuiSessionService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly ILogger<Phd2GuiSessionService> _logger;

    public bool XpraInstalled { get; private set; }
    public string? XpraVersion { get; private set; }
    public string? XpraPath { get; private set; }
    /// <summary>Path of the standalone INDI Control Panel binary
    /// (<c>indi_control_panel</c>, ships with <c>indi-bin</c>) when
    /// present on PATH, else null. The Polaris UI exposes a "Launch
    /// INDI Control Panel" button in the RIGS tab that runs this
    /// inside the existing xpra session so the user can edit per-
    /// property device settings (cooler ramps, focuser presets, etc.)
    /// that PHD2's Connect dialog doesn't surface. Set once at
    /// service init.</summary>
    public string? IndiControlPanelPath { get; private set; }
    public bool IndiControlPanelInstalled => !string.IsNullOrEmpty(IndiControlPanelPath);
    public bool SessionRunning { get; private set; }
    public int DisplayNumber { get; }
    public int BindPort { get; }
    public string OperatingSystem => Environment.OSVersion.Platform.ToString();
    public bool IsSupportedOs => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>True when the current CPU architecture has a working
    /// xpra + Xorg-dummy stack. ARMv7 32-bit (Raspberry Pi 2 / 3
    /// with 32-bit Raspbian) is excluded, xpra installs from apt but
    /// crashes at session start: the dummy Xorg driver is unreliable
    /// on 32-bit ARM and several Python/GTK deps don't behave. Pi 4+
    /// with the 64-bit Raspberry Pi OS is fine.
    ///
    /// Reported separately from <see cref="IsSupportedOs"/> so the UI
    /// can show the right "why this isn't available" message instead
    /// of a generic "not Linux".</summary>
    public bool IsSupportedArch =>
        RuntimeInformation.OSArchitecture != Architecture.Arm;

    /// <summary>One-line human-readable reason the embedded GUI is
    /// unavailable on this host, or null when it should work.
    /// UI surfaces this in the GUIDE / Settings banner so the user
    /// doesn't see a generic "click Start to begin" button on a
    /// platform that physically can't run xpra.</summary>
    public string? UnsupportedReason {
        get {
            if (!IsSupportedOs)
                return $"Embedded PHD2 GUI requires Linux + xpra. {RuntimeInformation.OSDescription} is not supported.";
            if (!IsSupportedArch)
                return "Embedded PHD2 GUI requires 64-bit Linux. xpra has known issues on 32-bit ARM " +
                       "(Raspberry Pi 2 / 3 with 32-bit Raspberry Pi OS). Upgrade to 64-bit Pi OS on a Pi 4 / 5, " +
                       "or use PHD2's native window via X11 forwarding / VNC.";
            return null;
        }
    }

    public DateTime? LastHealthCheckAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>True when a <c>phd2</c> or <c>phd2.bin</c> process is
    /// currently alive on the host. Refreshed by the 15 s health-probe
    /// loop alongside the xpra TCP probe. Lets the UI distinguish
    /// "xpra session up, PHD2 inside" from "xpra session up but PHD2
    /// is missing" (the second happens when xpra's <c>--start=phd2</c>
    /// fails silently, or when PHD2 crashes inside the session). Both
    /// cases would otherwise show the same green pill, which is what
    /// triggered users to need shell access to debug.</summary>
    public bool Phd2Running { get; private set; }

    public Phd2GuiSessionService(IConfiguration config,
                                 ILogger<Phd2GuiSessionService> logger) {
        _config = config;
        _logger = logger;
        DisplayNumber = _config.GetValue("Phd2Gui:DisplayNumber", 100);
        BindPort      = _config.GetValue("Phd2Gui:BindPort", 14600);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!IsSupportedOs) {
            _logger.LogInformation("Phd2GuiSessionService: OS {Os} not supported (Linux only), service idle",
                RuntimeInformation.OSDescription);
            LastError = UnsupportedReason;
            return;
        }
        if (!IsSupportedArch) {
            // Don't even try to detect xpra on 32-bit ARM, install
            // succeeds via apt but session-start crashes; better to
            // surface the limitation cleanly upfront than to let the
            // user click Start and get a confusing process-died log.
            _logger.LogInformation("Phd2GuiSessionService: architecture {Arch} not supported (xpra unstable on 32-bit ARM), service idle",
                RuntimeInformation.OSArchitecture);
            LastError = UnsupportedReason;
            return;
        }

        await DetectXpraAsync(stoppingToken);
        if (!XpraInstalled) {
            _logger.LogInformation("Phd2GuiSessionService: xpra not found, install via 'sudo apt install xpra' to enable embedded PHD2 GUI");
        }

        // 3-second stagger so PHD2AutoStartService gets a head start (it may
        // be launching PHD2 itself; if we beat it, the xpra session would
        // launch its own PHD2 and we'd end up with two).
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (TaskCanceledException) { return; }

        var autoStart = _config.GetValue("Phd2Gui:AutoStart", false);
        if (XpraInstalled && autoStart) {
            _logger.LogInformation("Phd2GuiSessionService: AutoStart enabled, starting xpra session");
            try { await StartSessionAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-start of xpra session failed"); }
        }

        // Periodic health check (every 15s), refreshes SessionRunning
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
            // `xpra --version` prints "xpra v6.0" or "xpra version 6.0", grab first token after "v"
            var line = (ver.stdout + " " + ver.stderr).Trim();
            var idx = line.IndexOf('v');
            XpraVersion = idx >= 0 ? line[(idx + 1)..].Split(new[] { ' ', '\n', '\r' })[0] : "unknown";
            XpraInstalled = true;
            _logger.LogInformation("Phd2GuiSessionService: detected xpra v{Ver} at {Path}", XpraVersion, XpraPath);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "xpra detection failed");
            XpraInstalled = false;
        }
        await DetectIndiControlPanelAsync(ct);
    }

    /// <summary>
    /// Detect the standalone INDI Control Panel binary
    /// (<c>indi_control_panel</c>, ships with the <c>indi-bin</c> apt
    /// package). Tries <c>which</c> first, then falls back to a small
    /// list of well-known absolute paths so we still find the binary
    /// when <c>which</c> isn't on PATH (some minimal Debian containers)
    /// or when systemd hands us a stripped PATH.
    ///
    /// Re-callable: the status endpoint invokes this each tick when the
    /// current state says "not installed" so installing
    /// <c>indi-bin</c> after Polaris started is a self-healing event,
    /// no service restart required.
    /// </summary>
    public async Task DetectIndiControlPanelAsync(CancellationToken ct = default) {
        // 1) PATH lookup via `which`. Cheapest + finds non-standard
        //    installs (Snap, /opt/...). Silent on miss because `which`
        //    exits 1 with empty stdout when nothing matches.
        try {
            var which = await RunCommandAsync("which", "indi_control_panel", ct);
            var path = which.stdout?.Trim();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) {
                if (IndiControlPanelPath != path) {
                    IndiControlPanelPath = path;
                    _logger.LogInformation(
                        "Phd2GuiSessionService: detected indi_control_panel at {Path}",
                        IndiControlPanelPath);
                }
                return;
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi_control_panel detection via `which` failed");
        }
        // 2) Absolute-path fallback. Covers the common cases when
        //    `which` is missing or PATH was stripped. apt installs the
        //    binary at /usr/bin/indi_control_panel; the libindi source
        //    install drops it in /usr/local/bin.
        foreach (var candidate in new[] {
                     "/usr/bin/indi_control_panel",
                     "/usr/local/bin/indi_control_panel",
                     "/opt/indi/bin/indi_control_panel",
                 }) {
            try {
                if (File.Exists(candidate)) {
                    if (IndiControlPanelPath != candidate) {
                        IndiControlPanelPath = candidate;
                        _logger.LogInformation(
                            "Phd2GuiSessionService: detected indi_control_panel via fallback at {Path}",
                            IndiControlPanelPath);
                    }
                    return;
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "fallback path check for {Path} failed", candidate);
            }
        }
        // Nothing matched. Leave previous Path intact if non-null (we
        // don't want a transient FS hiccup to flip the UI to "missing"
        // mid-session); only set to null when it was already null.
        if (IndiControlPanelPath != null) {
            _logger.LogDebug("indi_control_panel no longer at {Path}, but keeping cached value",
                IndiControlPanelPath);
        }
    }

    /// <summary>
    /// Launches a fresh instance of <c>indi_control_panel</c> as a child
    /// window inside the existing xpra display, so it shows up in the
    /// same HTML5 client iframe the user already uses for the PHD2 GUI.
    /// No-op (returns false + sets LastError) when xpra isn't running,
    /// when the binary isn't installed, or when the OS isn't Linux.
    /// </summary>
    public async Task<bool> LaunchIndiControlPanelAsync(CancellationToken ct = default) {
        if (!IsSupportedOs)        { LastError = "OS not supported"; return false; }
        if (!XpraInstalled)        { LastError = "xpra not installed"; return false; }
        if (!IndiControlPanelInstalled) {
            LastError = "indi_control_panel not installed (apt install indi-bin)";
            return false;
        }
        if (!await ProbeHealthAsync(ct)) {
            LastError = "xpra session not running, start the embedded GUI first";
            return false;
        }
        var args = $"control :{DisplayNumber} start-child indi_control_panel";
        _logger.LogInformation("Launching indi_control_panel inside xpra session :{Display}",
            DisplayNumber);
        var res = await RunCommandAsync("xpra", args, ct, timeoutMs: 10000);
        if (res.exitCode != 0) {
            LastError = $"xpra start-child indi_control_panel exited {res.exitCode}: {res.stderr}";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }
        LastError = null;
        return true;
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
        // xpra forks into the background. Gate readiness on PHD2 actually
        // RUNNING, not just on the TCP port answering.
        //
        // The bind-tcp port comes up the instant xpra's server starts —
        // well before the `--start=phd2` child has launched and mapped its
        // window. Returning on the port alone is why the GUI tab sometimes
        // shows the bare xpra desktop (just the wallpaper), and the HTML
        // client occasionally hangs connecting to a server that isn't fully
        // serving yet. Worse, when `--start=phd2` silently fails (a startup
        // race on a slow Pi), the port is up forever with no PHD2 at all.
        //
        // So: poll for port + PHD2 process. If the port has been up for a
        // few seconds but PHD2 never appeared, recover once by issuing a
        // start-child — the same thing the UI's "Relaunch PHD2" button does.
        bool portUp = false;
        bool recovered = false;
        for (int i = 0; i < 40; i++) {   // ~20s budget (PHD2 cold-start on a Pi is slow)
            try { await Task.Delay(500, ct); } catch (TaskCanceledException) { return false; }
            await ProbeHealthAsync(ct);   // refreshes SessionRunning + Phd2Running
            if (SessionRunning) portUp = true;
            if (SessionRunning && Phd2Running) {
                if (!await WaitForPhd2Async(0, 4000, ct)) {
                    _logger.LogWarning("xpra :{Display} up but PHD2 exited seconds after starting; launching it directly",
                        DisplayNumber);
                    await LaunchPhd2DirectlyAsync(ct);
                    return true;
                }
                _logger.LogInformation("xpra :{Display} ready (port {Port} + PHD2 up)",
                    DisplayNumber, BindPort);
                LastError = null;
                return true;
            }
            // Port up but no PHD2 after ~5s -> the --start=phd2 child didn't
            // take. Kick a start-child once to recover the empty-desktop case.
            if (portUp && !Phd2Running && !recovered && i >= 10) {
                recovered = true;
                _logger.LogWarning(
                    "xpra :{Display} up but PHD2 absent after ~5s; issuing start-child phd2 recovery",
                    DisplayNumber);
                try {
                    await RunCommandAsync("xpra", $"control :{DisplayNumber} start-child phd2",
                        ct, timeoutMs: 10000);
                } catch { /* best effort; keep polling */ }
            }
        }
        if (portUp) {
            // Session exists so the UI can still attach. Try PHD2 directly on
            // the display, which either brings it up or says why it will not.
            _logger.LogWarning("xpra :{Display} up but PHD2 never appeared; launching it directly", DisplayNumber);
            await LaunchPhd2DirectlyAsync(ct);
            return true;
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

    /// <summary>TCP probe, returns true if something is listening on 127.0.0.1:BindPort.</summary>
    private async Task<bool> ProbeHealthAsync(CancellationToken ct) {
        SessionRunning = await NetProbe.TryConnectAsync("127.0.0.1", BindPort, 500, ct);
        LastHealthCheckAt = DateTime.UtcNow;
        // Refresh Phd2Running alongside the xpra probe. Cheap, single
        // pgrep call, no need to be conditional on SessionRunning.
        // FIELD-5: widened from `pgrep -x phd2.bin` / `pgrep -x phd2`
        // to a regex over the full command line (`-f`). The exact-
        // match form missed AppImage builds (process name extracts to
        // a random tmpdir like `/tmp/.mount_phd2X/phd2`), python-
        // launched builds (`python3 phd2.py`), and SnapStore (which
        // wraps as `snap run phd2`). The `\bphd2(\.bin)?\b` anchor
        // keeps unrelated processes that happen to contain "phd2" in
        // a path (e.g. `nano /home/me/phd2-notes.txt`) from
        // accidentally matching. The banner in the UI also ORs this
        // with phd2.connected so a missed detection no longer
        // silently lies to the operator anyway.
        // `-a` lists the command line so xpra's own processes, whose
        // arguments name phd2 (`start :100 --start=phd2`, `control :100
        // start-child phd2`), can be dropped from the match.
        try {
            var res = await RunCommandAsync("pgrep", "-af \"\\bphd2(\\.bin)?\\b\"", ct, timeoutMs: 2000);
            Phd2Running = res.exitCode == 0 && res.stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(l => !l.Contains("xpra", StringComparison.Ordinal));
        } catch {
            Phd2Running = false;
        }
        return SessionRunning;
    }

    /// <summary>
    /// True once a phd2 process has been seen and is still there
    /// <paramref name="holdMs"/> later. A process that shows up and is gone a
    /// second later is a PHD2 that died on startup, not a running one.
    /// </summary>
    private async Task<bool> WaitForPhd2Async(int appearMs, int holdMs, CancellationToken ct) {
        var seen = Phd2Running && appearMs == 0;
        for (int t = 0; t < appearMs && !seen; t += 500) {
            try { await Task.Delay(500, ct); } catch (TaskCanceledException) { return false; }
            await ProbeHealthAsync(ct);
            seen = Phd2Running;
        }
        if (!seen) return false;
        for (int t = 0; t < holdMs; t += 1000) {
            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { return false; }
            await ProbeHealthAsync(ct);
            if (!Phd2Running) return false;
        }
        return true;
    }

    /// <summary>
    /// Runs when xpra's start-child produced no lasting phd2 process: launch
    /// PHD2 directly on the session display with its output captured. If it
    /// stays up it becomes the session's PHD2; if it exits, its exit code and
    /// last output lines (plus what xpra's log says about the child) go to
    /// <see cref="LastError"/> so the panel shows why.
    /// </summary>
    private async Task<bool> LaunchPhd2DirectlyAsync(CancellationToken ct) {
        var which = await RunCommandAsync("which", "phd2", ct, timeoutMs: 3000);
        if (which.exitCode != 0 || string.IsNullOrWhiteSpace(which.stdout)) {
            LastError = "PHD2 is not installed on the host. Install it with: sudo apt install phd2";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }
        var psi = new ProcessStartInfo {
            FileName = "phd2",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["DISPLAY"] = $":{DisplayNumber}";
        var tail = new Queue<string>();
        void Keep(string? line) {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail) { tail.Enqueue(line.Trim()); while (tail.Count > 4) tail.Dequeue(); }
        }
        Process p;
        try {
            p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => Keep(e.Data);
            p.ErrorDataReceived += (_, e) => Keep(e.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        } catch (Exception ex) {
            LastError = $"PHD2 could not be launched: {ex.Message}";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }
        _logger.LogInformation("PHD2 launched directly on :{Display} (pid {Pid}) after xpra start-child left no process",
            DisplayNumber, p.Id);
        var exit = p.WaitForExitAsync(ct);
        try { await Task.WhenAny(exit, Task.Delay(6000, ct)); } catch (TaskCanceledException) { return false; }
        if (!p.HasExited) {
            await ProbeHealthAsync(ct);
            LastError = null;
            _logger.LogInformation("PHD2 is up on :{Display} (direct launch, pid {Pid})", DisplayNumber, p.Id);
            return true;
        }
        string lines;
        lock (tail) lines = string.Join(" | ", tail);
        var xpraSays = await XpraLogSaysAboutPhd2Async(ct);
        LastError = $"PHD2 exits right after launch (exit code {p.ExitCode})."
            + (lines.Length > 0 ? $" Output: {lines}" : " It printed nothing.")
            + (xpraSays.Length > 0 ? $" xpra log: {xpraSays}" : "");
        _logger.LogWarning("{Error}", LastError);
        return false;
    }

    /// <summary>Last lines of the xpra session log that mention phd2, joined; empty when none.</summary>
    private async Task<string> XpraLogSaysAboutPhd2Async(CancellationToken ct) {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        var candidates = new[] {
            runtimeDir is null ? null : Path.Combine(runtimeDir, "xpra", $":{DisplayNumber}.log"),
            Path.Combine(home, ".xpra", $":{DisplayNumber}.log")
        };
        foreach (var f in candidates) {
            if (f is null || !File.Exists(f)) continue;
            try {
                var res = await RunCommandAsync("tail", $"-n 60 \"{f}\"", ct, timeoutMs: 3000);
                var hits = res.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.Contains("phd2", StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Trim()).TakeLast(3).ToList();
                if (hits.Count > 0) return string.Join(" | ", hits);
            } catch { }
        }
        return "";
    }

    /// <summary>
    /// Spawn a PHD2 process inside the already-running xpra session.
    /// Used by the UI's "Relaunch PHD2" button when the user sees the
    /// xpra session is alive but PHD2 is not (xpra's <c>--start=phd2</c>
    /// failed silently or PHD2 crashed mid-session). Does NOT restart
    /// xpra, so any other windows the user opened in the session stay.
    /// </summary>
    public async Task<bool> RelaunchPhd2Async(CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (!XpraInstalled) { LastError = "xpra not installed"; return false; }
        if (!await ProbeHealthAsync(ct)) {
            // No xpra session to talk to. Caller should start one first.
            LastError = "xpra session not running, call /start before /relaunch-phd2";
            return false;
        }
        // First, kill any orphan phd2 still hanging around. Otherwise
        // xpra start-child will spawn a second one and we get two phd2
        // processes both fighting for port 4400.
        try { await RunCommandAsync("pkill", "-x phd2.bin", ct, timeoutMs: 3000); } catch { }
        try { await RunCommandAsync("pkill", "-x phd2", ct, timeoutMs: 3000); } catch { }
        try { await Task.Delay(800, ct); } catch (TaskCanceledException) { return false; }

        var args = $"control :{DisplayNumber} start-child phd2";
        _logger.LogInformation("Relaunching PHD2 inside xpra session :{Display}", DisplayNumber);
        var res = await RunCommandAsync("xpra", args, ct, timeoutMs: 10000);
        if (res.exitCode != 0) {
            LastError = $"xpra start-child phd2 exited {res.exitCode}: {res.stderr}";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }
        // Up to 15 s for a phd2 process to appear, then 4 s more to be sure
        // it stays: a process that is gone a second later is PHD2 dying on
        // startup, and the direct launch below captures why.
        if (await WaitForPhd2Async(15000, 4000, ct)) {
            LastError = null;
            _logger.LogInformation("PHD2 relaunch confirmed (process up for 4 s)");
            return true;
        }
        _logger.LogWarning("xpra start-child phd2 left no lasting process; launching PHD2 directly");
        return await LaunchPhd2DirectlyAsync(ct);
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