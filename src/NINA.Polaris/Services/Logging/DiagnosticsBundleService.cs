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
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// One zip with what a bug report needs, from the LOG panel's Diagnostics
/// button: Polaris's log and recent session logs, crash files, the native-SDK
/// quarantine, and on Linux the system journal, kernel log, indiserver log,
/// USB tree, network state, package versions and resources. It replaces the
/// list of commands a reporter was asked to run over SSH.
///
/// <para>Everything text goes through <see cref="LogService.RedactText"/>.
/// Profiles (which hold passwords) are never included, and no command that
/// prints a WiFi key is run.</para>
/// </summary>
public sealed class DiagnosticsBundleService {
    private readonly LogService _log;
    private readonly ILogger<DiagnosticsBundleService> _logger;

    public DiagnosticsBundleService(LogService log, ILogger<DiagnosticsBundleService> logger) {
        _log = log;
        _logger = logger;
    }

    internal static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA.Polaris", "logs");

    /// <summary>Linux commands and the file each one's output goes into. Fixed
    /// strings, run through /bin/sh for the pipes; none takes caller input.</summary>
    internal static readonly (string File, string Command)[] LinuxCommands = {
        ("system/journal-polaris.txt",          "journalctl -u polaris -b --no-pager -n 4000 2>&1"),
        ("system/journal-polaris-previous-boot.txt", "journalctl -u polaris -b -1 --no-pager -n 1500 2>&1"),
        ("system/journal-warnings.txt",         "journalctl -b -p warning --no-pager -n 4000 2>&1"),
        ("system/journal-kernel.txt",           "journalctl -k -b --no-pager -n 4000 2>&1 || dmesg 2>&1 | tail -n 4000"),
        ("system/indiserver.log",               "tail -n 4000 " + SystemJournalService.IndiserverLog + " 2>&1"),
        ("system/usb.txt",                      "lsusb 2>&1; echo; lsusb -t 2>&1"),
        ("system/network.txt",                  "nmcli -t device status 2>&1; echo; nmcli -t -f NAME,TYPE,DEVICE connection show --active 2>&1; echo; ip -br addr 2>&1"),
        ("system/packages.txt",                 "dpkg-query -W -f='${Package} ${Version}\\n' polaris indi-bin libindi1 'libraw*' 'libgphoto2*' 'indi-*' 'libasi*' 'libqhy*' 'libtoupcam*' astap phd2 2>/dev/null"),
        ("system/resources.txt",                "uptime 2>&1; echo; free -m 2>&1; echo; df -h 2>&1; echo; cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null; command -v vcgencmd >/dev/null && vcgencmd get_throttled 2>&1"),
        ("system/os.txt",                       "cat /etc/os-release 2>&1; echo; uname -a 2>&1"),
    };

    public async Task WriteZipAsync(Stream destination, CancellationToken ct) {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        await AddTextAsync(zip, "README.txt", Readme());
        await AddTextAsync(zip, "system/polaris.txt", PolarisSummary());

        // The live log, exactly what the LOG panel holds (already redacted).
        var live = zip.CreateEntry("polaris-log.jsonl", CompressionLevel.Optimal);
        await using (var s = live.Open()) await _log.ExportJsonlAsync(s, ct);

        // The two most recent session logs and the five most recent crash files.
        try {
            if (Directory.Exists(LogDir)) {
                foreach (var f in new DirectoryInfo(LogDir).GetFiles("polaris_*.jsonl")
                             .OrderByDescending(f => f.LastWriteTimeUtc).Take(2))
                    await AddFileAsync(zip, "session-logs/" + f.Name, f.FullName, ct);
                foreach (var f in new DirectoryInfo(LogDir).GetFiles("polaris_crash_*.log")
                             .OrderByDescending(f => f.LastWriteTimeUtc).Take(5))
                    await AddFileAsync(zip, "crashes/" + f.Name, f.FullName, ct);
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Diagnostics: reading {Dir} failed", LogDir);
        }

        var quarantined = NINA.Image.NativeLibs.NativeCallGuard.Quarantined();
        await AddTextAsync(zip, "native-sdk-guard.txt", quarantined.Count == 0
            ? "No native camera SDK is switched off."
            : string.Join("\n", quarantined.Select(q => $"{q.Sdk}: {q.Reason}")));

        if (OperatingSystem.IsLinux()) {
            foreach (var (file, cmd) in LinuxCommands)
                await AddTextAsync(zip, file, await ShellAsync(cmd, ct));
        }
    }

    private static string Readme() =>
        "Polaris diagnostics, " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'") + "\n\n" +
        "polaris-log.jsonl        what the LOG panel holds right now\n" +
        "session-logs/            the two most recent session logs\n" +
        "crashes/                 the most recent crash files, if any\n" +
        "native-sdk-guard.txt     native camera SDKs switched off after a crash\n" +
        "system/                  (Linux) journal, kernel log, indiserver log, USB, network, packages, resources\n\n" +
        "Passwords, tokens and WiFi keys are redacted; profiles are not included.\n";

    private static string PolarisSummary() {
        var sb = new StringBuilder();
        sb.AppendLine("Polaris " + NINA.Polaris.Services.External.UpdateService.CurrentVersionShort);
        sb.AppendLine("OS " + RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")");
        sb.AppendLine("Process " + RuntimeInformation.ProcessArchitecture + ", " + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("Up since " + Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        sb.AppendLine("Machine " + Environment.MachineName + ", " + Environment.ProcessorCount + " CPUs");
        return sb.ToString();
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text) {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = e.Open();
        var bytes = Encoding.UTF8.GetBytes(LogService.RedactText(text));
        await s.WriteAsync(bytes);
    }

    private static async Task AddFileAsync(ZipArchive zip, string name, string path, CancellationToken ct) {
        try {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                text = await sr.ReadToEndAsync(ct);
            await AddTextAsync(zip, name, text);
        } catch {
            // A file rotated away or locked mid-read is left out.
        }
    }

    private static async Task<string> ShellAsync(string command, CancellationToken ct) {
        try {
            // Every command folds stderr into stdout itself (2>&1), so only
            // stdout is read; an unread redirected stderr could block it.
            var psi = new ProcessStartInfo("/bin/sh") {
                RedirectStandardOutput = true, RedirectStandardError = false, UseShellExecute = false
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            using var proc = Process.Start(psi);
            if (proc == null) return "(could not run)";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return "$ " + command + "\n\n" + await outTask;
        } catch (OperationCanceledException) {
            return "$ " + command + "\n\n(timed out)";
        } catch (Exception ex) {
            return "$ " + command + "\n\n(" + ex.Message + ")";
        }
    }
}
