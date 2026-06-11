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

namespace NINA.Polaris.Services;

/// <summary>
/// Power / lifecycle control for the host: restart the Polaris process and
/// reboot the whole device, surfaced as one-click buttons in Settings.
///
/// Restart strategy is platform-aware:
///   - Under systemd (the SBC / .deb deployment): <c>systemctl restart
///     polaris.service</c>. The .deb ships a polkit rule
///     (50-polaris-power.rules) so the polaris user may manage its own unit
///     and reboot without a password. If that call is denied we fall back to
///     <c>Environment.Exit(1)</c> so the unit's <c>Restart=on-failure</c>
///     brings the process back a few seconds later (no privilege needed).
///   - Standalone (dev run, Windows console): re-exec the same executable
///     through a tiny detached shell that waits ~2s for the listening socket
///     to free up, then starts a fresh instance, and exit the current one.
///
/// Reboot uses <c>systemctl reboot</c> on Linux (logind, polkit-gated) and
/// <c>shutdown /r /t 0</c> on Windows.
///
/// Windows auto-start (boot without a login session) is offered as an opt-in
/// that registers a Scheduled Task running at system startup as SYSTEM
/// (RunLevel Highest). Creating it needs admin rights; we report a clear
/// error if Polaris isn't elevated. On Linux auto-start is already handled by
/// the systemd unit the installer enables, so we only report its state.
///
/// Every action here is user-initiated from the authenticated UI (all
/// /api/* routes pass through AuthMiddleware); nothing reboots on its own.
/// </summary>
public class PowerService {
    private readonly ILogger<PowerService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>Scheduled Task name used for Windows boot auto-start.</summary>
    private const string WinTaskName = "NINA Polaris";
    /// <summary>The systemd unit name the .deb installs.</summary>
    private const string SystemdUnit = "polaris.service";

    public PowerService(ILogger<PowerService> logger, IHostApplicationLifetime lifetime) {
        _logger = logger;
        _lifetime = lifetime;
    }

    public bool IsLinux => OperatingSystem.IsLinux();
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>systemd exports INVOCATION_ID into every unit it spawns; the
    /// cleanest no-extra-dependency way to know we're running as a unit.</summary>
    public bool UnderSystemd =>
        IsLinux && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"));

    public bool CanReboot => IsLinux || IsWindows;
    public bool CanShutdown => IsLinux || IsWindows;

    public PowerInfo GetInfo() {
        bool autoSupported = false, autoEnabled = false;
        if (IsWindows) {
            autoSupported = true;
            autoEnabled = WindowsTaskExists();
        } else if (IsLinux) {
            // Auto-start is the systemd unit being enabled. We can only
            // toggle it when we're actually running under systemd.
            autoSupported = UnderSystemd;
            autoEnabled = SystemdEnabled();
        }
        return new PowerInfo(
            Platform: IsWindows ? "windows" : IsLinux ? "linux" : "other",
            UnderSystemd: UnderSystemd,
            CanRestartApp: true,
            CanReboot: CanReboot,
            CanShutdown: CanShutdown,
            AutoStartSupported: autoSupported,
            AutoStartEnabled: autoEnabled);
    }

    // ---- Restart -------------------------------------------------------
    /// <summary>Schedule a process restart shortly after returning, so the
    /// HTTP response flushes before we tear the process down.</summary>
    public PowerActionResult ScheduleRestart() {
        _ = Task.Run(async () => {
            await Task.Delay(700);
            try { await DoRestartAsync(); }
            catch (Exception ex) {
                _logger.LogError(ex, "Restart failed; forcing exit");
                Environment.Exit(1);
            }
        });
        return PowerActionResult.Okay(UnderSystemd
            ? "Restarting the Polaris service…"
            : "Restarting Polaris…");
    }

    private async Task DoRestartAsync() {
        if (UnderSystemd) {
            // On success systemctl SIGTERMs us before this returns; if we
            // get here the call failed (most likely polkit denied), so fall
            // back to a non-zero exit which Restart=on-failure handles.
            var r = await RunAsync("systemctl", $"restart {SystemdUnit}",
                ignoreExit: true, timeoutMs: 8_000);
            _logger.LogWarning(
                "systemctl restart returned {Code} (stderr: {Err}); "
                + "falling back to exit so systemd restarts us",
                r.ExitCode, r.Stderr);
            Environment.Exit(1);
            return;
        }
        // Standalone: re-exec a fresh copy, then bow out.
        ReExecSelf();
        await Task.Delay(300);
        _lifetime.StopApplication();
        Environment.Exit(0);
    }

    // ---- Reboot --------------------------------------------------------
    public PowerActionResult ScheduleReboot() {
        if (!CanReboot)
            return PowerActionResult.Fail("Device reboot is not supported on this platform.", 501);
        _ = Task.Run(async () => {
            await Task.Delay(700);
            try { await DoRebootAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Reboot failed"); }
        });
        return PowerActionResult.Okay("Rebooting the device…");
    }

    private async Task DoRebootAsync() {
        if (IsLinux) {
            var r = await RunAsync("systemctl", "reboot", ignoreExit: true, timeoutMs: 8_000);
            if (r.ExitCode != 0)
                _logger.LogWarning("systemctl reboot returned {Code}: {Err}", r.ExitCode, r.Stderr);
        } else if (IsWindows) {
            await RunAsync("shutdown", "/r /t 0", ignoreExit: true, timeoutMs: 8_000);
        }
    }

    // ---- Shutdown ------------------------------------------------------
    public PowerActionResult ScheduleShutdown() {
        if (!CanShutdown)
            return PowerActionResult.Fail("Device shutdown is not supported on this platform.", 501);
        _ = Task.Run(async () => {
            await Task.Delay(700);
            try { await DoShutdownAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Shutdown failed"); }
        });
        return PowerActionResult.Okay("Shutting the device down…");
    }

    private async Task DoShutdownAsync() {
        if (IsLinux) {
            var r = await RunAsync("systemctl", "poweroff", ignoreExit: true, timeoutMs: 8_000);
            if (r.ExitCode != 0)
                _logger.LogWarning("systemctl poweroff returned {Code}: {Err}", r.ExitCode, r.Stderr);
        } else if (IsWindows) {
            await RunAsync("shutdown", "/s /t 0", ignoreExit: true, timeoutMs: 8_000);
        }
    }

    // ---- Windows auto-start --------------------------------------------
    public PowerActionResult SetAutoStart(bool enable) {
        if (!IsWindows)
            return PowerActionResult.Fail(
                "Auto-start can only be toggled here on Windows. On Linux it is "
                + "managed by the systemd unit the installer enables.", 501);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return PowerActionResult.Fail("Could not resolve the Polaris executable path.");
        try {
            ProcessResult r;
            if (enable) {
                // /SC ONSTART runs at boot before any user logs in; /RU SYSTEM
                // gives it the SYSTEM account (no stored password, no session);
                // /RL HIGHEST = highest privileges; /F overwrites any existing.
                r = RunSync("schtasks",
                    $"/Create /TN \"{WinTaskName}\" /TR \"\\\"{exe}\\\"\" "
                    + "/SC ONSTART /RU SYSTEM /RL HIGHEST /F");
                if (r.ExitCode != 0)
                    return PowerActionResult.Fail(
                        "Could not create the startup task (Polaris likely needs to "
                        + "run as Administrator to register a SYSTEM task). Detail: "
                        + Trim(r.Stderr, r.Stdout));
                return PowerActionResult.Okay(
                    "Auto-start enabled. Polaris will launch at boot as SYSTEM, "
                    + "before any login.");
            } else {
                r = RunSync("schtasks", $"/Delete /TN \"{WinTaskName}\" /F");
                if (r.ExitCode != 0)
                    return PowerActionResult.Fail(
                        "Could not remove the startup task: " + Trim(r.Stderr, r.Stdout));
                return PowerActionResult.Okay("Auto-start disabled.");
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "schtasks failed");
            return PowerActionResult.Fail("schtasks failed: " + ex.Message);
        }
    }

    private bool WindowsTaskExists() {
        try { return RunSync("schtasks", $"/Query /TN \"{WinTaskName}\"").ExitCode == 0; }
        catch { return false; }
    }

    private bool SystemdEnabled() {
        try { return RunSync("systemctl", $"is-enabled {SystemdUnit}").Stdout.Trim() == "enabled"; }
        catch { return false; }
    }

    // ---- Re-exec helper ------------------------------------------------
    private void ReExecSelf() {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) {
            _logger.LogWarning("Cannot resolve executable path for re-exec; exiting only");
            return;
        }
        var wd = AppContext.BaseDirectory;
        // Forward the original CLI args (skip element 0 = the exe itself).
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        try {
            if (IsWindows) {
                var argStr = string.Join(" ", args.Select(QuoteWin));
                // Detached cmd: wait for the socket to free, then relaunch.
                var psi = new ProcessStartInfo {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\" {argStr}",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WorkingDirectory = wd
                };
                Process.Start(psi);
            } else {
                var argStr = string.Join(" ", args.Select(QuoteSh));
                var psi = new ProcessStartInfo {
                    FileName = "/bin/sh",
                    Arguments = $"-c \"sleep 2; exec '{exe}' {argStr}\"",
                    UseShellExecute = false,
                    WorkingDirectory = wd
                };
                Process.Start(psi);
            }
            _logger.LogInformation("Spawned replacement Polaris instance (re-exec)");
        } catch (Exception ex) {
            _logger.LogError(ex, "Re-exec spawn failed; the process will exit without restarting");
        }
    }

    private static string QuoteWin(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
    private static string QuoteSh(string a) => "'" + a.Replace("'", "'\\''") + "'";
    private static string Trim(string? a, string? b) {
        var s = (a ?? "").Trim();
        if (string.IsNullOrEmpty(s)) s = (b ?? "").Trim();
        return string.IsNullOrEmpty(s) ? "(no output)" : s;
    }

    // ---- Process helpers (mirrors ClockSyncService) --------------------
    private async Task<ProcessResult> RunAsync(string fileName, string args,
            bool ignoreExit = false, int timeoutMs = 5_000) {
        var psi = new ProcessStartInfo {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        using var cts = new CancellationTokenSource(timeoutMs);
        try {
            await p.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { p.Kill(); } catch { }
            throw;
        }
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        if (!ignoreExit && p.ExitCode != 0)
            _logger.LogDebug("{Cmd} {Args} -> exit {Code}, stderr: {Err}",
                fileName, args, p.ExitCode, stderr);
        return new ProcessResult(p.ExitCode, stdout, stderr);
    }

    private ProcessResult RunSync(string fileName, string args, int timeoutMs = 5_000) {
        var psi = new ProcessStartInfo {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) {
            try { p.Kill(); } catch { }
            return new ProcessResult(-1, stdout, "timed out");
        }
        return new ProcessResult(p.ExitCode, stdout, stderr);
    }

    private record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

public record PowerInfo(
    string Platform,
    bool UnderSystemd,
    bool CanRestartApp,
    bool CanReboot,
    bool CanShutdown,
    bool AutoStartSupported,
    bool AutoStartEnabled);

public record PowerActionResult(bool Ok, string Message, int StatusCode) {
    public static PowerActionResult Okay(string message) => new(true, message, 200);
    public static PowerActionResult Fail(string message, int statusCode = 500) =>
        new(false, message, statusCode);
}
