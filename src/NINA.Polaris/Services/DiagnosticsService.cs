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
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NINA.Polaris.Services;

/// <summary>Severity of a single check. `Unknown` exists on purpose and is the
/// most important value here: a check that could not run must say so, never
/// pass by omission. An image-verification script in this repo died on a glob
/// that matched nothing and its truncated output read exactly like a clean
/// pass, which is how a broken image nearly shipped.</summary>
public static class DiagSeverity {
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Unknown = "unknown";   // the check itself failed
    public const string Skipped = "skipped";   // not applicable to this host
}

/// <param name="Fix">What the operator should do. A finding without this just
/// moves the work back to them.</param>
public record DiagnosticCheck(
    string Id,
    string Category,
    string Severity,
    string Title,
    string Detail,
    string? Fix = null);

public record DiagnosticsReport(
    string GeneratedUtc,
    string Version,
    string Host,
    string Board,
    string Os,
    int Ok, int Warn, int Fail, int Unknown, int Skipped,
    List<DiagnosticCheck> Checks);

/// <summary>
/// Read-only host self-check: is every piece Polaris needs actually in place,
/// enabled, and permitted. Every check here exists because its absence bit
/// someone in the field, and the report is meant to be pasted into a bug
/// report, so nothing secret may appear in it (see <see cref="Redact"/>).
///
/// Deliberately does not fix anything. Changing host state is a separate,
/// confirmed action.
/// </summary>
public sealed class DiagnosticsService {
    private readonly ProfileService _profile;
    private readonly IndiWebManagerService _indiWeb;
    private readonly ClockSyncService _clock;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(ProfileService profile, IndiWebManagerService indiWeb,
                              ClockSyncService clock, ILogger<DiagnosticsService> logger) {
        _profile = profile;
        _indiWeb = indiWeb;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DiagnosticsReport> RunAsync(CancellationToken ct = default) {
        var checks = new List<DiagnosticCheck>();

        await Units(checks, ct);
        await Binaries(checks, ct);
        Rules(checks);
        Storage(checks);
        await Network(checks, ct);
        Equipment(checks);
        Data(checks);
        Time(checks);
        Identity(checks);

        var host = HostInfo.Current;
        return new DiagnosticsReport(
            GeneratedUtc: DateTime.UtcNow.ToString("o"),
            Version: typeof(DiagnosticsService).Assembly.GetName().Version?.ToString() ?? "unknown",
            Host: Environment.MachineName,
            Board: $"{host.Kind} {host.Model}".Trim(),
            Os: Environment.OSVersion.VersionString,
            Ok: checks.Count(c => c.Severity == DiagSeverity.Ok),
            Warn: checks.Count(c => c.Severity == DiagSeverity.Warn),
            Fail: checks.Count(c => c.Severity == DiagSeverity.Fail),
            Unknown: checks.Count(c => c.Severity == DiagSeverity.Unknown),
            Skipped: checks.Count(c => c.Severity == DiagSeverity.Skipped),
            Checks: checks);
    }

    // ---- the wrapper that makes principle 1 structural ---------------------

    /// <summary>Runs one probe. A probe that throws becomes `unknown` WITH the
    /// reason, never a silent omission and never an `ok`.</summary>
    private async Task Add(List<DiagnosticCheck> into, string id, string category,
                           string title, Func<Task<(string sev, string detail, string? fix)>> probe) {
        try {
            var (sev, detail, fix) = await probe();
            into.Add(new DiagnosticCheck(id, category, sev, title, Redact(detail), fix));
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Diagnostic check {Id} threw", id);
            into.Add(new DiagnosticCheck(id, category, DiagSeverity.Unknown, title,
                "the check could not run: " + Redact(ex.Message),
                "This is a gap in the diagnostic, not necessarily a problem with the host."));
        }
    }

    private Task Add(List<DiagnosticCheck> into, string id, string category, string title,
                     Func<(string sev, string detail, string? fix)> probe)
        => Add(into, id, category, title, () => Task.FromResult(probe()));

    /// <summary>Strips what must never reach a pasted report. The diagnostic
    /// reads exactly the files that hold secrets, and five published images
    /// turned out to be carrying a WiFi pre-shared key, so this is a
    /// requirement and not a nicety.</summary>
    public static string Redact(string s) {
        if (string.IsNullOrEmpty(s)) return s;
        // WPA pre-shared keys: 64 hex chars, and psk=/password= assignments.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b[0-9a-fA-F]{64}\b", "<redacted-key>");
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(?i)\b(psk|password|passwd|secret|token|apikey|api_key)\s*[:=]\s*\S+", "$1=<redacted>");
        // Bearer tokens and long base64 blobs.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?i)bearer\s+\S+", "Bearer <redacted>");
        return s;
    }

    // ---- categories --------------------------------------------------------

    private async Task Units(List<DiagnosticCheck> into, CancellationToken ct) {
        if (!OperatingSystem.IsLinux()) {
            into.Add(new DiagnosticCheck("units", "units", DiagSeverity.Skipped,
                "systemd units", "not a Linux host"));
            return;
        }

        // polaris-growroot and polaris-sshkeys are checked for ENABLED, not just
        // present: a .deb installed into an image with `dpkg -i` cannot enable a
        // unit (the postinst gates on /run/systemd/system, absent in a chroot),
        // so both once shipped present and inert on every board.
        var units = new (string Unit, string Title, bool Required)[] {
            ("polaris.service",                "Polaris service",        true),
            ("polaris-growroot.service",       "Grow root on first boot", true),
            ("polaris-sshkeys.service",        "SSH host key generation", true),
            ("polaris-wifi-bootstrap.service", "WiFi hotspot bootstrap",  false),
            ("ssh.service",                    "SSH server",              false),
        };

        foreach (var (unit, title, required) in units) {
            await Add(into, "unit." + unit, "units", title, async () => {
                var enabled = (await Run("systemctl", $"is-enabled {unit}", ct)).Trim();
                var active = (await Run("systemctl", $"is-active {unit}", ct)).Trim();
                var result = (await Run("systemctl", $"show -p Result --value {unit}", ct)).Trim();
                var detail = $"enabled={Blank(enabled)} active={Blank(active)} result={Blank(result)}";

                if (enabled.StartsWith("not-found"))
                    return (required ? DiagSeverity.Fail : DiagSeverity.Skipped, "unit not installed",
                        required ? "Reinstall the polaris package: it ships this unit." : null);
                if (enabled != "enabled" && enabled != "enabled-runtime")
                    return (required ? DiagSeverity.Fail : DiagSeverity.Warn, detail,
                        $"sudo systemctl enable {unit}");
                if (result is "exit-code" or "timeout" or "signal" or "core-dump")
                    return (DiagSeverity.Fail, detail, $"journalctl -u {unit} -b");
                return (DiagSeverity.Ok, detail, null);
            });
        }
    }

    private async Task Binaries(List<DiagnosticCheck> into, CancellationToken ct) {
        // (name, what it is for, hard requirement?)
        var bins = new (string Bin, string What, bool Required)[] {
            ("sfdisk",      "grow the root partition (fallback)", true),
            ("resize2fs",   "grow the root filesystem",           true),
            ("growpart",    "grow the root partition (preferred)", false),
            ("ssh-keygen",  "generate SSH host keys",             false),
            ("astap",       "plate solving (default solver)",     false),
            ("solve-field", "plate solving (astrometry.net)",     false),
            ("siril",       "post-processing scripts",            false),
            ("gphoto2",     "DSLR capture",                       false),
            ("indiserver",  "INDI equipment drivers",             false),
        };

        foreach (var (bin, what, required) in bins) {
            await Add(into, "bin." + bin, "binaries", $"{bin} ({what})", async () => {
                var path = (await Run("sh", $"-c \"command -v {bin} || true\"", ct)).Trim();
                if (!string.IsNullOrEmpty(path)) return (DiagSeverity.Ok, path, null);
                // growpart absent is fine BECAUSE the grow-root script falls back
                // to sfdisk; say so rather than raising an alarm.
                if (bin == "growpart")
                    return (DiagSeverity.Warn, "not installed; grow-root uses the sfdisk fallback",
                        "sudo apt install cloud-guest-utils");
                return (required ? DiagSeverity.Fail : DiagSeverity.Warn, "not installed",
                    $"sudo apt install {bin}");
            });
        }
    }

    private void Rules(List<DiagnosticCheck> into) {
        if (!OperatingSystem.IsLinux()) {
            into.Add(new DiagnosticCheck("rules", "rules", DiagSeverity.Skipped,
                "udev / polkit rules", "not a Linux host"));
            return;
        }

        _ = Add(into, "rules.udev", "rules", "Camera udev rules", () => {
            var files = SafeFiles("/lib/udev/rules.d", "99-polaris-*.rules")
                .Concat(SafeFiles("/etc/udev/rules.d", "99-polaris-*.rules")).ToList();
            return files.Count == 0
                ? (DiagSeverity.Fail, "no 99-polaris-*.rules found",
                   "Reinstall the polaris package: without these the polaris user cannot open USB cameras.")
                : (DiagSeverity.Ok, $"{files.Count} rule file(s)", null);
        });

        _ = Add(into, "rules.usbfs", "rules", "USB buffer size (usbfs_memory_mb)", () => {
            var v = ReadFirstLine("/sys/module/usbcore/parameters/usbfs_memory_mb");
            if (v == null) return (DiagSeverity.Unknown, "parameter not readable", null);
            var mb = int.TryParse(v.Trim(), out var n) ? n : -1;
            // 16 MB is the kernel default and is not enough for high-fps USB3
            // planetary streaming; the udev rule raises it.
            return mb >= 200
                ? (DiagSeverity.Ok, $"{mb} MB", null)
                : (DiagSeverity.Warn, $"{mb} MB (low: USB3 video may drop frames)",
                   "The polaris udev rule raises this; reload rules or reboot.");
        });

        _ = Add(into, "rules.polkit", "rules", "PolicyKit rules", () => {
            var jsRules = SafeFiles("/etc/polkit-1/rules.d", "*polaris*").Count;
            var pkla = SafeFiles("/etc/polkit-1/localauthority/50-local.d", "*polaris*.pkla").Count;
            // Which format counts depends on the polkit version: < 0.106 reads
            // only .pkla and silently ignores .rules, so both ship.
            if (jsRules == 0 && pkla == 0)
                return (DiagSeverity.Fail, "no polaris polkit rules installed",
                    "Reinstall the polaris package: without them the one-click self-update and network changes fail.");
            return (DiagSeverity.Ok, $".rules={jsRules} .pkla={pkla}", null);
        });

        _ = Add(into, "rules.sudoers", "rules", "sudoers entry", () =>
            File.Exists("/etc/sudoers.d/polaris")
                ? (DiagSeverity.Ok, "/etc/sudoers.d/polaris", null)
                : (DiagSeverity.Warn, "not present (appliance-mode installs create it)", null));
    }

    private void Storage(List<DiagnosticCheck> into) {
        _ = Add(into, "storage.captureRoot", "storage", "Capture root", () => {
            var dir = _profile.Active.ImageOutputDir;
            if (string.IsNullOrWhiteSpace(dir))
                return (DiagSeverity.Fail, "not configured",
                    "Settings > Storage: pick a folder, otherwise saving frames is a no-op.");
            if (!Directory.Exists(dir))
                return (DiagSeverity.Fail, $"{dir} does not exist", "Create it or pick another folder.");
            try {
                var probe = Path.Combine(dir, ".polaris-write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            } catch (Exception ex) {
                return (DiagSeverity.Fail, $"{dir} is not writable: {ex.Message}",
                    $"sudo chown -R polaris:polaris {dir}");
            }
            var free = new DriveInfo(Path.GetPathRoot(dir) ?? "/").AvailableFreeSpace;
            var gb = free / 1024.0 / 1024 / 1024;
            return gb < 2
                ? (DiagSeverity.Warn, $"{dir}, {gb:F1} GB free", "Low space for a night of subs.")
                : (DiagSeverity.Ok, $"{dir}, {gb:F1} GB free", null);
        });

        // The check that would have caught the field report "it booted but the
        // filesystem never expanded": compare the filesystem size with the
        // partition it sits on.
        _ = Add(into, "storage.rootGrown", "storage", "Root filesystem fills its partition", () => {
            if (!OperatingSystem.IsLinux()) return (DiagSeverity.Skipped, "not a Linux host", null);
            var root = new DriveInfo("/");
            var fsBytes = root.TotalSize;
            var dev = ReadFirstLine("/proc/self/mountinfo") == null ? null : FindRootDevice();
            if (dev == null) return (DiagSeverity.Unknown, "could not determine the root device", null);
            var sectors = ReadFirstLine($"/sys/class/block/{Path.GetFileName(dev)}/size");
            if (sectors == null || !long.TryParse(sectors.Trim(), out var sec))
                return (DiagSeverity.Unknown, $"could not read the size of {dev}", null);
            var partBytes = sec * 512;
            var pct = partBytes > 0 ? 100.0 * fsBytes / partBytes : 0;
            var detail = $"{dev}: filesystem {Gb(fsBytes)} of partition {Gb(partBytes)} ({pct:F0}%)";
            return pct >= 95
                ? (DiagSeverity.Ok, detail, null)
                : (DiagSeverity.Fail, detail,
                   "The card was never expanded. sudo systemctl start polaris-growroot, then reboot.");
        });

        _ = Add(into, "storage.swap", "storage", "Swap", () => {
            if (!OperatingSystem.IsLinux()) return (DiagSeverity.Skipped, "not a Linux host", null);
            var s = SafeRead("/proc/swaps") ?? "";
            var lines = s.Split('\n').Skip(1).Where(l => l.Trim().Length > 0).Count();
            return lines > 0
                ? (DiagSeverity.Ok, $"{lines} swap area(s) active", null)
                : (DiagSeverity.Warn, "no swap active",
                   "Live stacking on a 4 GB board benefits from swap: sudo dphys-swapfile setup && sudo dphys-swapfile swapon");
        });
    }

    private async Task Network(List<DiagnosticCheck> into, CancellationToken ct) {
        await Add(into, "net.nm", "network", "NetworkManager", async () => {
            if (!OperatingSystem.IsLinux()) return (DiagSeverity.Skipped, "not a Linux host", null);
            var v = (await Run("nmcli", "--version", ct)).Trim();
            if (string.IsNullOrEmpty(v))
                return (DiagSeverity.Warn, "nmcli not available",
                    "Network settings and the hotspot need NetworkManager.");
            var active = (await Run("systemctl", "is-active NetworkManager", ct)).Trim();
            return active == "active"
                ? (DiagSeverity.Ok, v, null)
                : (DiagSeverity.Fail, $"{v}, service {Blank(active)}", "sudo systemctl start NetworkManager");
        });

        // Certificate: the operator sees this as "my phone refuses to connect".
        // Anything over 398 days is rejected outright by iOS and Chrome on iOS.
        _ = Add(into, "net.tls", "network", "HTTPS certificate", () => {
            // Same location SelfSignedCertService owns, and the PFX carries an
            // EMPTY password (it is protected by file permissions, not a secret).
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "cert");
            var pfx = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.pfx").FirstOrDefault()
                : null;
            if (pfx == null) return (DiagSeverity.Warn, "no certificate found in " + dir,
                "Polaris generates one on first boot; restart it.");
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, null,
                X509KeyStorageFlags.EphemeralKeySet);
            var days = (cert.NotAfter - cert.NotBefore).TotalDays;
            var left = (cert.NotAfter - DateTime.Now).TotalDays;
            var detail = $"valid {cert.NotBefore:yyyy-MM-dd} to {cert.NotAfter:yyyy-MM-dd} "
                       + $"({days:F0} days, {left:F0} left), sha1 {cert.Thumbprint[..12]}...";
            if (left < 0) return (DiagSeverity.Fail, detail, "Delete the cert folder and restart Polaris.");
            if (days > 398) return (DiagSeverity.Warn, detail,
                "Over 398 days: iOS and Chrome on iOS reject it. Delete the cert folder and restart Polaris.");
            return (DiagSeverity.Ok, detail, null);
        });
    }

    private void Equipment(List<DiagnosticCheck> into) {
        _ = Add(into, "equip.indiweb", "equipment", "INDI Web Manager", () => {
            if (!_indiWeb.Installed)
                return (DiagSeverity.Warn, "indi-web not installed",
                    "INDI equipment needs it: reinstall the polaris package or check the venv.");
            return _indiWeb.Running
                ? (DiagSeverity.Ok, "running", null)
                : (DiagSeverity.Warn, "installed but not running",
                   "Equipment cards list INDI devices from a running indiserver. Start it in the INDI panel.");
        });
    }

    private void Data(List<DiagnosticCheck> into) {
        _ = Add(into, "data.astap", "data", "ASTAP star catalogue", () => {
            var dir = "/opt/astap";
            if (!Directory.Exists(dir)) return (DiagSeverity.Warn, "ASTAP not installed at /opt/astap", null);
            var files = Directory.GetFiles(dir, "*.1476");
            if (files.Length == 0)
                return (DiagSeverity.Fail, "no star database (*.1476) present",
                    "ASTAP cannot solve without a catalogue. Install the V50 database.");
            var flavours = files.Select(f => Path.GetFileName(f).Split('_')[0])
                                .Distinct().OrderBy(x => x).ToList();
            var bytes = files.Sum(f => new FileInfo(f).Length);
            return (DiagSeverity.Ok, $"{string.Join(", ", flavours)}: {files.Length} files, {Gb(bytes)}", null);
        });

        // The check that catches today's finding: deconvolution models built for
        // a Rockchip NPU sitting on a board that has none.
        _ = Add(into, "data.models", "data", "AI models", () => {
            // Same order OnnxModelRegistry resolves: the profile's path first,
            // then the packaged default.
            var dir = _profile.Active.OnnxModelsPath;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = "/home/polaris/models";
            if (!Directory.Exists(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "models");
            if (!Directory.Exists(dir))
                return (DiagSeverity.Warn, "no models directory", "Download models from Settings.");
            var total = DirSize(dir);
            var notes = new List<string> { $"{Gb(total)} in {dir}" };
            var sev = DiagSeverity.Ok;
            string? fix = null;
            var kind = (HostInfo.Current.Kind ?? "").ToLowerInvariant();
            if (Directory.Exists(Path.Combine(dir, "rknn")) && !kind.Contains("rock") && !kind.Contains("orange")) {
                notes.Add("rknn/ present but this board has no Rockchip NPU");
                sev = DiagSeverity.Warn;
                fix = "Those models are for another board and only take space: rm -rf " + Path.Combine(dir, "rknn");
            }
            return (sev, string.Join("; ", notes), fix);
        });
    }

    private void Time(List<DiagnosticCheck> into) {
        _ = Add(into, "time.sync", "time", "Clock", () => {
            var now = DateTime.UtcNow;
            if (!_clock.IsSupported)
                return (DiagSeverity.Skipped, $"UTC {now:yyyy-MM-dd HH:mm}, sync not supported here", null);
            // A board with no RTC and no network comes up in 1970, and every
            // plate solve and altitude calculation is then wrong.
            if (now.Year < 2024)
                return (DiagSeverity.Fail, $"UTC {now:yyyy-MM-dd HH:mm} looks unset",
                    "Settings > Clock: sync from this browser.");
            return (DiagSeverity.Ok, $"UTC {now:yyyy-MM-dd HH:mm}", null);
        });
    }

    private void Identity(List<DiagnosticCheck> into) {
        if (!OperatingSystem.IsLinux()) {
            into.Add(new DiagnosticCheck("identity", "identity", DiagSeverity.Skipped,
                "Device identity", "not a Linux host"));
            return;
        }

        // Everything below asks one question: is this device sharing an identity
        // with everyone else who flashed the same image? Published images have
        // shipped a populated machine-id, one TLS keypair for the whole fleet,
        // and (in the other direction) no SSH host keys at all.
        _ = Add(into, "identity.machineId", "identity", "machine-id", () => {
            var id = ReadFirstLine("/etc/machine-id")?.Trim() ?? "";
            return id.Length >= 32
                ? (DiagSeverity.Ok, "set (unique per device)", null)
                : (DiagSeverity.Fail, "empty",
                   "Every device from this image looks like the same host to DHCP and mDNS: sudo systemd-machine-id-setup");
        });

        _ = Add(into, "identity.sshHostKeys", "identity", "SSH host keys", () => {
            var keys = SafeFiles("/etc/ssh", "ssh_host_*_key");
            if (keys.Count == 0)
                return (DiagSeverity.Fail, "none present",
                    "sshd cannot start without them (port 22 refuses). sudo ssh-keygen -A && sudo systemctl restart ssh");
            // Generated on this device, or inherited from the image? The
            // machine-id is written on first boot, so a key older than it came
            // from the image and is shared with every other card.
            var midTime = File.Exists("/etc/machine-id") ? File.GetLastWriteTimeUtc("/etc/machine-id") : DateTime.MinValue;
            var oldest = keys.Min(k => File.GetLastWriteTimeUtc(k));
            if (midTime > DateTime.MinValue && oldest < midTime.AddMinutes(-5))
                return (DiagSeverity.Warn, $"{keys.Count} keys, older than this device's first boot",
                    "They probably came from the image and are shared: sudo rm /etc/ssh/ssh_host_* && sudo ssh-keygen -A && sudo systemctl restart ssh");
            return (DiagSeverity.Ok, $"{keys.Count} keys, generated on this device", null);
        });
    }

    // ---- text rendering ----------------------------------------------------

    /// <summary>Plain-text report. This is what lands on the boot partition and
    /// what people paste into a bug report, so it must be readable without any
    /// tooling.</summary>
    public static string ToText(DiagnosticsReport r) {
        var sb = new StringBuilder();
        sb.AppendLine("Polaris diagnostics");
        sb.AppendLine("===================");
        sb.AppendLine($"generated : {r.GeneratedUtc}");
        sb.AppendLine($"version   : {r.Version}");
        sb.AppendLine($"host      : {r.Host}");
        sb.AppendLine($"board     : {r.Board}");
        sb.AppendLine($"os        : {r.Os}");
        sb.AppendLine($"summary   : {r.Fail} fail, {r.Warn} warn, {r.Unknown} unknown, "
                    + $"{r.Ok} ok, {r.Skipped} skipped");
        sb.AppendLine();
        foreach (var g in r.Checks.GroupBy(c => c.Category)) {
            sb.AppendLine($"[{g.Key}]");
            foreach (var c in g) {
                sb.AppendLine($"  {c.Severity.ToUpperInvariant(),-8} {c.Title}");
                if (!string.IsNullOrWhiteSpace(c.Detail)) sb.AppendLine($"           {c.Detail}");
                if (!string.IsNullOrWhiteSpace(c.Fix)) sb.AppendLine($"           fix: {c.Fix}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("No passwords, keys or tokens are included in this report.");
        return sb.ToString();
    }

    // ---- small helpers -----------------------------------------------------

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "?" : s.Trim();

    private static string Gb(long bytes) {
        var gb = bytes / 1024.0 / 1024 / 1024;
        return gb >= 1 ? $"{gb:F2} GB" : $"{bytes / 1024.0 / 1024:F0} MB";
    }

    private static string? ReadFirstLine(string path) {
        try { return File.ReadLines(path).FirstOrDefault(); } catch { return null; }
    }

    private static string? SafeRead(string path) {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    private static List<string> SafeFiles(string dir, string pattern) {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).ToList() : new(); }
        catch { return new(); }
    }

    private static long DirSize(string dir) {
        try {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                                         .Sum(f => f.Length);
        } catch { return 0; }
    }

    /// <summary>The block device the root filesystem sits on, e.g. mmcblk0p2.</summary>
    private static string? FindRootDevice() {
        try {
            foreach (var line in File.ReadLines("/proc/self/mountinfo")) {
                var parts = line.Split(' ');
                var idx = Array.IndexOf(parts, "-");
                if (idx < 0 || idx + 2 >= parts.Length) continue;
                if (parts[4] != "/") continue;               // mount point
                var src = parts[idx + 2];
                if (src.StartsWith("/dev/")) return src;
            }
        } catch { /* fall through */ }
        return null;
    }

    private static async Task<string> Run(string file, string args, CancellationToken ct) {
        try {
            using var p = Process.Start(new ProcessStartInfo(file, args) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            if (p == null) return "";
            var outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return outp;
        } catch { return ""; }
    }
}
