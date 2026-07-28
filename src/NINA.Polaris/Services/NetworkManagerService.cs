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
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NINA.Polaris.Services;

/// <summary>
/// Manages the host WiFi via NetworkManager / <c>nmcli</c>, so users
/// can switch the Pi between Hotspot ("AP mode") and Station ("client
/// mode") right from the Polaris UI, the same as ASIAIR PRO.
///
/// Linux-only. On Windows / macOS this service short-circuits to
/// <c>IsSupportedOs = false</c> and every mutator returns "not
/// supported on this OS". Targets Pi OS Bookworm + (NetworkManager
/// default since Pi OS 12 / 2023). Older Pi OS Bullseye + Buster used
/// <c>dhcpcd</c> + <c>wpa_supplicant</c>, which this service does
/// not touch.
///
/// Connection naming convention:
/// - <c>polaris-hotspot</c>  pre-seeded by the .deb (postinst +
///                           polaris-wifi-bootstrap.sh), so the Pi
///                           comes up as an AP on first boot even
///                           without Polaris running.
/// - <c>polaris-station</c>  created (or recreated) on demand each
///                           time the user picks a station network
///                           from the UI scan.
///
/// PolicyKit: the daemon runs as user <c>polaris</c>; the .deb ships
/// <c>/etc/polkit-1/rules.d/50-polaris-nm.rules</c> granting that user
/// unrestricted NetworkManager access without password prompts.
/// </summary>
public class NetworkManagerService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly ILogger<NetworkManagerService> _logger;

    public bool IsSupportedOs => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool NmcliInstalled { get; private set; }
    public string? NmcliVersion { get; private set; }
    public bool HasWifiInterface { get; private set; }
    public string? WifiInterface { get; private set; }

    public WifiMode CurrentMode { get; private set; } = WifiMode.Unknown;
    public string? CurrentSsid { get; private set; }
    public string? CurrentIp { get; private set; }
    public int SignalStrength { get; private set; }
    public string HotspotSsid { get; private set; } = "Polaris-Hotspot";
    public string? LastError { get; private set; }
    public DateTime? LastRefreshAt { get; private set; }

    /// <summary>When true, the snapshot loop watches for a prolonged
    /// "no WiFi connected" state and automatically brings the
    /// <c>polaris-hotspot</c> AP up so the rig stays reachable after it
    /// is moved out of range of every saved station network (e.g. taken
    /// to a different house). Configurable via
    /// <c>Network:AutoHotspotFallback</c>, default on.</summary>
    public bool AutoHotspotFallback { get; private set; }

    /// <summary>True once the watchdog has brought the AP up as a
    /// fallback (cleared again as soon as a station link reconnects).
    /// Surfaced in the snapshot so the UI can explain why the Pi is in
    /// hotspot mode without the user having asked for it.</summary>
    public bool HotspotFallbackEngaged { get; private set; }

    // ----- auto hotspot fallback watchdog state -----
    private readonly TimeSpan _fallbackGrace;
    private readonly string _hotspotPsk;
    // First time we observed "no WiFi" in the current disconnected
    // episode. Null while connected (station or AP).
    private DateTime? _disconnectedSince;
    // Suppress the watchdog while a manual switch is mid-flight (those
    // calls can block ~35s and transiently report Disconnected).
    private DateTime _suppressFallbackUntil = DateTime.MinValue;

    /// <summary>One-line, human-readable reason WiFi management is
    /// unavailable on this host. Null when everything is in order. The
    /// UI surfaces this directly in the Settings → Network banner so
    /// the user does not see a generic "click Switch" button on a
    /// platform that physically cannot drive nmcli.</summary>
    public string? UnsupportedReason {
        get {
            if (!IsSupportedOs)
                return $"WiFi management requires Linux + NetworkManager. {RuntimeInformation.OSDescription} is not supported. Manage WiFi via the OS settings.";
            if (!NmcliInstalled)
                return "nmcli not installed. Install with: sudo apt install network-manager";
            if (!HasWifiInterface)
                return "No WiFi interface detected. Ethernet-only mini PCs are managed via the OS.";
            return null;
        }
    }

    public NetworkManagerService(IConfiguration config,
                                  ILogger<NetworkManagerService> logger) {
        _config = config;
        _logger = logger;
        HotspotSsid = _config.GetValue("Network:HotspotSsid", "Polaris-Hotspot") ?? "Polaris-Hotspot";
        _hotspotPsk = _config.GetValue("Network:HotspotPsk", "polaris1234") ?? "polaris1234";
        AutoHotspotFallback = _config.GetValue("Network:AutoHotspotFallback", true);
        // Clamp the grace period to a sane floor so a misconfigured tiny
        // value cannot make the watchdog yank the link away mid-DHCP.
        var graceSec = Math.Max(20, _config.GetValue("Network:HotspotFallbackSeconds", 45));
        _fallbackGrace = TimeSpan.FromSeconds(graceSec);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!IsSupportedOs) {
            _logger.LogInformation("NetworkManagerService: OS {Os} not supported (Linux only), service idle",
                RuntimeInformation.OSDescription);
            LastError = UnsupportedReason;
            return;
        }

        await DetectNmcliAsync(stoppingToken);
        if (!NmcliInstalled) {
            _logger.LogInformation("NetworkManagerService: nmcli not found. WiFi management disabled.");
            return;
        }
        await DetectWifiInterfaceAsync(stoppingToken);
        if (!HasWifiInterface) {
            _logger.LogInformation("NetworkManagerService: no WiFi interface detected. Service idle.");
            return;
        }

        // 5s snapshot loop. Cheap (3 nmcli calls), keeps the UI WS
        // payload accurate without endpoint polling.
        while (!stoppingToken.IsCancellationRequested) {
            try { await RefreshSnapshotAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "Network snapshot refresh failed"); }
            try { await EvaluateHotspotFallbackAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "Hotspot fallback evaluation failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    // ----- detection -----

    private async Task DetectNmcliAsync(CancellationToken ct) {
        try {
            var which = await RunCommandAsync("which", "nmcli", ct);
            if (string.IsNullOrWhiteSpace(which.stdout)) return;
            var ver = await RunCommandAsync("nmcli", "--version", ct);
            // "nmcli tool, version 1.42.4-2"
            var line = (ver.stdout + " " + ver.stderr).Trim();
            var idx = line.IndexOf("version", StringComparison.OrdinalIgnoreCase);
            NmcliVersion = idx >= 0
                ? line[(idx + 8)..].Split(new[] { ' ', '\n', '\r', '-' })[0]
                : "unknown";
            NmcliInstalled = true;
            _logger.LogInformation("NetworkManagerService: detected nmcli v{Ver}", NmcliVersion);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "nmcli detection failed");
            NmcliInstalled = false;
        }
    }

    private async Task DetectWifiInterfaceAsync(CancellationToken ct) {
        try {
            var res = await RunCommandAsync("nmcli", "-t -f DEVICE,TYPE device status", ct);
            // Each line: "wlan0:wifi:...:..." or "eth0:ethernet:..."
            foreach (var line in res.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                var parts = SplitNmcliTerse(line);
                if (parts.Length >= 2 && parts[1].Equals("wifi", StringComparison.OrdinalIgnoreCase)) {
                    WifiInterface = parts[0];
                    HasWifiInterface = true;
                    _logger.LogInformation("NetworkManagerService: WiFi interface = {Iface}", WifiInterface);
                    return;
                }
            }
            HasWifiInterface = false;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "wifi interface detection failed");
            HasWifiInterface = false;
        }
    }

    // ----- snapshot -----

    public async Task<NetworkSnapshot> GetSnapshotAsync(CancellationToken ct = default) {
        if (HasWifiInterface) await RefreshSnapshotAsync(ct);
        return new NetworkSnapshot(
            SupportedOs: IsSupportedOs,
            NmcliInstalled: NmcliInstalled,
            HasWifi: HasWifiInterface,
            WifiInterface: WifiInterface,
            Mode: CurrentMode,
            Ssid: CurrentSsid,
            Ip: CurrentIp,
            Signal: SignalStrength,
            HotspotSsid: HotspotSsid,
            LastError: LastError,
            UnsupportedReason: UnsupportedReason,
            AutoHotspotFallback: AutoHotspotFallback,
            HotspotFallbackEngaged: HotspotFallbackEngaged);
    }

    private async Task RefreshSnapshotAsync(CancellationToken ct) {
        if (!NmcliInstalled || !HasWifiInterface) return;

        // 1. Active connection on the wifi iface, gives us (name, type, mode hint).
        // nmcli -t -f NAME,DEVICE,TYPE connection show --active
        var conn = await RunCommandAsync("nmcli",
            "-t -f NAME,DEVICE,TYPE connection show --active", ct);
        string? activeName = null;
        foreach (var line in conn.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = SplitNmcliTerse(line);
            if (parts.Length >= 3
                && string.Equals(parts[1], WifiInterface, StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("802-11-wireless", StringComparison.OrdinalIgnoreCase)) {
                activeName = parts[0];
                break;
            }
        }

        // 2. Iface state + IP + (in station mode) signal.
        // nmcli -t -f IP4.ADDRESS,GENERAL.STATE,WIFI-PROPERTIES.MODE device show wlan0
        // Simpler: ip from `device show`, ssid + mode from `device wifi`.
        var ipRes = await RunCommandAsync("nmcli",
            $"-t -f IP4.ADDRESS device show {Shell(WifiInterface!)}", ct);
        CurrentIp = ParseFirstIp4(ipRes.stdout);

        // 3. SSID currently in use + signal. Even in AP mode nmcli
        // reports the SSID we hand it (via wifi-sec).
        // nmcli -t -f IN-USE,SSID,SIGNAL,MODE device wifi list ifname wlan0
        var wifi = await RunCommandAsync("nmcli",
            $"-t -f IN-USE,SSID,SIGNAL,MODE device wifi list ifname {Shell(WifiInterface!)}",
            ct, timeoutMs: 8000);
        string? activeSsid = null;
        int signal = 0;
        string? activeMode = null;
        foreach (var line in wifi.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = SplitNmcliTerse(line);
            if (parts.Length < 4) continue;
            if (parts[0] == "*") {
                activeSsid = parts[1];
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out signal);
                activeMode = parts[3];
                break;
            }
        }

        CurrentSsid = activeSsid;
        SignalStrength = signal;
        CurrentMode = ResolveMode(activeName, activeMode, activeSsid);
        LastRefreshAt = DateTime.UtcNow;
    }

    private WifiMode ResolveMode(string? activeName, string? mode, string? ssid) {
        if (string.IsNullOrEmpty(ssid)) return WifiMode.Disconnected;
        // Prefer the explicit nmcli MODE column ("Infra" / "Ap"). Fall
        // back to the connection name we created (polaris-hotspot vs
        // polaris-station) so even older nmcli that omits MODE works.
        if (!string.IsNullOrEmpty(mode)) {
            if (mode.Equals("Ap", StringComparison.OrdinalIgnoreCase)) return WifiMode.Hotspot;
            if (mode.Equals("Infra", StringComparison.OrdinalIgnoreCase)) return WifiMode.Station;
        }
        if (activeName?.Equals("polaris-hotspot", StringComparison.OrdinalIgnoreCase) == true)
            return WifiMode.Hotspot;
        return WifiMode.Station;
    }

    // ----- scan -----

    /// <summary>Neighbouring WiFi networks, strongest first.</summary>
    /// <remarks>
    /// Forces a fresh scan with <c>--rescan yes</c>. With <c>auto</c> nmcli
    /// answers from its cache and only rescans if that cache is older than
    /// ~30s, and the cache right after a connect holds little beyond the
    /// network the adapter is ON: the field report was a picker that listed
    /// the device's own network and nothing else.
    ///
    /// Some drivers refuse an explicit scan while the interface is running an
    /// AP ("Scanning not allowed while ..."), which is exactly the state a
    /// first-time user is in when they open this picker on the hotspot. So a
    /// refused rescan falls back to the cached list instead of failing: a
    /// stale list beats an empty one.
    ///
    /// The host's own hotspot SSID is dropped from the result. It is not a
    /// network anyone can join from here, and offering it as a target invites
    /// exactly the "connect the adapter to itself" confusion that was
    /// reported.
    /// </remarks>
    public async Task<List<WifiNetwork>> ScanAsync(CancellationToken ct = default) {
        if (!NmcliInstalled || !HasWifiInterface) return new();
        string ListArgs(string rescan) =>
            $"-t -f SSID,SIGNAL,SECURITY,IN-USE device wifi list ifname {Shell(WifiInterface!)} --rescan {rescan}";

        var res = await RunCommandAsync("nmcli", ListArgs("yes"), ct, timeoutMs: 25000);
        if (res.exitCode != 0) {
            _logger.LogDebug("wifi rescan refused ({Err}); falling back to the cached list",
                res.stderr.Trim());
            res = await RunCommandAsync("nmcli", ListArgs("auto"), ct, timeoutMs: 15000);
        }

        var byBest = new Dictionary<string, WifiNetwork>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in res.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = SplitNmcliTerse(line);
            if (parts.Length < 4) continue;
            var ssid = parts[0];
            if (string.IsNullOrEmpty(ssid)) continue; // hidden networks (--) — skip
            if (CurrentMode == WifiMode.Hotspot
                && ssid.Equals(HotspotSsid, StringComparison.OrdinalIgnoreCase)) {
                continue;   // our own AP, beaconing at us
            }
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sig);
            var sec = parts[2];
            var inUse = parts[3] == "*";
            if (byBest.TryGetValue(ssid, out var prev) && prev.Signal >= sig) continue;
            byBest[ssid] = new WifiNetwork(ssid, sig, sec, inUse);
        }
        return byBest.Values.OrderByDescending(n => n.Signal).ToList();
    }

    // ----- switch (try-and-revert) -----

    /// <summary>Switch the WiFi interface from whatever it is doing
    /// now into Station mode on the named SSID. Try-and-revert: if the
    /// connection does not get a DHCP lease + reachable gateway within
    /// 30 s the previous mode (hotspot if we were one, otherwise no-op)
    /// is restored so the user does not lose access to the Pi when the
    /// password is wrong or the AP is out of range.</summary>
    public async Task<SwitchResult> SwitchToStationAsync(string ssid, string password, CancellationToken ct = default) {
        if (!IsSupportedOs)      return SwitchResult.Fail("OS not supported");
        if (!NmcliInstalled)     return SwitchResult.Fail("nmcli not installed");
        if (!HasWifiInterface)   return SwitchResult.Fail("No WiFi interface");
        var v = ValidateSsidPsk(ssid, password);
        if (v != null) return SwitchResult.Fail(v);

        // Keep the fallback watchdog out of the way: this call can block
        // ~65s (up to 35s connect + 30s lease wait) during which the link
        // legitimately reports Disconnected. The user explicitly asked
        // for station, so do not race them onto the AP.
        _suppressFallbackUntil = DateTime.UtcNow + TimeSpan.FromSeconds(80);
        _disconnectedSince = null;
        HotspotFallbackEngaged = false;

        var hotspotWasUp = (CurrentMode == WifiMode.Hotspot);

        // Drop any prior polaris-station so we start from a clean slate.
        // Ignore the exit code, the connection legitimately does not
        // exist on the first switch.
        await RunCommandAsync("nmcli", "connection delete polaris-station", ct, timeoutMs: 5000);

        // autoconnect-priority 10 (vs the hotspot's -10) makes NM prefer
        // this station network whenever it is in range, leaving the AP as
        // the natural fallback when it is not.
        var add = await RunCommandAsync("nmcli",
            $"connection add type wifi ifname {Shell(WifiInterface!)} con-name polaris-station " +
            $"connection.autoconnect-priority 10 " +
            $"ssid {Shell(ssid)} wifi-sec.key-mgmt wpa-psk wifi-sec.psk {Shell(password)}",
            ct, timeoutMs: 8000);
        if (add.exitCode != 0) {
            LastError = $"nmcli add failed: {add.stderr.Trim()}";
            return SwitchResult.Fail(LastError);
        }

        var up = await RunCommandAsync("nmcli",
            "connection up polaris-station", ct, timeoutMs: 35000);
        if (up.exitCode != 0) {
            LastError = $"nmcli up failed (likely bad password / AP out of range): {up.stderr.Trim()}";
            await RevertToHotspotAsync(hotspotWasUp, ct);
            return SwitchResult.Fail(LastError);
        }

        var leaseOk = await WaitForLeaseAsync(WifiInterface!, TimeSpan.FromSeconds(30), ct);
        if (!leaseOk) {
            LastError = "No DHCP lease within 30s, reverting to hotspot";
            await RevertToHotspotAsync(hotspotWasUp, ct);
            return SwitchResult.Fail(LastError);
        }

        await RefreshSnapshotAsync(ct);
        LastError = null;
        return SwitchResult.Success(CurrentIp);
    }

    /// <summary>Switch the WiFi interface back into Hotspot mode using
    /// the pre-seeded <c>polaris-hotspot</c> connection. No try-and-revert
    /// since AP mode does not need a DHCP lease, the failure mode here is
    /// just "AP failed to start" which we surface to the user.</summary>
    public async Task<SwitchResult> SwitchToHotspotAsync(CancellationToken ct = default) {
        if (!IsSupportedOs)    return SwitchResult.Fail("OS not supported");
        if (!NmcliInstalled)   return SwitchResult.Fail("nmcli not installed");
        if (!HasWifiInterface) return SwitchResult.Fail("No WiFi interface");

        // User asked for the AP explicitly; this is not a fallback.
        _suppressFallbackUntil = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        _disconnectedSince = null;
        HotspotFallbackEngaged = false;

        await EnsureHotspotConnectionAsync(ct);
        var up = await RunCommandAsync("nmcli",
            "connection up polaris-hotspot", ct, timeoutMs: 20000);
        if (up.exitCode != 0) {
            LastError = $"nmcli up polaris-hotspot failed: {up.stderr.Trim()}. " +
                $"If the connection does not exist, run /opt/polaris/bin/polaris-wifi-bootstrap.sh as root.";
            return SwitchResult.Fail(LastError);
        }
        await RefreshSnapshotAsync(ct);
        LastError = null;
        return SwitchResult.Success(CurrentIp);
    }

    /// <summary>Update the SSID + password on the polaris-hotspot
    /// connection. Bringing it back up applies the change. Caller is
    /// responsible for warning the user that any device connected to
    /// the old SSID will be disconnected.</summary>
    public async Task<SwitchResult> SetHotspotCredentialsAsync(string ssid, string password, CancellationToken ct = default) {
        if (!IsSupportedOs)    return SwitchResult.Fail("OS not supported");
        if (!NmcliInstalled)   return SwitchResult.Fail("nmcli not installed");
        if (!HasWifiInterface) return SwitchResult.Fail("No WiFi interface");
        var v = ValidateSsidPsk(ssid, password);
        if (v != null) return SwitchResult.Fail(v);

        // Rebouncing the AP drops clients briefly; keep the watchdog out.
        _suppressFallbackUntil = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        var mod = await RunCommandAsync("nmcli",
            $"connection modify polaris-hotspot 802-11-wireless.ssid {Shell(ssid)} " +
            $"wifi-sec.psk {Shell(password)}", ct, timeoutMs: 8000);
        if (mod.exitCode != 0) {
            LastError = $"nmcli modify failed: {mod.stderr.Trim()}";
            return SwitchResult.Fail(LastError);
        }
        HotspotSsid = ssid;

        // Rebounce the connection so the change actually takes effect.
        // Ignore exit code, "Connection successfully activated" sometimes
        // returns non-zero on the first attempt while NM cycles wpa.
        await RunCommandAsync("nmcli", "connection down polaris-hotspot", ct, timeoutMs: 8000);
        await Task.Delay(500, ct);
        var up = await RunCommandAsync("nmcli", "connection up polaris-hotspot", ct, timeoutMs: 15000);
        if (up.exitCode != 0) {
            LastError = $"hotspot restart failed: {up.stderr.Trim()}";
            return SwitchResult.Fail(LastError);
        }
        await RefreshSnapshotAsync(ct);
        LastError = null;
        return SwitchResult.Success(CurrentIp);
    }

    // ----- auto hotspot fallback watchdog -----

    /// <summary>Called once per snapshot tick. When WiFi has been
    /// disconnected (no station associated, AP not up) for longer than
    /// the grace window, brings the <c>polaris-hotspot</c> AP up so the
    /// rig stays reachable without an ethernet cable. This is the path
    /// that recovers a Pi configured for the user's home network after
    /// it is carried somewhere that network is out of range: the saved
    /// station profile never associates, and nothing else would bring
    /// the AP up on its own.</summary>
    private async Task EvaluateHotspotFallbackAsync(CancellationToken ct) {
        if (!AutoHotspotFallback || !NmcliInstalled || !HasWifiInterface) return;
        var now = DateTime.UtcNow;

        // Connected (station link or AP serving clients) => healthy.
        if (CurrentMode == WifiMode.Station || CurrentMode == WifiMode.Hotspot) {
            _disconnectedSince = null;
            // A real station reconnect means the fallback is no longer in
            // effect; if WE put the AP up, leave the flag set so the UI
            // can still explain it until the user reconnects to a network.
            if (CurrentMode == WifiMode.Station) HotspotFallbackEngaged = false;
            return;
        }

        // Disconnected / Unknown: open (or continue) the grace timer.
        _disconnectedSince ??= now;

        if (!ShouldEngageHotspotFallback(CurrentMode, _disconnectedSince, now,
                _fallbackGrace, AutoHotspotFallback, _suppressFallbackUntil))
            return;

        _logger.LogWarning(
            "NetworkManagerService: no WiFi for {Sec:n0}s (no saved network in range). " +
            "Starting polaris-hotspot so the rig stays reachable.",
            (now - _disconnectedSince.Value).TotalSeconds);

        var ok = await EnsureAndStartHotspotAsync(ct);
        if (ok) {
            HotspotFallbackEngaged = true;
            _disconnectedSince = null;
            await RefreshSnapshotAsync(ct);
        } else {
            // Bringing the AP up failed; back off a full grace window
            // instead of hammering nmcli every 5 s.
            _disconnectedSince = now;
        }
    }

    /// <summary>Pure decision for the watchdog, factored out so it can be
    /// unit-tested without nmcli. Engage the AP fallback only when it is
    /// enabled, not suppressed by an in-flight manual switch, the link is
    /// genuinely down, and it has stayed down past the grace window.</summary>
    internal static bool ShouldEngageHotspotFallback(
            WifiMode mode, DateTime? disconnectedSince, DateTime now,
            TimeSpan grace, bool enabled, DateTime suppressUntil) {
        if (!enabled) return false;
        if (now < suppressUntil) return false;
        if (mode == WifiMode.Station || mode == WifiMode.Hotspot) return false;
        if (disconnectedSince == null) return false;
        return now - disconnectedSince.Value >= grace;
    }

    /// <summary>Ensures the <c>polaris-hotspot</c> connection exists
    /// (recreating it if the .deb bootstrap never ran), then brings it
    /// up. Returns true only when nmcli reports the AP activated.</summary>
    private async Task<bool> EnsureAndStartHotspotAsync(CancellationToken ct) {
        await EnsureHotspotConnectionAsync(ct);
        var up = await RunCommandAsync("nmcli", "connection up polaris-hotspot", ct, timeoutMs: 20000);
        if (up.exitCode != 0) {
            LastError = $"auto hotspot fallback failed: {up.stderr.Trim()}";
            _logger.LogWarning("NetworkManagerService: {Err}", LastError);
            return false;
        }
        return true;
    }

    /// <summary>Creates the <c>polaris-hotspot</c> AP connection if it is
    /// missing, mirroring the .deb bootstrap (2.4 GHz b/g, shared IPv4 so
    /// NM hands clients DHCP+DNS+NAT). No-op when the connection already
    /// exists. Makes the in-app fallback self-sufficient even on a Pi
    /// where the first-boot bootstrap service never ran.</summary>
    private async Task EnsureHotspotConnectionAsync(CancellationToken ct) {
        try {
            var show = await RunCommandAsync("nmcli", "-t -f NAME connection show", ct, timeoutMs: 5000);
            var exists = show.stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => SplitNmcliTerse(l).FirstOrDefault())
                .Any(n => string.Equals(n, "polaris-hotspot", StringComparison.OrdinalIgnoreCase));
            if (exists) return;

            var ssid = HotspotSsid;
            var psk = _hotspotPsk;
            if (ValidateSsidPsk(ssid, psk) != null) { ssid = "Polaris-Hotspot"; psk = "polaris1234"; }

            _logger.LogInformation(
                "NetworkManagerService: polaris-hotspot connection missing, recreating it (SSID {Ssid})", ssid);
            var add = await RunCommandAsync("nmcli",
                $"connection add type wifi ifname {Shell(WifiInterface!)} con-name polaris-hotspot " +
                $"autoconnect no ssid {Shell(ssid)} " +
                $"802-11-wireless.mode ap 802-11-wireless.band bg " +
                $"ipv4.method shared ipv6.method ignore " +
                $"wifi-sec.key-mgmt wpa-psk wifi-sec.psk {Shell(psk)}",
                ct, timeoutMs: 8000);
            if (add.exitCode != 0)
                _logger.LogWarning("NetworkManagerService: failed to recreate polaris-hotspot: {Err}", add.stderr.Trim());
        } catch (Exception ex) {
            _logger.LogDebug(ex, "EnsureHotspotConnectionAsync failed");
        }
    }

    private async Task RevertToHotspotAsync(bool hotspotWasUp, CancellationToken ct) {
        try {
            await RunCommandAsync("nmcli", "connection down polaris-station", ct, timeoutMs: 5000);
        } catch { }
        if (hotspotWasUp) {
            try {
                await RunCommandAsync("nmcli", "connection up polaris-hotspot", ct, timeoutMs: 15000);
            } catch { }
        }
        await RefreshSnapshotAsync(ct);
    }

    /// <summary>Polls nmcli for an IPv4 address on the iface, plus a
    /// ping against the inferred default gateway. Both have to succeed
    /// for the switch to count as a success, an IP without a reachable
    /// gateway means we got a lease from a router that has not figured
    /// out it is now our default route yet, or the AP gave us a bogus
    /// lease.</summary>
    internal async Task<bool> WaitForLeaseAsync(string iface, TimeSpan timeout, CancellationToken ct) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
            try {
                var ip = await RunCommandAsync("nmcli",
                    $"-t -f IP4.ADDRESS,IP4.GATEWAY device show {Shell(iface)}", ct, timeoutMs: 3000);
                var addr = ParseFirstIp4(ip.stdout);
                var gw = ParseGateway(ip.stdout);
                if (addr != null && gw != null) {
                    // ping -c 1 -W 2 GW
                    var p = await RunCommandAsync("ping",
                        $"-c 1 -W 2 {gw}", ct, timeoutMs: 4000);
                    if (p.exitCode == 0) return true;
                }
            } catch { }
            try { await Task.Delay(1500, ct); }
            catch (TaskCanceledException) { return false; }
        }
        return false;
    }

    internal static string? ParseGateway(string nmcliStdout) {
        foreach (var line in nmcliStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            // "IP4.GATEWAY:10.42.0.1" or "IP4.GATEWAY:--"
            if (!line.StartsWith("IP4.GATEWAY", StringComparison.OrdinalIgnoreCase)) continue;
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var val = line[(idx + 1)..].Trim();
            if (val.Length == 0 || val == "--") return null;
            return val;
        }
        return null;
    }

    /// <summary>WPA2-PSK requires 8-63 ASCII chars. SSID per IEEE 802.11
    /// must be 1-32 octets. Reject early with a friendly message instead
    /// of letting nmcli fail mid-pipeline.</summary>
    internal static string? ValidateSsidPsk(string ssid, string psk) {
        if (string.IsNullOrEmpty(ssid)) return "SSID is required";
        if (ssid.Length > 32)            return "SSID must be 32 characters or fewer";
        if (psk == null || psk.Length < 8 || psk.Length > 63)
            return "WiFi password must be 8 to 63 characters (WPA2 requirement)";
        return null;
    }

    // ----- helpers -----

    /// <summary>Wraps an argument that may contain spaces in double
    /// quotes for inclusion in an nmcli command line. Throws on
    /// embedded quotes, never silently strips them, so a malicious
    /// SSID/PSK cannot smuggle extra args.</summary>
    private static string Shell(string s) {
        if (s.Contains('"') || s.Contains('\\') || s.Contains('`') || s.Contains('$'))
            throw new ArgumentException($"argument contains shell-significant character: {s}");
        return s.Contains(' ') ? "\"" + s + "\"" : s;
    }

    /// <summary>Splits an nmcli terse-mode line on unescaped ':'. nmcli
    /// terse output escapes literal ':' as '\:' inside field values, so
    /// a naive String.Split mis-aligns columns when an SSID contains a
    /// colon. Walks the string preserving backslash-escapes.</summary>
    internal static string[] SplitNmcliTerse(string line) {
        var fields = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++) {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length) { sb.Append(line[i + 1]); i++; continue; }
            if (c == ':') { fields.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    internal static string? ParseFirstIp4(string nmcliStdout) {
        foreach (var line in nmcliStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            // "IP4.ADDRESS[1]:10.42.0.1/24"
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var val = line[(idx + 1)..];
            var slash = val.IndexOf('/');
            if (slash > 0) val = val[..slash];
            val = val.Trim();
            if (val.Count(c => c == '.') == 3) return val;
        }
        return null;
    }

    internal static async Task<(int exitCode, string stdout, string stderr)>
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

public enum WifiMode { Unknown, Disconnected, Hotspot, Station, Unsupported }

public record WifiNetwork(string Ssid, int Signal, string Security, bool InUse);

public record SwitchResult(bool Ok, string? Error, string? Ip) {
    public static SwitchResult Success(string? ip) => new(true, null, ip);
    public static SwitchResult Fail(string error) => new(false, error, null);
}

public record NetworkSnapshot(
    bool SupportedOs,
    bool NmcliInstalled,
    bool HasWifi,
    string? WifiInterface,
    WifiMode Mode,
    string? Ssid,
    string? Ip,
    int Signal,
    string HotspotSsid,
    string? LastError,
    string? UnsupportedReason,
    bool AutoHotspotFallback = true,
    bool HotspotFallbackEngaged = false);