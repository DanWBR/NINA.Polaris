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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services;

/// <summary>Watches for a removable USB drive plugged in <em>at runtime</em> and
/// exposes it as <see cref="Pending"/> so the status stream can offer to move the
/// capture home (ImageOutputDir) onto it. Drives already mounted at startup are
/// snapshotted and never offered, so only a genuine hot-plug prompts. The prompt
/// itself (a yes/no) and applying the change live client-side / in
/// <c>UsbEndpoints</c>; this service only detects and de-dups.</summary>
public sealed class UsbDriveWatcherService : BackgroundService {
    public record UsbDrive(string Path, string Label, long? FreeBytes, long? TotalBytes);
    public record RevertPrompt(string RemovedLabel, string DefaultPath);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<UsbDriveWatcherService> _log;

    // Mounts we have already accounted for (present at startup, or already
    // offered). Drives that disappear are dropped so a re-plug asks again.
    private readonly HashSet<string> _known = new();
    // Drives the user decided on (used or declined) this session.
    private readonly HashSet<string> _dismissed = new();

    private volatile UsbDrive? _pending;
    /// <summary>The newly-plugged removable drive awaiting the user's decision,
    /// or null. Serialized into the status stream.</summary>
    public UsbDrive? Pending => _pending;

    private volatile RevertPrompt? _revertPending;
    /// <summary>Set when the drive holding the current capture home is unplugged,
    /// offering to revert the home to the default folder. null otherwise.</summary>
    public RevertPrompt? RevertPending => _revertPending;

    public void ClearRevert() => _revertPending = null;

    /// <summary>The default capture home (the user's home/files), used to offer a
    /// revert when the drive holding the current home is removed.</summary>
    public static string DefaultImageDir() {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? "" : System.IO.Path.Combine(home, "files");
    }

    public UsbDriveWatcherService(ProfileService profiles, NotificationService notify,
                                  ILogger<UsbDriveWatcherService> log) {
        _profiles = profiles;
        _notify = notify;
        _log = log;
    }

    /// <summary>Mark a drive as decided (used or declined) so it is not offered
    /// again until it is unplugged and re-inserted.</summary>
    public void Dismiss(string path) {
        var key = Norm(path);
        _dismissed.Add(key);
        _known.Add(key);
        if (_pending is { } p && Norm(p.Path) == key) _pending = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // Seed: whatever removable media is already mounted at startup is "known"
        // and never prompts (the user did not just plug it in).
        try { foreach (var d in Enumerate()) _known.Add(Norm(d.Path)); }
        catch (Exception ex) { _log.LogDebug(ex, "USB watcher: initial snapshot failed"); }

        try { await Task.Delay(Interval, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested) {
            try { Poll(); }
            catch (Exception ex) { _log.LogDebug(ex, "USB watcher poll failed"); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Poll() {
        var current = Enumerate().ToList();
        var currentKeys = current.Select(d => Norm(d.Path)).ToHashSet();
        var home = Norm(_profiles.Active?.ImageOutputDir ?? "");

        // A drive we knew is gone now: if the capture home lived on it, offer to
        // revert the home to the default folder. Fires once per removal (the key
        // is dropped from _known just below, so it cannot re-trigger).
        foreach (var k in _known) {
            if (currentKeys.Contains(k)) continue;
            if (_revertPending == null && home.Length > 0 &&
                (home == k || home.StartsWith(k + "/") || home.StartsWith(k + "\\"))) {
                var def = DefaultImageDir();
                if (def.Length > 0 && Norm(def) != home)
                    _revertPending = new RevertPrompt(System.IO.Path.GetFileName(k), def);
            }
        }

        // Forget drives that were unplugged, so re-inserting one prompts again.
        _known.RemoveWhere(k => !currentKeys.Contains(k));
        _dismissed.RemoveWhere(k => !currentKeys.Contains(k));
        if (_pending is { } p0 && !currentKeys.Contains(Norm(p0.Path))) _pending = null;

        if (_pending != null) return;   // one add-prompt at a time

        foreach (var d in current) {
            var key = Norm(d.Path);
            if (_known.Contains(key)) continue;   // present at startup or already offered
            _known.Add(key);                       // offer each drive at most once
            if (_dismissed.Contains(key)) continue;
            // Skip if the capture home already lives on this drive.
            if (home.Length > 0 && (home == key || home.StartsWith(key + "/") || home.StartsWith(key + "\\")))
                continue;
            _pending = d;
            _notify.Push("info", $"USB drive detected: {d.Label}", 6000);
            break;
        }
    }

    private IEnumerable<UsbDrive> Enumerate() =>
        OperatingSystem.IsWindows() ? EnumerateWindows() : EnumerateLinux();

    private static IEnumerable<UsbDrive> EnumerateWindows() {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in drives) {
            bool ok;
            try { ok = d.DriveType == DriveType.Removable && d.IsReady; } catch { ok = false; }
            if (!ok) continue;
            string label; long? free = null, total = null;
            try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : d.VolumeLabel; } catch { label = d.Name; }
            try { free = d.AvailableFreeSpace; total = d.TotalSize; } catch { }
            yield return new UsbDrive(d.RootDirectory.FullName, label, free, total);
        }
    }

    // Auto-mount locations used by removable-media managers (udisks2 / usbmount).
    private static readonly string[] RemovablePrefixes = { "/media/", "/run/media/" };

    private static IEnumerable<UsbDrive> EnumerateLinux() {
        string[] lines;
        try { lines = File.ReadAllLines("/proc/mounts"); } catch { yield break; }
        var seen = new HashSet<string>();
        foreach (var line in lines) {
            var parts = line.Split(' ');
            if (parts.Length < 2) continue;
            var device = parts[0];
            var mount = Unescape(parts[1]);
            if (!device.StartsWith("/dev/")) continue;

            bool removable = RemovablePrefixes.Any(pfx => mount.StartsWith(pfx) && mount.Length > pfx.Length);
            // /mnt is often manual/internal mounts, so require the sysfs flag there.
            if (!removable && mount.StartsWith("/mnt/") && mount.Length > "/mnt/".Length)
                removable = IsRemovableBlockDevice(device);
            if (!removable) continue;
            if (!seen.Add(mount)) continue;

            var label = System.IO.Path.GetFileName(mount.TrimEnd('/'));
            if (string.IsNullOrEmpty(label)) label = mount;
            long? free = null, total = null;
            try { var di = new DriveInfo(mount); free = di.AvailableFreeSpace; total = di.TotalSize; } catch { }
            yield return new UsbDrive(mount, label, free, total);
        }
    }

    // Read /sys/block/<disk>/removable for a /dev/<partition> node.
    private static bool IsRemovableBlockDevice(string devPath) {
        try {
            var leaf = System.IO.Path.GetFileName(devPath);   // e.g. sdb1
            if (!leaf.StartsWith("sd")) return false;         // only interested in USB mass storage
            var disk = leaf.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'); // sdb1 -> sdb
            var flag = $"/sys/block/{disk}/removable";
            return File.Exists(flag) && File.ReadAllText(flag).Trim() == "1";
        } catch { return false; }
    }

    // /proc/mounts octal-escapes spaces and a few other chars (\040 = space).
    private static string Unescape(string s) {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++) {
            if (s[i] == '\\' && i + 3 < s.Length &&
                int.TryParse(s.Substring(i + 1, 3), System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out _)) {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            } else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string Norm(string path) {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.TrimEnd('/', '\\');
        return OperatingSystem.IsWindows() ? p.ToLowerInvariant() : p;
    }
}
