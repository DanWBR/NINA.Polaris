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
/// <para>Two paths. Mounting a filesystem that ALREADY EXISTS is
/// non-destructive and is what a disk arriving pre-formatted gets. STORAGE-2
/// adds the other one: partitioning and formatting a blank disk, because the
/// v1 quietly assumed the operator could run sfdisk and mkfs from a Linux
/// shell, and most cannot. That path ERASES A DISK, so it is gated on an
/// explicit confirmation plus an identity check (the disk's serial, re-read at
/// the moment of the format) and re-verified independently by the privileged
/// script.</para>
///
/// <para>The privileged half runs in polaris-storage-prep.service, started
/// through its OWN PolicyKit rule (50-polaris-storage.rules). The v1 claimed
/// the self-update's rule already covered it; it does not, because that rule
/// scopes its grant to <c>action.lookup("unit") == "polaris-self-update.service"</c>
/// and modern polkit ignores the broad .pkla twin. Every attempt to set up a
/// disk came back "Interactive authentication required".</para>
/// </summary>
public class StorageSetupService {
    private const string StageDir = "/home/polaris/.cache/polaris-storage";
    private const string Unit = "polaris-storage-prep.service";
    private const string LogPath = "/tmp/polaris-storage.log";
    private const string MountPrefix = "/mnt/polaris-";
    /// <summary>Mirrors MIN_FORMAT_BYTES in polaris-storage-prep.sh. A blank
    /// disk below this is far likelier to be a card reader than a data disk.
    /// </summary>
    private const long MinFormatBytes = 8L * 1024 * 1024 * 1024;

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

    /// <summary>A whole disk that could be erased and set up. STORAGE-2.</summary>
    /// <param name="Serial">the disk's own identity, and what the format
    /// request is checked against. A device node is a position, not a disk:
    /// /dev/sdb is a different disk after a reboot with two enclosures
    /// plugged in.</param>
    /// <param name="InUse">something on this disk is mounted right now</param>
    /// <param name="Contents">what would be destroyed, in words, so the
    /// confirmation can say it out loud instead of asking for trust</param>
    /// <param name="Blank">no partitions and no filesystem: the ordinary case
    /// of a brand-new SSD, and the only one we suggest unprompted</param>
    public record FormattableDisk(string Device, string Model, string Serial,
                                  long SizeBytes, bool Removable, bool InUse,
                                  bool Blank, string Contents);

    public record Survey(bool Supported, string? Reason, string CaptureRoot,
                         bool CaptureRootOnBootDisk, IReadOnlyList<Candidate> Candidates,
                         IReadOnlyList<FormattableDisk> Formattable);

    /// <summary>What is plugged in, and whether the captures are still on the
    /// system disk. The second half is what decides if there is anything worth
    /// suggesting: a host already writing to an SSD needs no card.</summary>
    public Survey Look() {
        if (!OperatingSystem.IsLinux())
            return new(false, "Capture-disk setup is Linux only.", CaptureRoot(), false,
                       Array.Empty<Candidate>(), Array.Empty<FormattableDisk>());

        var rootSrc = Run("findmnt", "-no SOURCE /").Trim();
        var bootDisk = string.IsNullOrEmpty(rootSrc) ? "" : Run("lsblk", $"-no PKNAME {rootSrc}").Trim();
        if (string.IsNullOrWhiteSpace(bootDisk))
            return new(false, "Cannot tell which disk the system runs from.", CaptureRoot(), false,
                       Array.Empty<Candidate>(), Array.Empty<FormattableDisk>());

        // -P gives KEY="value" pairs, which survive spaces in a model name;
        // parsing columns by position does not. -b gives SIZE in bytes, so the
        // number that gates a format is exact rather than re-parsed from "931.5G".
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in Run("lsblk",
                     "-P -b -o NAME,PKNAME,TYPE,SIZE,FSTYPE,UUID,LABEL,MOUNTPOINT,RM,MODEL,SERIAL")
                     .Split('\n')) {
            var kv = ParsePairs(line);
            if (kv.Count > 0) rows.Add(kv);
        }

        var list = new List<Candidate>();
        foreach (var kv in rows) {
            if (Get(kv, "TYPE") is not ("part" or "disk")) continue;
            var fstype = Get(kv, "FSTYPE");
            var uuid = Get(kv, "UUID");
            // No filesystem, no UUID, nothing to MOUNT. Such a disk is not
            // dropped any more: it comes back below as something to format.
            if (string.IsNullOrEmpty(fstype) || string.IsNullOrEmpty(uuid)) continue;
            // Skip the system's own filesystems and anything already in use by
            // the OS: swap, EFI, and the root itself.
            if (fstype is "swap" or "vfat" && Get(kv, "MOUNTPOINT").StartsWith("/boot")) continue;
            var name = Get(kv, "NAME");
            if (IsPseudo(name)) continue;

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
        // Which filesystem the captures currently land on. A host that was set
        // up months ago answers with the very disk the card would offer to
        // mount, so this is what keeps it quiet. Empty when the capture root is
        // unset or the path cannot be resolved.
        var rootSource = string.IsNullOrWhiteSpace(root)
            ? "" : Run("findmnt", $"-no SOURCE --target {root}").Trim();
        return new(true, null, root, IsOnBootDisk(root, bootDisk),
                   Offerable(list, rootSource),
                   Formattable(rows, bootDisk));
    }

    /// <summary>Candidates worth SUGGESTING.
    ///
    /// <para>Two exclusions, both of which produced a card that made no sense.
    /// The boot disk is never a capture disk. And the disk the captures are
    /// already written to is not something to offer to mount: it is mounted,
    /// that is how the frames are getting there. Field report 2026-08-08, a
    /// fully configured host offering to set its own NVMe up again.</para>
    ///
    /// <para><paramref name="captureRootSource"/> is the device node
    /// <c>findmnt --target</c> reports for the capture root, so a candidate is
    /// matched by the filesystem actually in use rather than by a mount point
    /// string that may be a parent, a symlink, or a bind.</para></summary>
    internal static List<Candidate> Offerable(
            IEnumerable<Candidate> all, string captureRootSource) {
        var host = (captureRootSource ?? "").Trim();
        return all.Where(c => !c.OnBootDisk
                           && (host.Length == 0
                               || !string.Equals(c.Device, host, StringComparison.Ordinal)))
                  .ToList();
    }

    /// <summary>Whole disks that could be erased and set up. STORAGE-2.</summary>
    private static List<FormattableDisk> Formattable(
            List<Dictionary<string, string>> rows, string bootDisk) {
        var disks = new List<FormattableDisk>();
        foreach (var kv in rows) {
            if (Get(kv, "TYPE") != "disk") continue;
            var name = Get(kv, "NAME");
            if (IsPseudo(name) || name == bootDisk) continue;

            var size = ParseSize(Get(kv, "SIZE"));
            if (size < MinFormatBytes) continue;

            // Everything the kernel says lives on this disk: the disk row
            // itself plus every partition whose PKNAME points back at it.
            var children = rows.Where(r => Get(r, "PKNAME") == name).ToList();
            var inUse = !string.IsNullOrEmpty(Get(kv, "MOUNTPOINT"))
                        || children.Any(r => !string.IsNullOrEmpty(Get(r, "MOUNTPOINT")));
            var blank = children.Count == 0 && string.IsNullOrEmpty(Get(kv, "FSTYPE"));

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Get(kv, "FSTYPE")))
                parts.Add(Describe(Get(kv, "FSTYPE"), Get(kv, "LABEL"), ParseSize(Get(kv, "SIZE"))));
            foreach (var ch in children)
                parts.Add(Describe(Get(ch, "FSTYPE"), Get(ch, "LABEL"), ParseSize(Get(ch, "SIZE"))));

            disks.Add(new FormattableDisk(
                Device: "/dev/" + name,
                Model: Get(kv, "MODEL"),
                Serial: Get(kv, "SERIAL"),
                SizeBytes: size,
                Removable: Get(kv, "RM") == "1",
                InUse: inUse,
                Blank: blank,
                Contents: parts.Count == 0 ? "" : string.Join(", ", parts)));
        }
        return disks;
    }

    private static string Describe(string fstype, string label, long size) {
        var what = string.IsNullOrEmpty(fstype) ? "unformatted" : fstype;
        if (!string.IsNullOrEmpty(label)) what += $" \"{label}\"";
        return size > 0 ? $"{what} ({Human(size)})" : what;
    }

    private static string Human(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.#} TB"
        : bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.#} GB"
        : $"{bytes / (double)(1 << 20):0.#} MB";

    private static bool IsPseudo(string name) =>
        name.StartsWith("loop") || name.StartsWith("zram") || name.StartsWith("sr")
        || name.StartsWith("ram") || name.StartsWith("dm-");

    /// <summary>True when the capture root lives on the same disk the system
    /// boots from, which is the condition that makes a data disk worth
    /// suggesting at all.</summary>
    private bool IsOnBootDisk(string path, string bootDisk) {
        try {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path)) return false;
            var src = Run("findmnt", $"-no SOURCE --target {path}").Trim();
            if (string.IsNullOrEmpty(src)) return false;
            var disk = Run("lsblk", $"-no PKNAME {src}").Trim();
            // A filesystem written straight to a whole disk (no partition
            // table, which is how a hand-formatted NVMe often ends up) has no
            // parent, so PKNAME is empty and the disk IS the node. The
            // candidate loop already handles that case via `name == bootDisk`;
            // without the same fallback here the two disagree.
            if (string.IsNullOrEmpty(disk)) disk = Run("lsblk", $"-no KNAME {src}").Trim();
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
        return await StageAndRunAsync($"UUID={uuid} MOUNT={mount}", mount, moveExisting,
            $"mounting {target.Device} (UUID={uuid}) at {mount}", ct);
    }

    /// <summary>STORAGE-2: erase a whole disk, put one ext4 filesystem on it,
    /// and mount that as the capture disk.
    ///
    /// <para>The v1 could only mount a filesystem that already existed, which
    /// assumed the operator could partition and mkfs an NVMe from a Linux
    /// shell. That is a fair thing to expect of nobody.</para>
    ///
    /// <para><paramref name="serial"/> is the guard that matters. The client
    /// echoes back the identity it was shown, and the disk at that device node
    /// has to still be that disk. A device node is a position, not a disk: if
    /// something was replugged between the survey and the confirmation,
    /// /dev/sdb is now somebody else and this refuses instead of erasing
    /// it.</para></summary>
    public async Task<PrepareResult> FormatAsync(string device, string serial, string confirm,
                                                 bool moveExisting, CancellationToken ct) {
        // A sentinel, not a boolean: a truncated or replayed body cannot spell
        // it by accident, and it reads unmistakably at the call site.
        if (confirm != "ERASE")
            return new(false, "This erases the disk and needs an explicit confirmation.", null, null, "");
        if (string.IsNullOrWhiteSpace(device) || !IsWholeDiskNode(device))
            return new(false, "That is not a whole-disk device.", null, null, "");

        var survey = Look();
        if (!survey.Supported) return new(false, survey.Reason, null, null, "");

        var disk = survey.Formattable.FirstOrDefault(d => d.Device == device);
        if (disk == null)
            return new(false, "That disk is no longer present.", null, null, "");
        if (disk.InUse)
            return new(false, "Something on that disk is mounted; unmount it first.", null, null, "");
        // Serial when the disk has one; size is the fallback for the USB
        // bridges that report none, and is far weaker (two identical disks
        // match), which is why it is only the fallback.
        var expected = string.IsNullOrEmpty(disk.Serial)
            ? disk.SizeBytes.ToString() : disk.Serial;
        if (serial != expected)
            return new(false, "The disk at that slot is not the one you confirmed; nothing was erased.",
                       null, null, "");

        var mount = MountPrefix + ShortId(Guid.NewGuid().ToString("N"));
        _logger.LogWarning("Storage setup: ERASING {Dev} ({Model}, {Size} bytes, contents: {Contents})",
            disk.Device, disk.Model, disk.SizeBytes,
            string.IsNullOrEmpty(disk.Contents) ? "empty" : disk.Contents);
        return await StageAndRunAsync(
            $"ACTION=format DEV={device} MOUNT={mount} CONFIRM=ERASE", mount, moveExisting,
            $"formatting {device} and mounting it at {mount}", ct);
    }

    /// <summary>Stage the request, run the privileged unit, and only point the
    /// capture root at the result once the kernel confirms the mount. Shared by
    /// both actions because the half that can go quietly wrong is the same.
    /// </summary>
    private async Task<PrepareResult> StageAndRunAsync(string request, string mount,
                                                       bool moveExisting, string what,
                                                       CancellationToken ct) {
        try {
            Directory.CreateDirectory(StageDir);
            await File.WriteAllTextAsync(Path.Combine(StageDir, "request"), request + "\n", ct);
        } catch (Exception ex) {
            return new(false, "Could not stage the request: " + ex.Message, null, null, "");
        }

        _logger.LogWarning("Storage setup: {What}", what);
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

    /// <summary>A whole disk, spelled exactly: /dev/nvme0n1, /dev/sda,
    /// /dev/mmcblk0. Not a partition (formatting a disk repartitions it), not a
    /// path with any traversal or shell metacharacters in it. Mirrors the case
    /// statement in polaris-storage-prep.sh; both exist because the script is
    /// what actually holds the erase and must not trust this side.</summary>
    /// <remarks>\A and \z, not ^ and $: in .NET "$" also matches immediately
    /// BEFORE a trailing newline, so "/dev/sda\n" satisfies ^...$ and would
    /// have been let through. \z is the true end of the string.</remarks>
    internal static bool IsWholeDiskNode(string dev) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            dev, @"\A/dev/(nvme\d+n\d+|sd[a-z]{1,2}|mmcblk\d+)\z");

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
