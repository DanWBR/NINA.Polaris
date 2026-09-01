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
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NINA.Polaris.Services;

/// <summary>
/// Manages a long-lived indi-web (a.k.a. indiwebmanager,
/// https://github.com/knro/indiwebmanager) process bound to
/// 127.0.0.1:8624. The Polaris frontend embeds it via an iframe at
/// <c>/indi-web/</c> (reverse-proxied in Program.cs) so users can
/// start/stop/enable INDI drivers from the same browser they use
/// for capture, without ssh-ing into the host to edit indiserver
/// command lines.
///
/// Why a separate service even though indi-web is "just a Python
/// webapp": it doesn't daemonize itself (no <c>--daemon</c> flag),
/// so we have to own the process lifecycle. Plus we want a single
/// place to gate the "auto-start on Polaris boot" toggle, the
/// install detection (so the UI can show a "pip install
/// indiwebmanager" banner when missing), and the TCP health probe
/// (so the iframe shows a clear "starting / running / down"
/// indicator instead of a generic browser-side fetch error).
///
/// Linux + macOS only. Windows technically can run indi-web via pip
/// + WSL, but indiserver itself is Linux/macOS so embedding the
/// driver-management UI without a working server is misleading. On
/// Windows the service short-circuits to <c>Installed = false</c>
/// and the UI surfaces "not supported on this OS".
///
/// Coexistence with <see cref="Simulator.SimulatorService"/>: both
/// want to own the indiserver process. When indi-web is the active
/// owner (running) the SimulatorService MUST route its start/stop
/// driver commands through indi-web's REST API instead of the
/// indiserver FIFO it normally talks to — otherwise the two will
/// race on the same FIFO and one of them loses. INDI-WEB-4 wires
/// that delegation; until then the user picks one or the other.
/// </summary>
/// <summary>An INDI driver installed on the host, as indi-web reports it.
/// <paramref name="Label"/> is the identifier every indi-web call takes.</summary>
public sealed record IndiInstalledDriver(string Label, string? Binary, string? Family);

public class IndiWebManagerService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly ILogger<IndiWebManagerService> _logger;
    private Process? _process;

    /// <summary>True when <c>indi-web</c> is on PATH (or at the
    /// path explicitly configured via IndiWeb:ExecutablePath).</summary>
    public bool Installed { get; private set; }
    public string? Version { get; private set; }
    public string? ExecutablePath { get; private set; }

    /// <summary>True when something is listening on the bound port
    /// — refreshed by the 15 s health-probe loop.</summary>
    public bool Running { get; private set; }
    public int BindPort { get; }
    public string BindAddress { get; }
    public DateTime? LastHealthCheckAt { get; private set; }
    public string? LastError { get; private set; }

    public string OperatingSystem => Environment.OSVersion.Platform.ToString();
    public bool IsSupportedOs =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>Human-readable reason indi-web is unavailable on
    /// this host, or null when it should work. The UI shows this in
    /// the Settings + RIGS banner so users on Windows don't see a
    /// stuck "click to start" button.</summary>
    public string? UnsupportedReason {
        get {
            if (!IsSupportedOs) {
                return $"INDI Web Manager requires Linux or macOS (indiserver is not packaged for Windows). " +
                       $"This host is {RuntimeInformation.OSDescription}.";
            }
            return null;
        }
    }

    public IndiWebManagerService(IConfiguration config,
                                  ILogger<IndiWebManagerService> logger) {
        _config = config;
        _logger = logger;
        BindPort = _config.GetValue("IndiWeb:Port", 8624);
        // Always loopback by default — indi-web has no auth, and the
        // user reaches it via Polaris's reverse-proxy (which IS
        // gated by the Relay's token if enabled). Letting it bind on
        // 0.0.0.0 would re-expose driver control to the LAN.
        BindAddress = _config.GetValue("IndiWeb:BindAddress", "127.0.0.1")!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!IsSupportedOs) {
            _logger.LogInformation("IndiWebManagerService: OS {Os} not supported (Linux/macOS only), service idle",
                RuntimeInformation.OSDescription);
            LastError = UnsupportedReason;
            return;
        }

        await DetectAsync(stoppingToken);
        if (!Installed) {
            _logger.LogInformation(
                "IndiWebManagerService: indi-web not found, install via " +
                "'pip install indiweb' (or 'pipenv install indiweb' in a venv) " +
                "to enable embedded INDI driver management");
        }

        // 3 s stagger after Polaris boot so PHD2 / simulator services
        // get out of the way before indi-web prints to stdout in the
        // log — keeps the startup banner readable.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (TaskCanceledException) { return; }

        var autoStart = _config.GetValue("IndiWeb:AutoStart", false);
        if (Installed && autoStart) {
            _logger.LogInformation("IndiWebManagerService: AutoStart enabled, launching indi-web");
            try { await StartAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-start of indi-web failed"); }
        }

        // Periodic health check (15 s) keeps Running fresh for the
        // UI status pill. Also catches the case where the user (or
        // an OOM killer) killed the indi-web process out of band —
        // we'll notice within 15 s and surface it.
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (Installed) await ProbeHealthAsync(stoppingToken);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "indi-web health probe failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }

        // Deliberately no StopAsync here: indi-web outlives Polaris.
        //
        // CanopusServerService does stop its child processes on the way out, so
        // the difference looks like an oversight. It is not. Restarting Polaris
        // (an update, a crash, a systemctl restart) should not bounce the INDI
        // drivers underneath it: the mount keeps tracking, the cooler keeps its
        // setpoint, and nothing goes through a USB re-enumeration for what is a
        // few seconds of downtime in the layer above. The operator stops
        // indi-web explicitly from the INDI panel when they actually mean it.
    }

    private async Task DetectAsync(CancellationToken ct) {
        try {
            // Explicit path wins over PATH lookup so an operator
            // running indi-web from a venv can point us at the
            // absolute binary. Otherwise just `which indi-web`.
            var explicitPath = _config.GetValue<string?>("IndiWeb:ExecutablePath", null);
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) {
                ExecutablePath = explicitPath;
            } else {
                var which = await RunCommandAsync("which", "indi-web", ct);
                if (string.IsNullOrWhiteSpace(which.stdout)) {
                    Installed = false;
                    return;
                }
                ExecutablePath = which.stdout.Trim();
            }

            // indi-web has a `--version` flag in current builds, but
            // older releases don't, so fall back to "pip show
            // indiweb" if the binary doesn't print one.
            var ver = await RunCommandAsync(ExecutablePath, "--version", ct);
            Version = (ver.stdout + " " + ver.stderr).Trim();
            if (string.IsNullOrEmpty(Version) || ver.exitCode != 0) {
                var pip = await RunCommandAsync("pip", "show indiweb", ct);
                var line = pip.stdout
                    .Split('\n')
                    .FirstOrDefault(l => l.StartsWith("Version:",
                        StringComparison.OrdinalIgnoreCase));
                Version = line?["Version:".Length..].Trim() ?? "unknown";
            }
            Installed = true;
            _logger.LogInformation("IndiWebManagerService: detected indi-web {Ver} at {Path}",
                Version, ExecutablePath);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi-web detection failed");
            Installed = false;
        }
    }

    // Hides BackgroundService.StartAsync on purpose (like CanopusServerService):
    // the hosted lifecycle still runs via the base StartAsync -> ExecuteAsync
    // through IHostedService; this overload is what the endpoints call to launch
    // indi-web on demand and get a success bool back.
    public new async Task<bool> StartAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (!Installed) { LastError = "indi-web not installed"; return false; }
        if (await ProbeHealthAsync(ct)) {
            _logger.LogDebug("StartAsync: already running");
            return true;
        }

        // Unlike xpra, indi-web runs in the foreground; we own the
        // process and have to keep the handle around. Redirect
        // stdout/stderr to /dev/null-ish (no UseShellExecute, no
        // RedirectStandardOutput → child writes to our terminal,
        // visible in the systemd journal when Polaris runs as a
        // unit). Working dir = home so any default conf files land
        // somewhere predictable.
        var args = $"--port {BindPort} --host {BindAddress}";
        _logger.LogInformation("Spawning indi-web: {Path} {Args}", ExecutablePath, args);
        try {
            var psi = new ProcessStartInfo {
                FileName = ExecutablePath!,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
            };
            _process = Process.Start(psi);
            if (_process == null) {
                LastError = "Process.Start returned null";
                return false;
            }
            _logger.LogInformation("indi-web pid {Pid}", _process.Id);
        } catch (Exception ex) {
            LastError = $"Failed to launch indi-web: {ex.Message}";
            _logger.LogWarning(ex, "indi-web launch failed");
            return false;
        }

        // Wait up to 20 s for the HTTP server to come up. Bottle (the
        // framework indi-web uses) prints its "running on..." banner
        // after maybe a second of startup; allow generous slack for
        // slow Pi hardware.
        for (int i = 0; i < 40; i++) {
            try { await Task.Delay(500, ct); } catch (TaskCanceledException) { return false; }
            if (_process?.HasExited == true) {
                LastError = $"indi-web exited prematurely (code {_process.ExitCode})";
                _logger.LogWarning("{Error}", LastError);
                return false;
            }
            if (await ProbeHealthAsync(ct)) {
                _logger.LogInformation("indi-web listening on {Host}:{Port}",
                    BindAddress, BindPort);
                LastError = null;
                return true;
            }
        }

        LastError = "indi-web started but TCP probe never responded";
        return false;
    }

    // Hides BackgroundService.StopAsync, same reasoning as StartAsync above.
    public new Task<bool> StopAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) return Task.FromResult(false);
        if (_process == null || _process.HasExited) {
            Running = false;
            return Task.FromResult(true);
        }
        try {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
            Running = false;
            _process = null;
            LastError = null;
            return Task.FromResult(true);
        } catch (Exception ex) {
            LastError = $"Failed to stop indi-web: {ex.Message}";
            _logger.LogWarning(ex, "indi-web stop failed");
            return Task.FromResult(false);
        }
    }

    public async Task<bool> RestartAsync(CancellationToken ct = default) {
        await StopAsync(ct);
        try { await Task.Delay(1500, ct); } catch (TaskCanceledException) { return false; }
        return await StartAsync(ct);
    }

    // ── Per-driver control via the indi-web REST API ─────────────────────
    // indi-web (knro/indiwebmanager) exposes /api/drivers/{start,stop,restart}
    // /<label> and /api/server/drivers. Restarting a single driver bounces
    // just that driver process on the indiserver without dropping the others,
    // which is exactly what a wedged driver (dropped BLOB) needs — far less
    // disruptive than RestartAsync (which kills the whole indi-web) or
    // reconnecting the device (which does not fix a stuck driver process).

    private HttpClient? _http;
    private HttpClient Http => _http ??= new HttpClient {
        BaseAddress = new Uri($"http://{BindAddress}:{BindPort}"),
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>The driver <c>label</c>s indi-web currently has running
    /// (from <c>GET /api/server/drivers</c>). Empty when indi-web is down or
    /// no server is running. Labels are what the start/stop/restart calls
    /// take.</summary>
    public async Task<List<string>> GetRunningDriverLabelsAsync(CancellationToken ct = default) {
        if (!IsSupportedOs || !Running) return [];
        try {
            var drivers = await Http.GetFromJsonAsync<List<IndiWebDriver>>(
                "/api/server/drivers", ct);
            return drivers?.Where(d => !string.IsNullOrWhiteSpace(d.Label))
                          .Select(d => d.Label!).ToList() ?? [];
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi-web GET /api/server/drivers failed");
            return [];
        }
    }

    /// <summary>Every driver INSTALLED on this host (<c>GET /api/drivers</c>) --
    /// around 420 entries on a full indi + indi-3rdparty box. Deliberately
    /// distinct from <see cref="GetRunningDriverLabelsAsync"/>, which only
    /// reports the drivers of the ACTIVE profile: the profile assistant has to
    /// check its proposals against what is installed, and using the running set
    /// there would reject every driver the user has not already configured --
    /// i.e. exactly the ones it exists to suggest.</summary>
    public async Task<List<IndiInstalledDriver>> GetInstalledDriversAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) return [];
        try {
            var drivers = await Http.GetFromJsonAsync<List<IndiWebDriver>>("/api/drivers", ct);
            return drivers?.Where(d => !string.IsNullOrWhiteSpace(d.Label))
                          .Select(d => new IndiInstalledDriver(d.Label!, d.Binary, d.Family))
                          .ToList() ?? [];
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi-web GET /api/drivers failed");
            return [];
        }
    }

    /// <summary>Profile names indi-web knows about.</summary>
    public async Task<List<string>> GetProfileNamesAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) return [];
        try {
            var profiles = await Http.GetFromJsonAsync<List<IndiWebProfile>>("/api/profiles", ct);
            return profiles?.Where(p => !string.IsNullOrWhiteSpace(p.Name))
                           .Select(p => p.Name!).ToList() ?? [];
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi-web GET /api/profiles failed");
            return [];
        }
    }

    /// <summary>The driver labels a given profile carries
    /// (<c>GET /api/profiles/{name}/labels</c>, verified against a live
    /// indi-web: it answers <c>[{"label": "ZWO CCD"}, ...]</c>).
    ///
    /// <para>INDIAUTO uses this to tell a configured host from a fresh one. The
    /// profile NAMES alone cannot: indi-web ships a profile out of the box, so
    /// "a profile exists" is true on a machine nobody has set up yet.</para>
    /// </summary>
    public async Task<List<string>> GetProfileDriverLabelsAsync(string name,
                                                                CancellationToken ct = default) {
        if (!IsSupportedOs || string.IsNullOrWhiteSpace(name)) return [];
        try {
            var drivers = await Http.GetFromJsonAsync<List<IndiWebDriver>>(
                $"/api/profiles/{Uri.EscapeDataString(name)}/labels", ct);
            return drivers?.Where(d => !string.IsNullOrWhiteSpace(d.Label))
                          .Select(d => d.Label!).ToList() ?? [];
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi-web GET /api/profiles/{Name}/labels failed", name);
            return [];
        }
    }

    /// <summary>Create an indi-web profile and set its driver list, in the two
    /// calls indi-web requires. The body shape for the driver list
    /// (<c>[{"label": "..."}]</c>) was verified against a live indi-web rather
    /// than inferred -- its OpenAPI document declares no request schema.
    ///
    /// <para>This MUTATES the user's INDI setup, so nothing calls it
    /// automatically: the endpoint above it is only reachable from an explicit
    /// operator confirmation in the UI.</para></summary>
    public async Task<bool> CreateProfileAsync(string name, IEnumerable<string> driverLabels,
                                               CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (string.IsNullOrWhiteSpace(name)) { LastError = "profile name required"; return false; }
        if (!Running && !await ProbeHealthAsync(ct)) {
            LastError = "indi-web is not running";
            return false;
        }
        var escaped = Uri.EscapeDataString(name);
        try {
            using (var created = await Http.PostAsync($"/api/profiles/{escaped}", content: null, ct)) {
                if (!created.IsSuccessStatusCode) {
                    LastError = $"indi-web could not create profile '{name}' (HTTP {(int)created.StatusCode})";
                    _logger.LogWarning("{Error}", LastError);
                    return false;
                }
            }
            var payload = driverLabels
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => new Dictionary<string, string> { ["label"] = l })
                .ToList();
            using (var set = await Http.PostAsJsonAsync($"/api/profiles/{escaped}/drivers", payload, ct)) {
                if (!set.IsSuccessStatusCode) {
                    LastError = $"indi-web rejected the driver list for '{name}' (HTTP {(int)set.StatusCode})";
                    _logger.LogWarning("{Error}", LastError);
                    return false;
                }
            }

            // A freshly created profile comes back with autostart=0 and
            // autoconnect=0 (verified against a live indi-web), which means the
            // user would create a profile here and then still have to start the
            // server and connect every device by hand -- most of the work the
            // assistant exists to remove. Set both, LAST, so neither the create
            // nor the driver-list call can clobber them.
            var settings = new Dictionary<string, int> {
                ["port"] = 7624, ["autostart"] = 1, ["autoconnect"] = 1,
            };
            using (var upd = await Http.PutAsJsonAsync($"/api/profiles/{escaped}", settings, ct)) {
                if (!upd.IsSuccessStatusCode) {
                    // The profile itself is valid at this point, so this is a
                    // warning rather than a failure: the user just has to start
                    // it by hand.
                    _logger.LogWarning(
                        "Profile '{Name}' created but autostart/autoconnect could not be set (HTTP {Code})",
                        name, (int)upd.StatusCode);
                }
            }
            _logger.LogInformation(
                "Created indi-web profile '{Name}' with {Count} driver(s), autostart + autoconnect on",
                name, payload.Count);
            LastError = null;
            return true;
        } catch (Exception ex) {
            LastError = $"indi-web profile creation failed: {ex.Message}";
            _logger.LogWarning(ex, "indi-web profile creation failed for '{Name}'", name);
            return false;
        }
    }

    /// <summary>The profile indiserver is currently running, or null when no
    /// server is up.</summary>
    public async Task<string?> GetActiveProfileAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) return null;
        try {
            var status = await Http.GetFromJsonAsync<List<IndiWebServerStatus>>("/api/server/status", ct);
            var first = status?.FirstOrDefault();
            if (first == null) return null;
            return string.Equals(first.Status, "True", StringComparison.OrdinalIgnoreCase)
                ? first.ActiveProfile : null;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi-web GET /api/server/status failed");
            return null;
        }
    }

    /// <summary>Bring indiserver up on <paramref name="profile"/>, stopping
    /// whatever is running first.
    ///
    /// <para>The stop is explicit rather than relying on start-over-start: this
    /// runs right after the profile assistant writes a profile, and leaving the
    /// PREVIOUS profile's drivers alive would silently keep the old device set
    /// attached, which looks exactly like "the new profile did nothing".</para>
    ///
    /// <para>Switching profiles necessarily drops the drivers the old one had
    /// running, so only call this from a path the operator explicitly
    /// confirmed.</para></summary>
    public async Task<bool> StartServerAsync(string profile, CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (string.IsNullOrWhiteSpace(profile)) { LastError = "profile required"; return false; }
        if (!Running && !await ProbeHealthAsync(ct)) {
            LastError = "indi-web is not running";
            return false;
        }
        try {
            if (await GetActiveProfileAsync(ct) != null) {
                using var stop = await Http.PostAsync("/api/server/stop", content: null, ct);
                if (!stop.IsSuccessStatusCode) {
                    _logger.LogWarning("indi-web server stop returned HTTP {Code}", (int)stop.StatusCode);
                }
                // indiserver needs a moment to release port 7624 before the
                // next bind; without it the start can come back "ok" against a
                // socket that is still closing.
                await Task.Delay(1500, ct);
            }
            var url = $"/api/server/start/{Uri.EscapeDataString(profile)}";
            using var start = await Http.PostAsync(url, content: null, ct);
            if (!start.IsSuccessStatusCode) {
                LastError = $"indi-web could not start profile '{profile}' (HTTP {(int)start.StatusCode})";
                _logger.LogWarning("{Error}", LastError);
                return false;
            }

            // CONFIRM the switch instead of trusting the 200. indiserver needs a
            // few seconds to come up, and indi-web only reports the new
            // active_profile once it has. The caller reloads the indi-web iframe
            // as soon as this returns, and that page renders its profile
            // dropdown FROM active_profile -- so returning early left the panel
            // showing the previously running profile and made a successful
            // switch look like it had been ignored.
            for (int i = 0; i < 12; i++) {
                await Task.Delay(750, ct);
                var active = await GetActiveProfileAsync(ct);
                if (string.Equals(active, profile, StringComparison.OrdinalIgnoreCase)) {
                    _logger.LogInformation("indiserver started on profile '{Profile}'", profile);
                    LastError = null;
                    return true;
                }
            }
            LastError = $"indi-web accepted the start but still reports " +
                        $"'{await GetActiveProfileAsync(ct) ?? "no profile"}' as active";
            _logger.LogWarning("{Error}", LastError);
            return false;
        } catch (Exception ex) {
            LastError = $"indi-web start '{profile}' failed: {ex.Message}";
            _logger.LogWarning(ex, "indi-web start '{Profile}' failed", profile);
            return false;
        }
    }

    /// <summary>Stop whatever indiserver profile is running (no-op payload when
    /// none is). The setup wizard uses this to tear its temporary probe profile
    /// down before creating the final one.</summary>
    public async Task<bool> StopServerAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (!Running && !await ProbeHealthAsync(ct)) {
            LastError = "indi-web is not running";
            return false;
        }
        try {
            using var stop = await Http.PostAsync("/api/server/stop", content: null, ct);
            if (!stop.IsSuccessStatusCode) {
                LastError = $"indi-web server stop returned HTTP {(int)stop.StatusCode}";
                _logger.LogWarning("{Error}", LastError);
                return false;
            }
            // Give indiserver a moment to release port 7624, same reason as
            // the stop inside StartServerAsync.
            await Task.Delay(1500, ct);
            LastError = null;
            return true;
        } catch (Exception ex) {
            LastError = $"indi-web server stop failed: {ex.Message}";
            _logger.LogWarning(ex, "indi-web server stop failed");
            return false;
        }
    }

    /// <summary>Delete a profile (<c>DELETE /api/profiles/{name}</c>). Deleting
    /// the profile whose server is running does not stop the server; call
    /// <see cref="StopServerAsync"/> first when that matters.</summary>
    public async Task<bool> DeleteProfileAsync(string name, CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (string.IsNullOrWhiteSpace(name)) { LastError = "profile name required"; return false; }
        if (!Running && !await ProbeHealthAsync(ct)) {
            LastError = "indi-web is not running";
            return false;
        }
        try {
            using var del = await Http.DeleteAsync($"/api/profiles/{Uri.EscapeDataString(name)}", ct);
            if (!del.IsSuccessStatusCode) {
                LastError = $"indi-web could not delete profile '{name}' (HTTP {(int)del.StatusCode})";
                _logger.LogWarning("{Error}", LastError);
                return false;
            }
            _logger.LogInformation("Deleted indi-web profile '{Name}'", name);
            LastError = null;
            return true;
        } catch (Exception ex) {
            LastError = $"indi-web profile delete failed: {ex.Message}";
            _logger.LogWarning(ex, "indi-web profile delete failed for '{Name}'", name);
            return false;
        }
    }

    public Task<bool> RestartDriverAsync(string label, CancellationToken ct = default)
        => DriverActionAsync("restart", label, ct);
    public Task<bool> StartDriverAsync(string label, CancellationToken ct = default)
        => DriverActionAsync("start", label, ct);
    public Task<bool> StopDriverAsync(string label, CancellationToken ct = default)
        => DriverActionAsync("stop", label, ct);

    private async Task<bool> DriverActionAsync(string action, string label, CancellationToken ct) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (string.IsNullOrWhiteSpace(label)) { LastError = "driver label required"; return false; }
        if (!Running && !await ProbeHealthAsync(ct)) {
            LastError = "indi-web is not running";
            return false;
        }
        // Path segment, not a query value — Uri.EscapeDataString keeps spaces
        // (%20) and other label characters intact for Bottle's route match.
        var url = $"/api/drivers/{action}/{Uri.EscapeDataString(label)}";
        try {
            using var resp = await Http.PostAsync(url, content: null, ct);
            if (resp.IsSuccessStatusCode) {
                _logger.LogInformation("indi-web driver {Action}: '{Label}' OK", action, label);
                LastError = null;
                return true;
            }
            LastError = $"indi-web driver {action} '{label}' returned HTTP {(int)resp.StatusCode}";
            _logger.LogWarning("{Error}", LastError);
            return false;
        } catch (Exception ex) {
            LastError = $"indi-web driver {action} '{label}' failed: {ex.Message}";
            _logger.LogWarning(ex, "indi-web driver {Action} '{Label}' failed", action, label);
            return false;
        }
    }

    private sealed class IndiWebDriver {
        public string? Label { get; set; }
        public string? Version { get; set; }
        public string? Binary { get; set; }
        public string? Family { get; set; }
    }

    private sealed class IndiWebProfile {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int Port { get; set; }
    }

    private sealed class IndiWebServerStatus {
        /// <summary>indi-web reports this as the STRING "True"/"False",
        /// not a JSON boolean.</summary>
        public string? Status { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("active_profile")]
        public string? ActiveProfile { get; set; }
    }

    /// <summary>TCP probe — true if something is listening on
    /// BindAddress:BindPort. Cheap (single connect + 500 ms cap)
    /// so we can call it from the 15 s health loop without making
    /// the log noisy.</summary>
    private async Task<bool> ProbeHealthAsync(CancellationToken ct) {
        Running = await NetProbe.TryConnectAsync(BindAddress, BindPort, 500, ct);
        LastHealthCheckAt = DateTime.UtcNow;
        return Running;
    }

    private static async Task<(int exitCode, string stdout, string stderr)>
        RunCommandAsync(string file, string args, CancellationToken ct,
                        int timeoutMs = 5000) {
        var psi = new ProcessStartInfo {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = new Process { StartInfo = psi };
        try {
            p.Start();
        } catch {
            return (-1, "", "");
        }
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