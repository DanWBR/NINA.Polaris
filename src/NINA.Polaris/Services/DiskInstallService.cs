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
using System.Text;

namespace NINA.Polaris.Services;

/// <summary>A disk the running system could be installed onto.</summary>
public record InstallTarget(string Device, string Size, string Model, bool IsBootDisk);

/// <summary>Everything the UI needs to decide whether to offer the install.</summary>
public record InstallTargetsResult(
    bool Available,
    string? Reason,
    string? BootDisk,
    bool RunningFromRemovable,
    IReadOnlyList<InstallTarget> Targets);

/// <summary>
/// Front end for <c>polaris-install-to-disk</c>: the tool that clones a Polaris
/// USB stick onto a mini PC's internal SSD.
///
/// The heavy lifting stays in the shell script, which is also usable over SSH
/// and is what the image ships. This only enumerates candidate disks and runs
/// the tool, so there is one implementation of the dangerous part.
///
/// Nothing here writes to a disk on its own; the script owns every guard
/// (refuses the boot disk, requires UEFI, wants a whole disk) and this service
/// re-checks the obvious ones so the UI can grey the option out instead of
/// letting the operator discover it in a log.
/// </summary>
public class DiskInstallService {
    private readonly ILogger<DiskInstallService> _logger;
    private const string Tool = "/usr/local/sbin/polaris-install-to-disk";

    // One install at a time, and the log is kept so the modal can show what
    // happened after the browser reconnects.
    private readonly object _lock = new();
    private readonly StringBuilder _log = new();
    private bool _running;
    private bool? _lastSucceeded;

    public DiskInstallService(ILogger<DiskInstallService> logger) => _logger = logger;

    public bool IsRunning { get { lock (_lock) return _running; } }
    public bool? LastSucceeded { get { lock (_lock) return _lastSucceeded; } }
    public string Log { get { lock (_lock) return _log.ToString(); } }

    public InstallTargetsResult GetTargets() {
        if (!OperatingSystem.IsLinux())
            return new(false, "Only Linux hosts can be cloned to an internal disk.", null, false, []);
        if (!File.Exists(Tool))
            return new(false, "This build does not ship the disk installer.", null, false, []);
        if (!Directory.Exists("/sys/firmware/efi"))
            return new(false, "The machine booted in legacy BIOS mode; the installer produces a UEFI system.",
                       null, false, []);

        var rootSrc = Run("findmnt", "-no SOURCE /").Trim();
        var bootDisk = string.IsNullOrEmpty(rootSrc)
            ? null
            : "/dev/" + Run("lsblk", $"-no PKNAME {rootSrc}").Trim();
        if (string.IsNullOrWhiteSpace(bootDisk) || bootDisk == "/dev/")
            return new(false, "Cannot tell which disk the system is running from.", null, false, []);

        // Only worth offering when we booted from something removable: on an
        // already-installed machine this would just be a way to wipe a data
        // disk. Not fatal - the script still guards - but the UI leads with it.
        var removable = ReadSysBool(bootDisk, "removable");

        var targets = new List<InstallTarget>();
        foreach (var line in Run("lsblk", "-dn -o NAME,SIZE,MODEL,TYPE").Split('\n')) {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            var name = parts[0];
            if (name.StartsWith("loop") || name.StartsWith("sr") || name.StartsWith("zram")) continue;
            if (!line.TrimEnd().EndsWith("disk", StringComparison.Ordinal)) continue;
            var dev = "/dev/" + name;
            var model = parts.Length > 3 ? string.Join(' ', parts[2..^1]) : "";
            targets.Add(new(dev, parts[1], model, dev == bootDisk));
        }

        return new(true, null, bootDisk, removable, targets);
    }

    /// <summary>Start the clone. Returns false when one is already running or
    /// the device is obviously wrong; the script does the real validation.</summary>
    public bool Start(string device) {
        if (string.IsNullOrWhiteSpace(device) || !device.StartsWith("/dev/")) return false;
        // No shell metacharacters: the value goes to a process argument, but a
        // device path has no business containing them either way.
        if (device.Any(c => !char.IsLetterOrDigit(c) && c != '/' && c != '-' && c != '_')) return false;

        lock (_lock) {
            if (_running) return false;
            _running = true;
            _lastSucceeded = null;
            _log.Clear();
            _log.AppendLine($"$ {Tool} {device} --yes");
        }

        _logger.LogWarning("Disk install starting: cloning the running system onto {Device}", device);
        _ = Task.Run(() => RunInstall(device));
        return true;
    }

    private void RunInstall(string device) {
        var ok = false;
        try {
            var psi = new ProcessStartInfo(Tool) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(device);
            psi.ArgumentList.Add("--yes");
            using var p = Process.Start(psi);
            if (p == null) { Append("failed to start the installer"); return; }
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Append(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) Append(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            ok = p.ExitCode == 0;
            Append(ok ? "\nDone. Power off, remove the USB stick, and boot from the disk."
                      : $"\nInstaller exited with code {p.ExitCode}.");
        } catch (Exception ex) {
            Append("error: " + ex.Message);
            _logger.LogError(ex, "Disk install failed");
        } finally {
            lock (_lock) { _running = false; _lastSucceeded = ok; }
            _logger.LogWarning("Disk install finished, success={Ok}", ok);
        }
    }

    private void Append(string line) {
        lock (_lock) {
            _log.AppendLine(line);
            // The rsync progress line is chatty; keep the tail bounded.
            if (_log.Length > 64 * 1024) _log.Remove(0, _log.Length - 48 * 1024);
        }
    }

    private static bool ReadSysBool(string devPath, string attr) {
        try {
            var name = Path.GetFileName(devPath);
            var p = $"/sys/block/{name}/{attr}";
            return File.Exists(p) && File.ReadAllText(p).Trim() == "1";
        } catch { return false; }
    }

    private static string Run(string file, string args) {
        try {
            using var p = Process.Start(new ProcessStartInfo(file, args) {
                RedirectStandardOutput = true, UseShellExecute = false,
            });
            if (p == null) return "";
            var s = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return s;
        } catch { return ""; }
    }
}
