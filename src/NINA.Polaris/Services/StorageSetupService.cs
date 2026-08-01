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
/// STORAGE-1: find a data disk worth putting captures on, and put them there.
///
/// <para>The appliance images keep captures on the ROOT filesystem, which is
/// how a 4.10 GB planetary clip filled a 57 GB card and took the host down with
/// it (field, 2026-07-31). An SSD solves that, but only if plugging it in
/// leads somewhere: an operator who has to learn fstab first will keep
/// recording onto the system card.</para>
///
/// <para>NON-DESTRUCTIVE. This service reports what is already there and can
/// mount a filesystem that ALREADY EXISTS. It never partitions and never
/// formats; a disk with no filesystem is reported as such and left alone. The
/// privileged half runs in polaris-storage-prep.service, started through the
/// same PolicyKit manage-units rule the self-update uses.</para>
/// </summary>
public class StorageSetupService {
    private const string StageDir = "/home/polaris/.cache/polaris-storage";
    private const string Unit = "polaris-storage-prep.service";
    private const string LogPath = "/tmp/polaris-storage.log";
    private const string MountPrefix = "/mnt/polaris-";

    private readonly ProfileService _profiles;
    private readonly ILogger<StorageSetupService> _logger;

    public StorageSetupService(ProfileService profiles, ILogger<StorageSetupService> logger) {
        _profiles = profiles;
        _logger = logger;
    }

    /// <summary>A filesystem that could hold captures.</summary>
    /// <param name="Device">e.g. /dev/nvme0n1p1</param>
    /// <param name="Uuid">stable identity; what fstab is keyed on</param>
    /// <param name="MountPoint">empty when not mounted</param>
    /// <param name="Removable">USB enclosure and friends</param>
    public record Candidate(string Device, string Uuid, string FsType, string Label,
                            long SizeBytes, string Model, string MountPoint,
                            bool Removable, bool OnBootDisk);

    public record Survey(bool Supported, string? Reason, string CaptureRoot,
                         bool CaptureRootOnBootDisk, IReadOnlyList<Candidate> Candidates);

    /// <summary>What is plugged in, and whether the captures are still on the
    /// system disk. The second half is what decides if there is anything worth
    /// suggesting: a host already writing to an SSD needs no card.</summary>
    public Survey Look() {
        if (!OperatingSystem.IsLinux())
            return new(false, "Capture-disk setup is Linux only.", CaptureRoot(), false, Array.Empty<Candidate>());

        var rootSrc = Run("findmnt", "-no SOURCE /").Trim();
        var bootDisk = string.IsNullOrEmpty(rootSrc) ? "" : Run("lsblk", $"-no PKNAME {rootSrc}").Trim();
        if (string.IsNullOrWhiteSpace(bootDisk))
            return new(false, "Cannot tell which disk the system runs from.", CaptureRoot(), false,
                       Array.Empty<Candidate>());

        var list = new List<Candidate>();
        // -P gives KEY="value" pairs, which survive spaces in a model name;
        // parsing columns by position does not.
        foreach (var line in Run("lsblk",
                     "-P -o NAME,PKNAME,TYPE,SIZE,FSTYPE,UUID,LABEL,MOUNTPOINT,RM,MODEL").Split('\n')) {
            var kv = ParsePairs(line);
            if (kv.Count == 0) continue;
            if (Get(kv, "TYPE") is not ("part" or "disk")) continue;
            var fstype = Get(kv, "FSTYPE");
            var uuid = Get(kv, "UUID");
            // No filesystem, no UUID, nothing to mount. Reported nowhere on
            // purpose: offering to "set up" a blank disk would imply this tool
            // can format it, and it deliberately cannot.
            if (string.IsNullOrEmpty(fstype) || string.IsNullOrEmpty(uuid)) continue;
            // Skip the system's own filesystems and anything already in use by
            // the OS: swap, EFI, and the root itself.
            if (fstype is "swap" or "vfat" && Get(kv, "MOUNTPOINT").StartsWith("/boot")) continue;
            var name = Get(kv, "NAME");
            if (name.StartsWith("loop") || name.StartsWith("zram") || name.StartsWith("sr")) continue;

            var parent = Get(kv, "PKNAME");
            bool onBoot = parent == bootDisk || name == bootDisk;
            var mount = Get(kv, "MOUNTPOINT");
            if (mount is "/" or "/boot" or "/boot/efi" or "[SWAP]") continue;

            list.Add(new Candidate(
                Device: "/dev/" + name,
                Uuid: uuid,
                FsType: fstype,
                Label: Get(kv, "LABEL"),
                SizeBytes: ParseSize(Get(kv, "SIZE")),
                Model: Get(kv, "MODEL"),
                MountPoint: mount,
                Removable: Get(kv, "RM") == "1",
                OnBootDisk: onBoot));
        }

        var root = CaptureRoot();
        return new(true, null, root, IsOnBootDisk(root, bootDisk),
                   list.Where(c => !c.OnBootDisk).ToList());
    }

    /// <summary>True when the capture root lives on the same disk the system
    /// boots from, which is the condition that makes a data disk worth
    /// suggesting at all.</summary>
    private bool IsOnBootDisk(string path, string bootDisk) {
        try {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path)) return false;
            var src = Run("findmnt", $"-no SOURCE --target {path}").Trim();
            if (string.IsNullOrEmpty(src)) return false;
            var disk = Run("lsblk", $"-no PKNAME {src}").Trim();
            return !string.IsNullOrEmpty(disk) && disk == bootDisk;
        } catch { return false; }
    }

    private string CaptureRoot() => _profiles.Active?.ImageOutputDir ?? "";

    public record PrepareResult(bool Ok, string? Error, string? MountPoint, string? CaptureDir, string Log);

    /// <summary>Mount the chosen filesystem and, once the mount is PROVEN, move
    /// the capture root onto it.
    ///
    /// <para>The order matters and is the whole point: pointing the profile at
    /// a path that was never mounted would send frames into an empty directory
    /// on the root filesystem, which looks like it worked right up until the
    /// card fills.</para></summary>
    public async Task<PrepareResult> PrepareAsync(string uuid, bool moveExisting, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(uuid) || uuid.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
            return new(false, "A filesystem UUID is required.", null, null, "");

        var survey = Look();
        if (!survey.Supported) return new(false, survey.Reason, null, null, "");
        var target = survey.Candidates.FirstOrDefault(c => c.Uuid == uuid);
        if (target == null) return new(false, "That filesystem is not present.", null, null, "");
        if (target.OnBootDisk) return new(false, "That filesystem is on the boot disk.", null, null, "");

        var mount = MountPrefix + ShortId(uuid);
        try {
            Directory.CreateDirectory(StageDir);
            await File.WriteAllTextAsync(Path.Combine(StageDir, "request"),
                $"UUID={uuid} MOUNT={mount}\n", ct);
        } catch (Exception ex) {
            return new(false, "Could not stage the request: " + ex.Message, null, null, "");
        }

        _logger.LogWarning("Storage setup: mounting {Dev} (UUID={Uuid}) at {Mount}",
            target.Device, uuid, mount);
        var (started, startErr) = await StartUnitAsync(ct);
        var log = ReadLog();
        if (!started) return new(false, startErr, null, null, log);

        // Trust the kernel, not the exit code: the script may have reported
        // success and the mount still be absent (a race, a disk that vanished).
        var mounted = Run("findmnt", $"-no TARGET {mount}").Trim();
        if (mounted != mount)
            return new(false, "The disk did not end up mounted; the capture root was left alone.",
                       null, null, log);

        var captureDir = Path.Combine(mount, "files");
        if (!Directory.Exists(captureDir))
            return new(false, "The mount succeeded but the capture directory is missing.",
                       mount, null, log);

        var previous = CaptureRoot();
        _profiles.UpdateSettings(p => p.ImageOutputDir = captureDir);
        _logger.LogWarning("Capture root moved: {Old} -> {New}", previous, captureDir);

        if (moveExisting && !string.IsNullOrEmpty(previous) && Directory.Exists(previous)) {
            // Deliberately NOT automatic: copying tens of gigabytes over a slow
            // card is a long, interruptible operation, and doing it silently
            // behind a "set up disk" button is how a session dies. The caller
            // asks for it explicitly, and even then it is a background copy the
            // operator can watch in the log.
            _ = Task.Run(() => CopyTree(previous, captureDir), CancellationToken.None);
        }

        return new(true, null, mount, captureDir, log);
    }

    private async Task<(bool ok, string? error)> StartUnitAsync(CancellationToken ct) {
        try {
            var psi = new ProcessStartInfo("systemctl") {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            psi.ArgumentList.Add("start");
            psi.ArgumentList.Add(Unit);
            using var p = Process.Start(psi);
            if (p == null) return (false, "Could not start " + Unit);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) {
                var err = (await p.StandardError.ReadToEndAsync(ct)).Trim();
                return (false, string.IsNullOrEmpty(err)
                    ? $"{Unit} failed (exit {p.ExitCode})" : err);
            }
            return (true, null);
        } catch (Exception ex) {
            return (false, ex.Message);
        }
    }

    private static string ReadLog() {
        try { return File.Exists(LogPath) ? File.ReadAllText(LogPath) : ""; } catch { return ""; }
    }

    /// <summary>Copy, never move: the originals stay until the operator is
    /// satisfied, because a half-finished move of a night's data has no undo.
    /// </summary>
    private void CopyTree(string from, string to) {
        try {
            long files = 0;
            foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) {
                var rel = Path.GetRelativePath(from, src);
                var dst = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst)) { File.Copy(src, dst); files++; }
            }
            _logger.LogInformation("Copied {N} file(s) from {From} to {To}; the originals were kept",
                files, from, to);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Copying the existing captures failed; the originals are untouched");
        }
    }

    private static string ShortId(string uuid) =>
        new string(uuid.Where(char.IsLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();

    private static Dictionary<string, string> ParsePairs(string line) {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < line.Length) {
            int eq = line.IndexOf('=', i);
            if (eq < 0) break;
            var key = line[i..eq].Trim();
            int q1 = line.IndexOf('"', eq);
            if (q1 < 0) break;
            int q2 = line.IndexOf('"', q1 + 1);
            if (q2 < 0) break;
            d[key] = line[(q1 + 1)..q2];
            i = q2 + 1;
        }
        return d;
    }

    private static string Get(Dictionary<string, string> kv, string key) =>
        kv.TryGetValue(key, out var v) ? v.Trim() : "";

    /// <summary>lsblk SIZE is human ("931.5G"). Approximate is fine: this
    /// number is shown to a person choosing between two disks.</summary>
    internal static long ParseSize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        char unit = char.ToUpperInvariant(s[^1]);
        if (char.IsDigit(unit)) return long.TryParse(s, out var raw) ? raw : 0;
        if (!double.TryParse(s[..^1].Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return unit switch {
            'K' => (long)(v * 1024),
            'M' => (long)(v * 1024 * 1024),
            'G' => (long)(v * 1024L * 1024 * 1024),
            'T' => (long)(v * 1024L * 1024 * 1024 * 1024),
            _ => (long)v
        };
    }

    private static string Run(string file, string args) {
        try {
            var psi = new ProcessStartInfo(file, args) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var s = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return s;
        } catch { return ""; }
    }
}
