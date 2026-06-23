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
using Microsoft.Extensions.Diagnostics.ResourceMonitoring;
using Microsoft.Extensions.Hosting;

// IResourceMonitor is marked obsolete in 10.x in favour of
// OpenTelemetry observable instruments. We knowingly use the
// imperative API because it returns a synchronous snapshot the
// activity bar consumes once per second; switching to the OTEL
// path would require an extra subscription + aggregation layer
// for no UX gain. When the API is actually removed (no earlier
// than .NET 12 per the announcement) we revisit.
#pragma warning disable EXTOBS0001

namespace NINA.Polaris.Services;

/// <summary>
/// Background sampler for host-level CPU + memory metrics. Powers the
/// activity bar at the bottom of the UI. Samples every 2 seconds
/// (the minimum window <see cref="IResourceMonitor.GetUtilization"/>
/// needs for a meaningful CPU%); the status WebSocket broadcasts the
/// most recent snapshot at its own 1 Hz cadence (so a snapshot may
/// reach the client twice, harmless, the UI just renders the same
/// value).
///
/// Two CPU numbers are exposed:
///   - <c>CpuPercent</c> = system-wide, all processes combined,
///     normalised to 100% regardless of core count.
///   - <c>ProcessCpuPercent</c> = the Polaris process alone, also
///     normalised to 100% (so the user sees "12%" instead of "192%
///     because Polaris is using two cores fully").
///
/// Memory:
///   - <c>MemoryUsedMB</c> / <c>MemoryTotalMB</c> = system-wide
///     (derived from <see cref="IResourceMonitor"/> percentage and
///     <see cref="GC.GetGCMemoryInfo"/>'s OS-allocated ceiling).
///   - <c>ProcessMemoryMB</c> = Polaris's WorkingSet64.
/// </summary>
public class HostMetricsService : BackgroundService {
    private readonly IResourceMonitor _monitor;
    private readonly ILogger<HostMetricsService> _logger;
    private readonly ProfileService _profiles;
    private readonly FileBrowserService _files;

    /// <summary>Most recent successful sample. Initialised to zeros.</summary>
    public HostMetricsSnapshot Latest { get; private set; } = new();

    /// <summary>Host hardware identification, detected once at
    /// startup, broadcast verbatim in every snapshot so the UI can
    /// label the activity bar.</summary>
    public HostDeviceInfo Device { get; } = HostInfo.Current;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    public HostMetricsService(IResourceMonitor monitor,
                               ProfileService profiles,
                               FileBrowserService files,
                               ILogger<HostMetricsService> logger) {
        _monitor = monitor;
        _profiles = profiles;
        _files = files;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var process = Process.GetCurrentProcess();
        var lastCpuTime = process.TotalProcessorTime;
        var lastSampleTime = DateTime.UtcNow;
        var coreCount = Math.Max(1, Environment.ProcessorCount);

        // First sample skipped, TotalProcessorTime delta needs a
        // reference window, so we wait one interval before the first
        // valid emit.
        await Task.Delay(SampleInterval, stoppingToken);

        // Seed Latest immediately with the device info so the first
        // status broadcast (which may happen before the first sample
        // completes) already carries the host label.
        Latest = Latest with { Device = Device };

        while (!stoppingToken.IsCancellationRequested) {
            try {
                Latest = Sample(process, ref lastCpuTime, ref lastSampleTime, coreCount) with { Device = Device };
            } catch (Exception ex) {
                // Defensive: cgroups not mounted, ResourceMonitor
                // edge cases, or a transient PerformanceCounter
                // hiccup on Windows. Keep the last good Latest so
                // the UI doesn't flash zeros.
                _logger.LogDebug(ex, "HostMetrics sample failed; keeping last good snapshot");
            }
            try {
                await Task.Delay(SampleInterval, stoppingToken);
            } catch (OperationCanceledException) {
                break;
            }
        }
    }

    /// <summary>
    /// Public for unit tests, pulls one snapshot off the monitor
    /// and the current process. Updates the in/out cpu trackers in
    /// place so the caller can call repeatedly.
    /// </summary>
    public HostMetricsSnapshot Sample(Process process,
                                       ref TimeSpan lastCpuTime,
                                       ref DateTime lastSampleTime,
                                       int coreCount) {
        var util = _monitor.GetUtilization(SampleInterval);

        long totalBytes;
        long usedBytes;
        double usedPercent;

        // Prefer /proc/meminfo on Linux. The default IResourceMonitor
        // path counts buff/cache as used memory, which makes a healthy
        // Pi with lots of file cache show 100% RAM and look alarming.
        // /proc/meminfo's MemAvailable subtracts reclaimable pages,
        // matching what `free -h` reports under "available" and what
        // actually matters for memory pressure.
        if (OperatingSystem.IsLinux() && TryReadProcMeminfo(out var procTotal, out var procAvailable)) {
            totalBytes = procTotal;
            usedBytes = Math.Max(0, procTotal - procAvailable);
            usedPercent = procTotal > 0
                ? Math.Min(100.0, 100.0 * usedBytes / procTotal)
                : 0;
        } else {
            // Fallback (Windows, edge cases). GCMemoryInfo carries BOTH the
            // physical-memory ceiling (TotalAvailableMemoryBytes) AND the
            // system-wide memory load (MemoryLoadBytes, sourced from the OS
            // — GlobalMemoryStatusEx on Windows). We must NOT use the
            // IResourceMonitor's MemoryUsedPercentage here: on Windows it is
            // process-scoped (it reported ~Polaris's working set, e.g.
            // "1.2 / 63.8 GB"), which is wrong for a system-memory display.
            var gcInfo = GC.GetGCMemoryInfo();
            totalBytes = gcInfo.TotalAvailableMemoryBytes;
            usedBytes = gcInfo.MemoryLoadBytes;
            if (usedBytes <= 0 || totalBytes <= 0) {
                // GC info not populated yet (no GC has run): fall back to
                // the monitor's percentage so we show *something* sane.
                usedBytes = (long)(totalBytes * util.MemoryUsedPercentage / 100.0);
            }
            usedPercent = totalBytes > 0
                ? Math.Min(100.0, 100.0 * usedBytes / totalBytes)
                : 0;
        }

        var now = DateTime.UtcNow;
        var elapsedMs = (now - lastSampleTime).TotalMilliseconds * coreCount;
        var cpuDeltaMs = (process.TotalProcessorTime - lastCpuTime).TotalMilliseconds;
        var processCpu = elapsedMs > 0
            ? Math.Max(0, Math.Min(100.0, 100.0 * cpuDeltaMs / elapsedMs))
            : 0.0;
        lastCpuTime = process.TotalProcessorTime;
        lastSampleTime = now;

        // Refresh the process snapshot so WorkingSet64 reflects the
        // most recent value instead of the value at Process.GetCurrentProcess().
        process.Refresh();

        // Disk usage on the volume that hosts the STUDIO root (where FILES /
        // STUDIO browse + captures are written). Mirror the exact same
        // resolution the FILES tab uses (ResolveStudioRoot) so the gauge
        // always measures the disk the user actually sees in STUDIO — e.g.
        // an NVMe SSD when ImageOutputDir points there — never the app's
        // install partition. Surfaces free / total in the activity bar so
        // the user notices a full disk before a sequence fails mid-frame.
        string studioRoot;
        try { studioRoot = _files.ResolveStudioRoot(_profiles?.Active?.ImageOutputDir); }
        catch { studioRoot = _profiles?.Active?.ImageOutputDir ?? ""; }
        var (diskFree, diskTotal, diskName) = TryGetDiskInfo(studioRoot);

        // Raspberry Pi under-voltage detection. The Pi VideoCore
        // firmware tracks USB / Vcore voltage and reports state via
        // /sys/class/hwmon/.../in0_lcrit_alarm (raw bit) or the
        // higher-level vcgencmd `get_throttled` flags. We read the
        // sysfs path because it doesn't require shelling out and is
        // available on every Pi that booted normally.
        var (uvNow, uvOccurred) = TryReadPiThrottleState();

        return new HostMetricsSnapshot {
            CpuPercent = Math.Round(util.CpuUsedPercentage, 1),
            MemoryPercent = Math.Round(usedPercent, 1),
            MemoryUsedMB = usedBytes / (1024 * 1024),
            MemoryTotalMB = totalBytes / (1024 * 1024),
            ProcessCpuPercent = Math.Round(processCpu, 1),
            ProcessMemoryMB = process.WorkingSet64 / (1024 * 1024),
            DiskFreeGB = diskTotal > 0 ? Math.Round(diskFree / 1073741824.0, 1) : 0,
            DiskTotalGB = diskTotal > 0 ? Math.Round(diskTotal / 1073741824.0, 1) : 0,
            DiskMountName = diskName,
            UnderVoltageNow = uvNow,
            UnderVoltageOccurred = uvOccurred,
            SampledAt = now
        };
    }

    /// <summary>
    /// Pi-specific under-voltage probe. Reads the cached vcgencmd
    /// get_throttled output (the firmware exposes the same flags
    /// at /sys/devices/platform/soc/.../throttled but that path
    /// varies across kernel versions). Returns (currentlyUnder,
    /// happenedSinceBoot). Both false on non-Pi hardware or when
    /// the vcgencmd binary isn't installed (default false → UI
    /// hides the chip entirely).
    ///
    /// vcgencmd get_throttled returns a 20-bit flag word:
    ///   bit 0  (0x1)     = under-voltage detected RIGHT NOW
    ///   bit 1  (0x2)     = ARM frequency capped now
    ///   bit 2  (0x4)     = currently throttled
    ///   bit 3  (0x8)     = soft temp limit hit now
    ///   bit 16 (0x10000) = under-voltage detected since boot
    ///   bit 17 (0x20000) = ARM freq capped since boot
    ///   bit 18 (0x40000) = throttled since boot
    ///   bit 19 (0x80000) = soft temp limit hit since boot
    ///
    /// We surface bit 0 ("now") and bit 16 ("ever happened") as
    /// the two flags. Operator who sees "ever happened" knows to
    /// add a powered USB hub even if the rail is stable now.
    /// </summary>
    internal static (bool now, bool occurred) TryReadPiThrottleState() {
        // Fast path: skip the subprocess on non-Linux entirely.
        if (!OperatingSystem.IsLinux()) return (false, false);
        try {
            // Capped at 500ms so a hung vcgencmd doesn't stall the
            // sample loop. Should typically return in single-digit ms.
            using var p = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "vcgencmd",
                    Arguments = "get_throttled",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!p.Start()) return (false, false);
            if (!p.WaitForExit(500)) {
                try { p.Kill(); } catch { /* race */ }
                return (false, false);
            }
            if (p.ExitCode != 0) return (false, false);
            // Output: "throttled=0x50005" (or 0x0 etc.)
            var line = p.StandardOutput.ReadToEnd().Trim();
            var idx = line.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return (false, false);
            var hex = line.Substring(idx + 2);
            if (!uint.TryParse(hex,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var flags)) {
                return (false, false);
            }
            return ((flags & 0x1u) != 0, (flags & 0x10000u) != 0);
        } catch {
            // vcgencmd not present (non-Pi Linux), permission denied,
            // process spawn failure -- silently return "no signal".
            return (false, false);
        }
    }

    /// <summary>
    /// Disk usage for the volume that contains <paramref name="capturePath"/>.
    /// Walks <see cref="DriveInfo.GetDrives"/> and returns the longest mount
    /// whose Name is a prefix of the path. This correctly attributes a path
    /// like <c>/mnt/usb-ssd/polaris/files</c> to the USB SSD mount rather
    /// than the root filesystem on Linux. Returns zeros when the path is
    /// empty / unmounted / probe fails so the UI hides the metric instead
    /// of showing a misleading "0 / 0 GB".
    /// </summary>
    internal static (long freeBytes, long totalBytes, string mountName) TryGetDiskInfo(string? capturePath) {
        try {
            // Callers pass the already-resolved STUDIO root (FileBrowserService
            // .ResolveStudioRoot), which always points at an existing dir; this
            // guard only catches an empty/missing path and mirrors that same
            // home-then-CWD fallback so the gauge measures the STUDIO partition,
            // not the app/root filesystem.
            if (string.IsNullOrWhiteSpace(capturePath)
                || !Directory.Exists(Path.GetFullPath(capturePath))) {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                capturePath = (!string.IsNullOrEmpty(home) && Directory.Exists(home))
                    ? home : Environment.CurrentDirectory;
            }
            var full = Path.GetFullPath(capturePath);

            // Primary (Linux): resolve the containing mount from /proc/mounts —
            // it lists EVERY mount, unlike DriveInfo.GetDrives() which can omit
            // one like an NVMe at /mnt/nvme. That omission was the bug: the gauge
            // kept reporting the SD card's "/" (128 GB) while images saved to the
            // NVMe (256 GB). Pick the longest mountpoint that contains the path,
            // then statvfs it for the real free/total.
            var procMount = ResolveProcMount(full);
            if (procMount != null) {
                try {
                    var di = new DriveInfo(procMount);
                    if (di.TotalSize > 0)
                        return (di.AvailableFreeSpace, di.TotalSize, procMount);
                } catch { /* fall through */ }
            }

            // Cross-platform: statvfs the path directly. On Unix `new
            // DriveInfo(path)` resolves the filesystem containing the path.
            try {
                var di = new DriveInfo(full);
                if (di.TotalSize > 0)
                    return (di.AvailableFreeSpace, di.TotalSize, di.Name);
            } catch { /* fall through to enumeration */ }

            // Fallback: longest-prefix match over the enumerated drives.
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives()) {
                if (!d.IsReady) continue;
                if (full.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase)) {
                    if (best == null || d.Name.Length > best.Name.Length) {
                        best = d;
                    }
                }
            }
            if (best == null) return (0, 0, string.Empty);
            return (best.AvailableFreeSpace, best.TotalSize, best.Name);
        } catch {
            return (0, 0, string.Empty);
        }
    }

    /// <summary>
    /// Return the longest mountpoint in <c>/proc/mounts</c> that contains
    /// <paramref name="fullPath"/> (Linux only). Reads the kernel mount table
    /// directly because <see cref="DriveInfo.GetDrives"/> can omit real mounts
    /// (e.g. an NVMe at /mnt/nvme), which made the disk gauge attribute the path
    /// to "/" instead. Null when not on Linux / no match / read fails.
    /// </summary>
    private static string? ResolveProcMount(string fullPath) {
        try {
            if (!File.Exists("/proc/mounts")) return null;
            string? best = null;
            foreach (var line in File.ReadLines("/proc/mounts")) {
                // Format: "<device> <mountpoint> <fstype> <opts> ...".
                // Spaces in the mountpoint are octal-escaped as \040.
                var parts = line.Split(' ');
                if (parts.Length < 2) continue;
                var mp = parts[1].Replace("\\040", " ");
                if (mp.Length == 0) continue;
                bool contains = mp == "/"
                    ? true
                    : (fullPath == mp || fullPath.StartsWith(mp + "/", StringComparison.Ordinal));
                if (contains && (best == null || mp.Length > best.Length)) best = mp;
            }
            return best;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Parse the two lines we care about out of /proc/meminfo. Returns
    /// false on any IO error or unparseable line so the caller can fall
    /// back to the cross-platform path.
    /// </summary>
    internal static bool TryReadProcMeminfo(out long totalBytes, out long availableBytes) {
        totalBytes = 0;
        availableBytes = 0;
        try {
            foreach (var line in File.ReadLines("/proc/meminfo")) {
                if (totalBytes == 0 && line.StartsWith("MemTotal:", StringComparison.Ordinal)) {
                    totalBytes = ParseKibLine(line);
                } else if (availableBytes == 0 && line.StartsWith("MemAvailable:", StringComparison.Ordinal)) {
                    availableBytes = ParseKibLine(line);
                }
                if (totalBytes > 0 && availableBytes > 0) break;
            }
        } catch {
            return false;
        }
        return totalBytes > 0 && availableBytes > 0;
    }

    private static long ParseKibLine(string line) {
        // Format: "MemTotal:        4015896 kB"
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb)) {
            return kb * 1024L;
        }
        return 0;
    }
}

/// <summary>
/// Immutable snapshot of host metrics. Serialised verbatim into the
/// status WebSocket payload. All numbers rounded to 1 decimal so
/// the UI doesn't display jittery sub-percent values.
/// </summary>
public sealed record HostMetricsSnapshot {
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public long MemoryUsedMB { get; init; }
    public long MemoryTotalMB { get; init; }
    public double ProcessCpuPercent { get; init; }
    public long ProcessMemoryMB { get; init; }
    /// <summary>Free + total bytes on the volume hosting the active rig's
    /// capture root. GB-rounded (1 decimal) since the activity bar shows
    /// "234.5 / 931.5 GB" not byte-precise. Zero on both axes = probe
    /// failed (no rig, unmounted path, sandbox) → UI hides the chip.</summary>
    public double DiskFreeGB { get; init; }
    public double DiskTotalGB { get; init; }
    /// <summary>Mount name of the volume above ("C:\" on Windows, "/" or
    /// "/mnt/usb-ssd" on Linux). Tooltip context so the user knows which
    /// disk they are reading the free-space gauge for.</summary>
    public string DiskMountName { get; init; } = string.Empty;
    /// <summary>True when the Pi's voltage monitor is reporting
    /// under-voltage right now (bit 0 of vcgencmd get_throttled).
    /// Drives a red chip on the activity bar -- ANY recurring
    /// under-voltage is a strong predictor of imminent USB device
    /// crashes. Always false on non-Pi hardware.</summary>
    public bool UnderVoltageNow { get; init; }
    /// <summary>True when under-voltage has been detected at any
    /// point since the Pi booted (bit 16 of vcgencmd get_throttled).
    /// Doesn't clear until reboot, so we surface it as a softer
    /// amber chip to advise "you may need a powered USB hub or a
    /// better PSU" even if the rail is currently stable.</summary>
    public bool UnderVoltageOccurred { get; init; }
    public DateTime SampledAt { get; init; }

    /// <summary>Host hardware identification, same instance is shared
    /// across every snapshot (detection is one-shot at startup). Null
    /// before the first <see cref="HostMetricsService.ExecuteAsync"/>
    /// tick runs.</summary>
    public HostDeviceInfo? Device { get; init; }
}