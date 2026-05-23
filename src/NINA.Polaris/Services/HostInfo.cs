using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services;

/// <summary>
/// Read-once host identification — what kind of machine is Polaris
/// running on. Shown in the activity bar so the user (and remote
/// support) can see at a glance "Raspberry Pi 5 / Linux arm64" vs
/// "Intel NUC11 / Windows 11 x64".
///
/// All detection happens at process start. Hardware doesn't change
/// at runtime; caching the snapshot keeps the WS payload cheap.
///
/// The detection is **best effort**: missing files, locked-down
/// containers, or exotic distros fall through to a generic OS
/// description rather than throwing. Fields are always populated
/// with at least a non-empty fallback so the UI never has to
/// render an empty string.
/// </summary>
public static class HostInfo {
    private static readonly Lazy<HostDeviceInfo> _cached = new(Detect, isThreadSafe: true);

    /// <summary>Detected once on first access, then cached for the
    /// process lifetime. Subsequent reads are free.</summary>
    public static HostDeviceInfo Current => _cached.Value;

    private static HostDeviceInfo Detect() {
        var os = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var kind = "generic";
        var model = "Unknown";

        try {
            if (OperatingSystem.IsLinux()) {
                (kind, model) = DetectLinux();
            } else if (OperatingSystem.IsWindows()) {
                (kind, model) = DetectWindows();
            } else if (OperatingSystem.IsMacOS()) {
                (kind, model) = ("mac", "Apple Mac");
            }
        } catch {
            // Detection is best-effort; don't let a missing file or
            // a locked-down container break the activity bar.
        }

        return new HostDeviceInfo(
            Kind: kind,
            Model: model,
            Os: os,
            Architecture: arch,
            Cores: Math.Max(1, Environment.ProcessorCount),
            ShortLabel: BuildShortLabel(kind, model, arch));
    }

    /// <summary>Linux detection priority:
    /// 1. <c>/proc/device-tree/model</c> — single source of truth on
    ///    Raspberry Pi + most ARM SBCs (NVIDIA Jetson, Rock Pi, ...).
    /// 2. <c>/sys/class/dmi/id/{sys_vendor,product_name}</c> — x86
    ///    standard, populated on basically every modern PC/server/laptop.
    /// 3. <c>/proc/cpuinfo</c> "Model" line — older RPi fallback.
    /// 4. Generic "Linux x64".</summary>
    internal static (string Kind, string Model) DetectLinux() {
        // 1. device-tree
        const string dtPath = "/proc/device-tree/model";
        if (File.Exists(dtPath)) {
            // device-tree files end with a stray null byte
            var dt = File.ReadAllText(dtPath).TrimEnd('\0', ' ', '\n', '\r');
            if (!string.IsNullOrWhiteSpace(dt)) {
                return (ClassifyLinuxModel(dt), dt);
            }
        }

        // 2. DMI (x86)
        var vendor = ReadFirstLine("/sys/class/dmi/id/sys_vendor");
        var product = ReadFirstLine("/sys/class/dmi/id/product_name");
        if (!string.IsNullOrWhiteSpace(product)) {
            var combined = string.IsNullOrWhiteSpace(vendor) || product.Contains(vendor!, StringComparison.OrdinalIgnoreCase)
                ? product!
                : $"{vendor} {product}".Trim();
            return (ClassifyLinuxModel(combined), combined);
        }

        // 3. /proc/cpuinfo Model line (old RPi 1/2/3 firmware)
        try {
            foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                if (line.StartsWith("Model", StringComparison.OrdinalIgnoreCase)) {
                    var idx = line.IndexOf(':');
                    if (idx > 0) {
                        var v = line[(idx + 1)..].Trim();
                        if (!string.IsNullOrWhiteSpace(v)) {
                            return (ClassifyLinuxModel(v), v);
                        }
                    }
                }
            }
        } catch { /* /proc not readable, skip */ }

        return ("linux", $"Linux {RuntimeInformation.OSArchitecture}");
    }

    /// <summary>Windows detection: WMI is the canonical source but
    /// the `System.Management` namespace is Windows-only + adds a
    /// nontrivial dep. <c>ManagementObjectSearcher</c> via
    /// reflection-style access is overkill; instead we shell out to
    /// PowerShell <c>Get-CimInstance Win32_ComputerSystem</c> which
    /// ships in every Windows since 2012. The call runs once at
    /// startup, ~150 ms.</summary>
    internal static (string Kind, string Model) DetectWindows() {
        var manufacturer = QueryWmi("Win32_ComputerSystem", "Manufacturer");
        var model = QueryWmi("Win32_ComputerSystem", "Model");

        // Most desktops/servers report something here. Laptops often
        // report a useful product name in Win32_ComputerSystem.Model
        // (e.g. "Latitude 7420"); for some custom builds Model is
        // generic ("System Product Name") and Manufacturer carries
        // the brand ("ASUS"). Combine when they don't overlap.
        var combined = !string.IsNullOrWhiteSpace(manufacturer) && !string.IsNullOrWhiteSpace(model)
            ? (model!.Contains(manufacturer!, StringComparison.OrdinalIgnoreCase)
                ? model!
                : $"{manufacturer} {model}".Trim())
            : (model ?? manufacturer ?? "Windows PC");

        return (ClassifyWindowsModel(combined), combined);
    }

    /// <summary>Map a verbose model string to a short device kind so
    /// the UI can pick an icon (raspberry / mini-pc / pc / server /
    /// generic). String matching only — no syscalls.</summary>
    internal static string ClassifyLinuxModel(string model) {
        var m = model.ToLowerInvariant();
        if (m.Contains("raspberry pi")) return "raspberry-pi";
        if (m.Contains("jetson")) return "jetson";
        if (m.Contains("rock pi") || m.Contains("rockpi") || m.Contains("rk35")) return "rockpi";
        if (m.Contains("odroid")) return "odroid";
        if (m.Contains("nuc")) return "mini-pc";
        if (m.Contains("nano")) return "mini-pc";
        if (m.Contains("vmware") || m.Contains("virtualbox") || m.Contains("kvm") || m.Contains("qemu")) return "vm";
        return "linux";
    }

    internal static string ClassifyWindowsModel(string model) {
        var m = model.ToLowerInvariant();
        if (m.Contains("nuc")) return "mini-pc";
        if (m.Contains("virtual") || m.Contains("vmware") || m.Contains("hyper-v")) return "vm";
        return "windows";
    }

    /// <summary>Compact one-liner the activity bar shows. Drops the
    /// "Computer/Corporation/Inc." noise some manufacturers stamp and
    /// the trailing "Rev 1.2" silicon revisions that no human cares
    /// about in a status bar.</summary>
    internal static string BuildShortLabel(string kind, string model, string arch) {
        if (string.IsNullOrWhiteSpace(model) || model == "Unknown") return $"Host ({arch})";
        var s = model;
        s = Regex.Replace(s, @"\s+Rev\s+\d+(\.\d+)*", "", RegexOptions.IgnoreCase);
        // Use (?=\s|$) instead of \b because `\b` doesn't trigger
        // between two non-word chars (the trailing "." in "Inc." +
        // following " ") and would otherwise leave a dangling period.
        s = Regex.Replace(s, @"\s+(Inc\.?|Corporation|Computer|Computers|LLC|Ltd\.?|Co\.?\s*Ltd\.?)(?=\s|$)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        // Truncate at 48 chars so a very chatty DMI string doesn't
        // wrap the activity bar.
        return s.Length > 48 ? s[..45] + "..." : s;
    }

    private static string? ReadFirstLine(string path) {
        try {
            if (!File.Exists(path)) return null;
            var s = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        } catch { return null; }
    }

    private static string? QueryWmi(string @class, string property) {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -Command \"(Get-CimInstance {@class}).{property}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(5000)) {
                try { proc.Kill(); } catch { }
                return null;
            }
            var output = proc.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        } catch { return null; }
    }
}

/// <summary>
/// Immutable snapshot of the host's identity. Constructed once at
/// startup via <see cref="HostInfo.Current"/> and serialised into the
/// status WebSocket payload alongside the live metrics so the UI can
/// show a label like "Raspberry Pi 5 Model B Rev 1.0" next to CPU%.
/// </summary>
/// <param name="Kind">Coarse classification for icon selection:
/// raspberry-pi / jetson / rockpi / odroid / mini-pc / vm / windows
/// / linux / mac / generic.</param>
/// <param name="Model">Full model string as reported by hardware.
/// "Raspberry Pi 5 Model B Rev 1.0", "Intel(R) NUC11PAHi7", etc.</param>
/// <param name="Os">Free-form OS description from
/// <see cref="RuntimeInformation.OSDescription"/>.</param>
/// <param name="Architecture">x64 / arm64 / x86 / arm.</param>
/// <param name="Cores">Logical processor count.</param>
/// <param name="ShortLabel">Trimmed label suitable for the activity
/// bar — manufacturer noise and silicon-revision suffixes removed,
/// capped at 48 chars.</param>
public sealed record HostDeviceInfo(
    string Kind,
    string Model,
    string Os,
    string Architecture,
    int Cores,
    string ShortLabel);
