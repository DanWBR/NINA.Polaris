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

// macOS is not supported by the package provider; HostMetricsService still
// needs a monitor so the shared service graph can start for development.
internal sealed class MacResourceMonitor : IResourceMonitor {
    public ResourceUtilization GetUtilization(TimeSpan window) =>
        new(
            cpuUsedPercentage: 0,
            memoryUsedInBytes: 0,
            systemResources: new SystemResources(
                guaranteedCpuUnits: 0,
                maximumCpuUnits: 0,
                guaranteedMemoryInBytes: 0,
                maximumMemoryInBytes: 0));
}

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
    // ---- Host-wide network counters -------------------------------------
    // Cumulative interface byte counts from the previous sample, so the
    // difference gives a rate. Kept here rather than read fresh each time
    // because the OS exposes totals, not rates.
    private long _netRxLast, _netTxLast;
    private DateTime _netLastAt;
    private long _netRxTotal, _netTxTotal;

    /// <summary>Bytes per second across the host's real network interfaces.
    ///
    /// This is the OTHER leg. The browser's own meter counts what crosses
    /// between the page and Polaris, which by design says nothing about what
    /// the host itself pulls from the internet: a 181 MB model pack landing on
    /// the host in seconds showed as a flat 8 KB/s in the UI, and the operator
    /// fairly called it fake news. Now both are visible side by side.
    ///
    /// Honest about what it is: EVERY interface the OS calls up and non-
    /// loopback, so on the usual single-WiFi board it includes this browser's
    /// traffic as well as the host's own. Separating internet from LAN would
    /// need routing-table awareness for a number nobody would trust more.
    /// Loopback is excluded, which matters here: the Canopus proxy and the
    /// INDI server talk over it constantly and would swamp the reading.</summary>
    private (long rx, long tx) SampleNetwork() {
        long rx = 0, tx = 0;
        try {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) {
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var s = nic.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        } catch {
            // Container or sandbox without interface stats: report nothing
            // rather than a fabricated zero rate.
            return (0, 0);
        }

        var now = DateTime.UtcNow;
        if (_netLastAt == default || rx < _netRxLast || tx < _netTxLast) {
            // First sample, or the counters wrapped / the interface set
            // changed. Re-baseline instead of emitting a nonsense spike.
            _netRxLast = rx; _netTxLast = tx; _netLastAt = now;
            return (0, 0);
        }

        var seconds = (now - _netLastAt).TotalSeconds;
        if (seconds <= 0.05) return (0, 0);

        long dRx = rx - _netRxLast, dTx = tx - _netTxLast;
        _netRxTotal += dRx; _netTxTotal += dTx;
        _netRxLast = rx; _netTxLast = tx; _netLastAt = now;
        return ((long)(dRx / seconds), (long)(dTx / seconds));
    }

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
        var (netRx, netTx) = SampleNetwork();

        return new HostMetricsSnapshot {
            NetRxBytesPerSec = netRx,
            NetTxBytesPerSec = netTx,
            NetRxTotalBytes = _netRxTotal,
            NetTxTotalBytes = _netTxTotal,
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
            var full = ResolveSymlinks(Path.GetFullPath(capturePath));

            // THE NUMBERS COME FROM THE KERNEL, NOT FROM THE MOUNT TABLE.
            // On Unix `new DriveInfo(path)` statvfs's that path, so it reports
            // the filesystem the path actually lives on whatever the mount table
            // looks like from here. Deciding the volume by prefix-matching
            // /proc/mounts instead is what produced the field report: a capture
            // root on a 256 GB NVMe showing the 128 GB SD card's numbers, because
            // the match fell through to "/" whenever the NVMe's mountpoint was
            // not visible in this process's mount table under the name the path
            // was written with (a symlinked or bind-mounted path, or a mount that
            // happened after a private-namespace unit started).
            long free = 0, total = 0;
            try {
                var di = new DriveInfo(full);
                if (di.TotalSize > 0) { free = di.AvailableFreeSpace; total = di.TotalSize; }
            } catch { /* probe below */ }

            if (total <= 0) {
                // Windows, or a statvfs that refused: longest-prefix match over
                // the enumerated drives.
                DriveInfo? best = null;
                foreach (var d in DriveInfo.GetDrives()) {
                    if (!d.IsReady) continue;
                    if (full.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase)) {
                        if (best == null || d.Name.Length > best.Name.Length) best = d;
                    }
                }
                if (best == null) return (0, 0, string.Empty);
                return (best.AvailableFreeSpace, best.TotalSize, best.Name);
            }

            // The mount table is now used only to NAME what was measured, and
            // the name has to agree with the measurement to be shown: a
            // mountpoint whose own size differs from the path's is not the
            // volume the path is on, which is exactly the case that used to be
            // reported as fact.
            var (mount, device) = ResolveProcMount(full);
            if (mount != null) {
                try {
                    var mi = new DriveInfo(mount);
                    if (mi.TotalSize == total) {
                        return (free, total, string.IsNullOrEmpty(device) ? mount : $"{mount} ({device})");
                    }
                } catch { /* fall through to the path itself */ }
            }
            return (free, total, full);
        } catch {
            return (0, 0, string.Empty);
        }
    }

    /// <summary>
    /// Best-effort realpath: walks the path from the root and follows every
    /// symlinked component. The mount table records where a filesystem is
    /// attached, so a capture root written through a symlink (a tidy
    /// <c>/mnt/nvme</c> pointing at an automounted directory, say) never matches
    /// it by string. Returns the input unchanged on any failure, since this only
    /// improves the name of a volume that has already been measured.
    /// </summary>
    internal static string ResolveSymlinks(string path) {
        try {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return path;
            var parts = path.Substring(root.Length)
                            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = root;
            foreach (var part in parts) {
                if (part.Length == 0) continue;
                current = Path.Combine(current, part);
                for (int hops = 0; hops < 16; hops++) {
                    var target = Directory.ResolveLinkTarget(current, returnFinalTarget: false)
                                 ?? File.ResolveLinkTarget(current, returnFinalTarget: false);
                    if (target == null) break;
                    var t = target.FullName;
                    current = Path.IsPathRooted(t)
                        ? t
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? root, t));
                }
            }
            return Path.GetFullPath(current);
        } catch {
            return path;
        }
    }

    /// <summary>
    /// Return the longest mountpoint in <c>/proc/mounts</c> that contains
    /// <paramref name="fullPath"/>, with the device backing it (Linux only).
    /// Naming only: the caller measures the path itself and shows this name
    /// solely when the two agree. Null when not on Linux / no match / read
    /// fails.
    /// </summary>
    private static (string? mount, string? device) ResolveProcMount(string fullPath) {
        try {
            if (!File.Exists("/proc/mounts")) return (null, null);
            return ResolveMountFrom(File.ReadLines("/proc/mounts"), fullPath);
        } catch {
            return (null, null);
        }
    }

    /// <summary>Mount-table parsing, split out so it can be tested against a
    /// literal table instead of the machine's own.</summary>
    internal static (string? mount, string? device) ResolveMountFrom(
            IEnumerable<string> mountTable, string fullPath) {
        string? best = null, bestDev = null;
        foreach (var line in mountTable) {
            // Format: "<device> <mountpoint> <fstype> <opts> ...".
            // Spaces in the mountpoint are octal-escaped as \040.
            var parts = line.Split(' ');
            if (parts.Length < 2) continue;
            var mp = parts[1].Replace("\\040", " ");
            if (mp.Length == 0) continue;
            bool contains = mp == "/"
                || fullPath == mp
                || fullPath.StartsWith(mp + "/", StringComparison.Ordinal);
            if (contains && (best == null || mp.Length > best.Length)) {
                best = mp;
                bestDev = parts[0];
            }
        }
        return (best, bestDev);
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
    /// <summary>Host-wide network throughput and session totals, across every
    /// non-loopback interface that is up. The counterpart to the browser's own
    /// meter: that one sees only the page-to-Polaris link, so a download the
    /// HOST makes (model packs, catalogs, an update) was invisible to the
    /// operator. Includes this browser's traffic too on a single-interface
    /// board - see SampleNetwork for why that is the honest scope.</summary>
    public long NetRxBytesPerSec { get; init; }
    public long NetTxBytesPerSec { get; init; }
    public long NetRxTotalBytes { get; init; }
    public long NetTxTotalBytes { get; init; }
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