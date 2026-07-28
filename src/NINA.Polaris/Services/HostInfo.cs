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

using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services;

/// <summary>
/// Read-once host identification, what kind of machine is Polaris
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
        string? cpu = null;
        int? cpuMhz = null;

        try {
            if (OperatingSystem.IsLinux()) {
                (kind, model) = DetectLinux();
                cpu = DetectLinuxCpu();
                cpuMhz = DetectLinuxCpuFrequencyMhz();
            } else if (OperatingSystem.IsWindows()) {
                (kind, model) = DetectWindows();
                cpu = NormaliseCpuName(QueryWmi("Win32_Processor", "Name"));
                cpuMhz = ParseInt(QueryWmi("Win32_Processor", "MaxClockSpeed"));
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
            ShortLabel: BuildShortLabel(kind, model, cpu, arch),
            Cpu: cpu,
            CpuFrequencyMhz: cpuMhz,
            CpuLabel: BuildCpuLabel(cpu, cpuMhz, Math.Max(1, Environment.ProcessorCount)));
    }

    /// <summary>Linux detection priority:
    /// 1. <c>/proc/device-tree/model</c>, single source of truth on
    ///    Raspberry Pi + most ARM SBCs (NVIDIA Jetson, Rock Pi, ...).
    /// 2. <c>/sys/class/dmi/id/{sys_vendor,product_name}</c>, x86
    ///    standard, populated on basically every modern PC/server/laptop.
    /// 3. <c>/sys/class/dmi/id/{board_vendor,board_name}</c>,
    ///    motherboard fallback when product_name is an OEM placeholder
    ///    ("System Product Name", "All Series", "Default string", ...).
    /// 4. <c>/proc/cpuinfo</c> "Model" line, older RPi fallback.
    /// 5. CPU model + "Custom build" as last resort.</summary>
    internal static (string Kind, string Model) DetectLinux() {
        // 1. device-tree (RPi, Jetson, Rock Pi, Odroid, ...)
        const string dtPath = "/proc/device-tree/model";
        if (File.Exists(dtPath)) {
            // device-tree files end with a stray null byte
            var dt = File.ReadAllText(dtPath).TrimEnd('\0', ' ', '\n', '\r');
            if (!string.IsNullOrWhiteSpace(dt)) {
                // Some vendor images (e.g. Orange Pi's stock Ubuntu) put only the
                // SoC CODENAME in the model node — "sun60iw2", "rk3588s" — instead
                // of a human board name. When it looks like a bare codename, try
                // /proc/device-tree/compatible, whose "vendor,board" tuples (e.g.
                // "xunlong,orangepi-4-pro") yield the real board.
                if (LooksLikeSocCodename(dt)) {
                    var board = DetectLinuxCompatibleBoard();
                    if (!string.IsNullOrWhiteSpace(board)) {
                        return (ClassifyLinuxModel(board!), board!);
                    }
                }
                return (ClassifyLinuxModel(dt), dt);
            }
        }

        // 2. DMI sys (x86 standard)
        var vendor = ReadFirstLine("/sys/class/dmi/id/sys_vendor");
        var product = ReadFirstLine("/sys/class/dmi/id/product_name");
        var combined = CombineMfgAndModel(vendor, product);
        if (!IsPlaceholderModel(combined)) {
            return (ClassifyLinuxModel(combined!), combined!);
        }

        // 3. DMI board (motherboard), fills in for DIY builds where
        //    sys_vendor/product_name are OEM placeholders
        var boardVendor = ReadFirstLine("/sys/class/dmi/id/board_vendor");
        var boardName = ReadFirstLine("/sys/class/dmi/id/board_name");
        var boardCombined = CombineMfgAndModel(boardVendor, boardName);
        if (!IsPlaceholderModel(boardCombined)) {
            return (ClassifyLinuxModel(boardCombined!), boardCombined!);
        }

        // 4. /proc/cpuinfo "Model" line, old RPi 1/2/3 firmware
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

        // 5. CPU-only fallback for headless servers + DIY builds
        //    with totally empty DMI
        var cpu = DetectLinuxCpu();
        if (!string.IsNullOrWhiteSpace(cpu)) {
            return ("linux", $"Custom build · {cpu}");
        }
        return ("linux", $"Linux {RuntimeInformation.OSArchitecture}");
    }

    /// <summary>Windows detection: WMI is the canonical source but
    /// the `System.Management` namespace is Windows-only + adds a
    /// nontrivial dep. <c>ManagementObjectSearcher</c> via
    /// reflection-style access is overkill; instead we shell out to
    /// PowerShell <c>Get-CimInstance Win32_ComputerSystem</c> which
    /// ships in every Windows since 2012. The call runs once at
    /// startup, ~150 ms per query (we do 2-4 queries).
    ///
    /// Fallback chain for DIY builds where the OEM left
    /// Win32_ComputerSystem.Model as a placeholder
    /// ("System Product Name", "All Series", "To Be Filled By O.E.M.",
    /// etc.):
    /// 1. Win32_ComputerSystem.{Manufacturer,Model}, works for laptops
    ///    + branded desktops + servers + every VM
    /// 2. Win32_BaseBoard.{Manufacturer,Product}, motherboard. For
    ///    a DIY PC this often reads "ASUS PRIME X670-P" / "MSI MAG B650"
    ///    which is what the user actually identifies the build by
    /// 3. CPU model + "Custom build", last resort so the chip is
    ///    never just "ASUS System Product Name"
    /// </summary>
    internal static (string Kind, string Model) DetectWindows() {
        var sysCombined = CombineMfgAndModel(
            QueryWmi("Win32_ComputerSystem", "Manufacturer"),
            QueryWmi("Win32_ComputerSystem", "Model"));

        if (!IsPlaceholderModel(sysCombined)) {
            return (ClassifyWindowsModel(sysCombined!), sysCombined!);
        }

        // Fallback 1: motherboard
        var boardCombined = CombineMfgAndModel(
            QueryWmi("Win32_BaseBoard", "Manufacturer"),
            QueryWmi("Win32_BaseBoard", "Product"));
        if (!IsPlaceholderModel(boardCombined)) {
            return (ClassifyWindowsModel(boardCombined!), boardCombined!);
        }

        // Fallback 2: CPU model
        var cpu = NormaliseCpuName(QueryWmi("Win32_Processor", "Name"));
        if (!string.IsNullOrWhiteSpace(cpu)) {
            return ("windows", $"Custom build · {cpu}");
        }
        return ("windows", "Windows PC");
    }

    /// <summary>Map a verbose model string to a short device kind so
    /// the UI can pick an icon (raspberry / mini-pc / pc / server /
    /// generic). String matching only, no syscalls.</summary>
    internal static string ClassifyLinuxModel(string model) {
        var m = model.ToLowerInvariant();
        if (m.Contains("raspberry pi")) return "raspberry-pi";
        if (m.Contains("jetson")) return "jetson";
        // Orange Pi must come before the rk35 rule below: the Orange Pi 5
        // family is RK3588, which would otherwise be classified as "rockpi".
        if (m.Contains("orange pi") || m.Contains("orangepi")) return "orangepi";
        // Radxa Dragon (Qualcomm QCS6490 / QCM6490) — a Qualcomm SoC board, so it
        // must come BEFORE the rk35/rockpi rule (it's not Rockchip) and is given
        // its own kind for the dragon glyph.
        if ((m.Contains("radxa") && m.Contains("dragon")) || m.Contains("dragon q6")
            || m.Contains("qcs6490") || m.Contains("qcm6490")) return "radxa-dragon";
        if (m.Contains("rock pi") || m.Contains("rockpi") || m.Contains("rk35")) return "rockpi";
        if (m.Contains("odroid")) return "odroid";
        if (m.Contains("nuc")) return "mini-pc";
        if (m.Contains("nano")) return "mini-pc";
        if (m.Contains("vmware") || m.Contains("virtualbox") || m.Contains("kvm") || m.Contains("qemu")) return "vm";
        return "linux";
    }

    /// <summary>True when a device-tree "model" is really a bare SoC codename
    /// (no vendor/board), e.g. "sun60iw2", "sun55iw3", "rk3588", "rk3399",
    /// "s905d3", "h616". Some vendor kernels populate the model node with the
    /// silicon codename instead of a human board name.</summary>
    internal static bool LooksLikeSocCodename(string s) {
        if (string.IsNullOrWhiteSpace(s) || s.Contains(' ')) return false;
        return Regex.IsMatch(s, @"^(sun\d+iw\d+[a-z]?|rk3\d{2,3}[a-z]?|s\d{3}[a-z]?\d?|h\d{2,3}|a\d{2,3}|t\d{3})$",
            RegexOptions.IgnoreCase);
    }

    /// <summary>Extract a friendly board name from the null-separated
    /// "vendor,board" list in <c>/proc/device-tree/compatible</c>
    /// (e.g. "xunlong,orangepi-4-pro\0allwinner,sun60iw2"). Skips pure-silicon
    /// vendor entries (allwinner/rockchip/...) and returns the first
    /// board-vendor tuple beautified. Null if none usable.</summary>
    internal static string? DetectLinuxCompatibleBoard() {
        try {
            const string path = "/proc/device-tree/compatible";
            if (!File.Exists(path)) return null;
            var entries = File.ReadAllText(path).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var e in entries) {
                var comma = e.IndexOf(',');
                if (comma <= 0) continue;
                var vendor = e[..comma].Trim().ToLowerInvariant();
                var board = e[(comma + 1)..].Trim();
                var brand = VendorBrand(vendor);
                if (brand == null || string.IsNullOrWhiteSpace(board)) continue; // silicon vendor -> skip
                return BeautifyBoardSlug(brand, board);
            }
        } catch { /* /proc not readable */ }
        return null;
    }

    /// <summary>Map a device-tree compatible vendor prefix to a consumer brand.
    /// Silicon vendors (allwinner/rockchip/qualcomm/...) return null so the
    /// caller skips their SoC-only compatible entry.</summary>
    internal static string? VendorBrand(string vendor) => vendor switch {
        "xunlong" => "Orange Pi",
        "raspberrypi" => "Raspberry Pi",
        "radxa" => "Radxa",
        "pine64" => "Pine64",
        "friendlyarm" or "friendlyelec" => "FriendlyELEC",
        "hardkernel" => "ODROID",
        "nvidia" => "NVIDIA",
        "libretech" or "libre-computer" => "Libre Computer",
        "bananapi" or "sinovoip" => "Banana Pi",
        "beagle" or "beagleboard" => "BeagleBoard",
        "asus" => "ASUS",
        // silicon vendors -> not a board name
        _ => null
    };

    /// <summary>Turn a compatible board slug ("orangepi-4-pro", "4-model-b")
    /// into a title-cased name, dropping a leading vendor-smashed token and
    /// prefixing the brand: -> "Orange Pi 4 Pro", "Raspberry Pi 4 Model B".</summary>
    internal static string BeautifyBoardSlug(string brand, string slug) {
        var parts = slug.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        var brandKey = brand.Replace(" ", "").ToLowerInvariant();
        // Drop leading tokens that just repeat the brand ("orangepi", "rpi", ...).
        while (parts.Count > 0) {
            var p0 = parts[0].ToLowerInvariant();
            if (p0 == brandKey || (p0.Length >= 4 && brandKey.StartsWith(p0))) parts.RemoveAt(0);
            else break;
        }
        var name = string.Join(" ", parts.Select(TitleToken)).Trim();
        return string.IsNullOrWhiteSpace(name) ? brand : $"{brand} {name}";
    }

    private static string TitleToken(string t) {
        if (t.Length == 0) return t;
        if (t.All(char.IsDigit)) return t;                 // "4", "5"
        return char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant(); // "pro"->"Pro", "b"->"B"
    }

    internal static string ClassifyWindowsModel(string model) {
        var m = model.ToLowerInvariant();
        if (m.Contains("nuc")) return "mini-pc";
        if (m.Contains("virtual") || m.Contains("vmware") || m.Contains("hyper-v")) return "vm";
        return "windows";
    }

    /// <summary>True when the given string is empty, whitespace, or
    /// one of the well-known OEM placeholders that motherboard makers
    /// stamp when the system builder didn't customise SMBIOS strings.
    /// Driving the fallback to motherboard / CPU info in that case.</summary>
    internal static bool IsPlaceholderModel(string? s) {
        if (string.IsNullOrWhiteSpace(s)) return true;
        // A model with no letter in it is not a name. Some mini-PC vendors
        // leave a bare number in product_name, and one of those ("156") reached
        // the activity bar as the device label with nothing to explain it: the
        // user could not tell what the number even was (field report). Falling
        // through to the board name, and then the CPU, always yields something
        // readable. Every genuine model name carries at least one letter.
        if (!s.Any(char.IsLetter)) return true;
        var lower = s.ToLowerInvariant();
        // The full set of OEM placeholders SMBIOS tools complain about.
        // See for example dmidecode's bad-strings list.
        string[] placeholders = [
            "system product name",
            "system manufacturer",
            "system version",
            "system serial number",
            "all series",
            "to be filled by o.e.m.",
            "to be filled by oem",
            "default string",
            "not specified",
            "not available",
            "not applicable",
            "no enclosure",
            "oem",
            "none",
            "unknown",
            "n/a",
        ];
        // Exact match OR the placeholder is the only content (manufacturer
        // sometimes appends garbage like "0x01" to a placeholder).
        foreach (var p in placeholders) {
            if (lower == p) return true;
            if (lower.Contains(p) && lower.Length < p.Length + 20) return true;
        }
        return false;
    }

    /// <summary>Compact one-liner the activity bar shows. Drops the
    /// "Computer/Corporation/Inc." noise some manufacturers stamp and
    /// the trailing "Rev 1.2" silicon revisions that no human cares
    /// about in a status bar.</summary>
    internal static string BuildShortLabel(string kind, string model, string? cpu, string arch) {
        if (string.IsNullOrWhiteSpace(model) || model == "Unknown") {
            return !string.IsNullOrWhiteSpace(cpu) ? cpu : $"Host ({arch})";
        }
        var s = model;
        s = Regex.Replace(s, @"\s+Rev\s+\d+(\.\d+)*", "", RegexOptions.IgnoreCase);
        // Use (?=\s|$) instead of \b because `\b` doesn't trigger
        // between two non-word chars (the trailing "." in "Inc." +
        // following " ") and would otherwise leave a dangling period.
        s = Regex.Replace(s, @"\s+(Inc\.?|Corporation|Computer|Computers|LLC|Ltd\.?|Co\.?\s*Ltd\.?)(?=\s|$)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        // Truncate at 56 chars (was 48), CPU-fallback strings like
        // "Custom build · Intel Core i7-12700K" need a bit more room.
        return s.Length > 56 ? s[..53] + "..." : s;
    }

    /// <summary>Combine a manufacturer + model into a friendly label,
    /// avoiding "ASUS ASUS Prime X670-P" duplication when the model
    /// already includes the brand. Returns null if both empty.</summary>
    internal static string? CombineMfgAndModel(string? mfg, string? model) {
        var m = model?.Trim();
        var v = mfg?.Trim();
        if (string.IsNullOrEmpty(m)) return string.IsNullOrEmpty(v) ? null : v;
        if (string.IsNullOrEmpty(v)) return m;
        return m.Contains(v, StringComparison.OrdinalIgnoreCase) ? m : $"{v} {m}";
    }

    /// <summary>Strip the marketing-y noise out of a CPU brand string.
    /// Intel reports "Intel(R) Core(TM) i7-12700K CPU @ 3.60GHz",
    /// AMD reports "AMD Ryzen 9 7950X 16-Core Processor", neither
    /// adds value past the model number when shown in a status bar.</summary>
    internal static string? NormaliseCpuName(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        s = Regex.Replace(s, @"\(R\)|\(TM\)|\(r\)|\(tm\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+CPU\s*@.*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+\d+-Core\s+Processor", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+Processor$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Format the CPU label shown in its own activity-bar
    /// chip: "<c>Intel Core i7-12700K @ 3.60 GHz · 16 cores</c>" when
    /// everything is available, gracefully dropping pieces when not.
    /// Returns null if there's no CPU info worth showing.</summary>
    internal static string? BuildCpuLabel(string? cpu, int? freqMhz, int cores) {
        if (string.IsNullOrWhiteSpace(cpu) && freqMhz is null or 0 && cores <= 1) return null;
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(cpu)) parts.Add(cpu);
        if (freqMhz is int mhz && mhz > 0) parts.Add($"@ {mhz / 1000.0:F2} GHz");
        if (cores > 1) parts.Add($"· {cores} cores");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Detect CPU max clock speed in MHz. Preferred source is
    /// the cpufreq sysfs (always accurate, accounts for turbo bins);
    /// falls back to /proc/cpuinfo's "cpu MHz" line which reports the
    /// CURRENT scaled clock, close enough for display purposes when
    /// cpufreq isn't exposed (containers, custom kernels). Returns
    /// null on systems where neither source works.</summary>
    internal static int? DetectLinuxCpuFrequencyMhz() {
        try {
            // cpufreq reports kHz; divide by 1000 → MHz
            const string maxFreqPath = "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq";
            if (File.Exists(maxFreqPath)) {
                var kHz = ParseInt(File.ReadAllText(maxFreqPath).Trim());
                if (kHz is > 0) return kHz.Value / 1000;
            }
        } catch { /* sysfs not readable */ }

        try {
            foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                if (line.StartsWith("cpu MHz", StringComparison.OrdinalIgnoreCase)) {
                    var idx = line.IndexOf(':');
                    if (idx > 0) {
                        var v = line[(idx + 1)..].Trim();
                        if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture,
                                            out var mhz)) {
                            return (int)Math.Round(mhz);
                        }
                    }
                }
            }
        } catch { /* /proc not readable */ }
        return null;
    }

    private static int? ParseInt(string? s) {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return int.TryParse(s.Trim(), out var v) ? v : null;
    }

    /// <summary>Read the first "model name" line out of /proc/cpuinfo.
    /// Returns the normalised brand string (or null on x86 systems
    /// where /proc isn't readable, or ARM systems where cpuinfo uses
    /// different field names).</summary>
    internal static string? DetectLinuxCpu() {
        try {
            foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase)) {
                    var idx = line.IndexOf(':');
                    if (idx > 0) return NormaliseCpuName(line[(idx + 1)..]);
                }
            }
        } catch { /* /proc not readable */ }
        return null;
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
/// raspberry-pi / jetson / orangepi / radxa-dragon / rockpi / odroid / mini-pc /
/// vm / windows / linux / mac / generic.</param>
/// <param name="Model">Full model string as reported by hardware.
/// "Raspberry Pi 5 Model B Rev 1.0", "Intel(R) NUC11PAHi7", etc.
/// On DIY builds where the OEM left placeholders in SMBIOS, falls
/// back to the motherboard model and finally to
/// "Custom build · {CpuModel}".</param>
/// <param name="Os">Free-form OS description from
/// <see cref="RuntimeInformation.OSDescription"/>.</param>
/// <param name="Architecture">x64 / arm64 / x86 / arm.</param>
/// <param name="Cores">Logical processor count.</param>
/// <param name="ShortLabel">Trimmed label suitable for the activity
/// bar, manufacturer noise and silicon-revision suffixes removed,
/// capped at 56 chars.</param>
/// <param name="Cpu">Normalised CPU brand string ("Intel Core i7-12700K",
/// "AMD Ryzen 9 7950X", "ARM Cortex-A76") with the vendor noise
/// stripped. Null if not detectable. Useful in the tooltip even when
/// the device chip shows a fancy mini-PC name.</param>
/// <param name="CpuFrequencyMhz">Maximum CPU clock speed in MHz
/// (turbo-aware on Linux via cpufreq sysfs; nominal MaxClockSpeed on
/// Windows via WMI). Null when the source isn't readable.</param>
/// <param name="CpuLabel">Pre-formatted human label combining brand
/// + frequency + cores: "Intel Core i7-12700K @ 3.60 GHz · 20 cores".
/// Null when there's nothing worth showing.</param>
public sealed record HostDeviceInfo(
    string Kind,
    string Model,
    string Os,
    string Architecture,
    int Cores,
    string ShortLabel,
    string? Cpu,
    int? CpuFrequencyMhz,
    string? CpuLabel);