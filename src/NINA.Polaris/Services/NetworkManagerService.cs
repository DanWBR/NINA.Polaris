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
            UnsupportedReason: UnsupportedReason);
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

    public async Task<List<WifiNetwork>> ScanAsync(CancellationToken ct = default) {
        if (!NmcliInstalled || !HasWifiInterface) return new();
        var res = await RunCommandAsync("nmcli",
            $"-t -f SSID,SIGNAL,SECURITY,IN-USE device wifi list ifname {Shell(WifiInterface!)} --rescan auto",
            ct, timeoutMs: 15000);
        var byBest = new Dictionary<string, WifiNetwork>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in res.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = SplitNmcliTerse(line);
            if (parts.Length < 4) continue;
            var ssid = parts[0];
            if (string.IsNullOrEmpty(ssid)) continue; // hidden networks (--) — skip
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

        var hotspotWasUp = (CurrentMode == WifiMode.Hotspot);

        // Drop any prior polaris-station so we start from a clean slate.
        // Ignore the exit code, the connection legitimately does not
        // exist on the first switch.
        await RunCommandAsync("nmcli", "connection delete polaris-station", ct, timeoutMs: 5000);

        var add = await RunCommandAsync("nmcli",
            $"connection add type wifi ifname {Shell(WifiInterface!)} con-name polaris-station " +
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
    string? UnsupportedReason);
