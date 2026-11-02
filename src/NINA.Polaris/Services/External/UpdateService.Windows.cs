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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NINA.Polaris.Services.External;

/// <summary>
/// Self-update for the portable Windows build.
///
/// <para>The Windows equivalent of the .deb flow's transient systemd unit is a
/// PowerShell worker (<c>polaris-update.ps1</c>, shipped in the release zip)
/// that runs as its own process. Polaris downloads the zip, checks it against
/// GitHub's SHA-256, merges the operator's <c>appsettings.json</c> into the new
/// one, copies the worker out of its own folder and starts it. The worker
/// extracts and checks the new build while Polaris keeps running, then asks
/// Polaris to stop over the loopback API, swaps the folders, starts the new
/// version, and puts the old folder back if the new one does not answer with
/// the expected version. A failure before the swap leaves the running Polaris
/// untouched.</para>
///
/// <para>Design and much of the worker's sequence come from matteoconti92's
/// community prototype (#63).</para>
/// </summary>
public partial class UpdateService {
    /// <summary>The worker script. Its presence next to the executable is what
    /// marks a portable release install: CI adds it to the Windows zip only, so
    /// a dev build never updates itself over its own bin folder.</summary>
    public const string WindowsWorkerScript = "polaris-update.ps1";

    /// <summary>Tests only: pretend to be (or not be) a portable Windows install.</summary>
    internal bool? WindowsOverride { get; set; }

    internal bool IsWindowsPortable => WindowsOverride ?? WindowsPortableDetected.Value;

    private static readonly Lazy<bool> WindowsPortableDetected = new(DetectWindowsPortable);

    private static bool DetectWindowsPortable() {
        if (!OperatingSystem.IsWindows()) return false;
        var dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, WindowsWorkerScript))
                || !File.Exists(Path.Combine(dir, "NINA.Polaris.exe")))
            return false;
        // Session 0 is a Windows service (NSSM, a task run without a logon). A
        // service manager restarts Polaris the moment it exits, which would
        // grab the folder the worker is replacing, so the service owner updates.
        try { return Process.GetCurrentProcess().SessionId != 0; }
        catch { return false; }
    }

    /// <summary>.NET runtime id of this process on Windows, as the release zips are named.</summary>
    internal static string WinRid =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    private string WindowsUpdateDir => Path.Combine(_profiles.DataDir, "update");
    private string WindowsStatusPath => Path.Combine(WindowsUpdateDir, "status.json");
    private static readonly SemaphoreSlim WindowsInstallGate = new(1, 1);

    /// <summary>The worker's last reported phase ("starting", "extracting",
    /// "stopping", "installing", "complete", "failed", "rolled-back"...), or
    /// null when no Windows update has run.</summary>
    public JsonElement? WindowsProgress() {
        try {
            if (!File.Exists(WindowsStatusPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(WindowsStatusPath));
            return doc.RootElement.Clone();
        } catch {
            return null;
        }
    }

    private void WriteWindowsStatus(string phase, string message, string? version) {
        try {
            Directory.CreateDirectory(WindowsUpdateDir);
            var json = JsonSerializer.Serialize(new {
                phase, message, version, at = DateTime.UtcNow.ToString("o")
            });
            File.WriteAllText(WindowsStatusPath, json);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not write the Windows update status");
        }
    }

    private async Task<(bool ok, string? error)> InstallWindowsAsync(string assetUrl, string? assetName,
            long assetSize, string? sha256, string? version, CancellationToken ct) {
        if (string.IsNullOrEmpty(sha256))
            return (false, "GitHub publishes no SHA-256 for this package, so it cannot be verified. Not installed.");
        if (string.IsNullOrEmpty(version)) return (false, "The release has no version.");
        // The status page reports the numeric version; a pre-release tag
        // (0.99.200-preview1) is matched on its numeric part.
        var core = version.Split('-')[0];

        var appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var worker = Path.Combine(appDir, WindowsWorkerScript);
        if (!File.Exists(worker)) return (false, $"The update worker ({WindowsWorkerScript}) is missing.");

        // The worker swaps folders next to this one, so the parent must be
        // writable by this user (it is not under Program Files).
        var probe = appDir + ".polaris-write-test";
        try {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
        } catch (Exception ex) {
            return (false, $"Polaris cannot write next to its folder ({Path.GetDirectoryName(appDir)}): "
                + ex.Message + " Move the Polaris folder to a place your user owns, such as Documents, and update from there.");
        }

        if (!await WindowsInstallGate.WaitAsync(0, ct))
            return (false, "An update is already being prepared.");
        try {
            WriteWindowsStatus("downloading", $"Downloading {version}…", version);
            var zip = Path.Combine(WindowsUpdateDir, "polaris-update.zip");
            var (dlOk, dlErr) = await DownloadAndStageAsync(assetUrl, assetName, zip, ct);
            if (!dlOk) {
                WriteWindowsStatus("failed", dlErr ?? "Download failed.", version);
                return (false, dlErr);
            }

            long length = new FileInfo(zip).Length;
            string got;
            await using (var fs = File.OpenRead(zip))
                got = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if ((assetSize > 0 && length != assetSize) || !string.Equals(got, sha256, StringComparison.Ordinal)) {
                try { File.Delete(zip); } catch { }
                _logger.LogWarning("Windows update: checksum mismatch (got {Got}, want {Want}, {Len} bytes)",
                    got, sha256, length);
                const string msg = "Checksum mismatch: the download is corrupt or not the genuine release. Not installed.";
                WriteWindowsStatus("failed", msg, version);
                return (false, msg);
            }

            // Carry the operator's settings across: the release's appsettings
            // with every value the operator has set laid over it.
            var mergedPath = Path.Combine(WindowsUpdateDir, "appsettings.merged.json");
            try { File.Delete(mergedPath); } catch { }
            using (var archive = ZipFile.OpenRead(zip)) {
                var exe = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, "NINA.Polaris.exe", StringComparison.OrdinalIgnoreCase));
                if (exe == null) {
                    const string msg = "The downloaded package has no NINA.Polaris.exe. Not installed.";
                    WriteWindowsStatus("failed", msg, version);
                    return (false, msg);
                }
                var prefix = exe.FullName[..^exe.Name.Length];
                var shipped = archive.GetEntry(prefix + "appsettings.json");
                var current = Path.Combine(appDir, "appsettings.json");
                if (shipped != null && File.Exists(current)) {
                    using var reader = new StreamReader(shipped.Open());
                    var merged = MergeAppSettings(await reader.ReadToEndAsync(ct),
                        await File.ReadAllTextAsync(current, ct));
                    await File.WriteAllTextAsync(mergedPath, merged, ct);
                }
            }

            // Run the worker from a copy: the folder it replaces cannot be
            // renamed while a process has a file or its working directory there.
            var workerCopy = Path.Combine(WindowsUpdateDir, WindowsWorkerScript);
            File.Copy(worker, workerCopy, overwrite: true);

            var restartArgs = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(Environment.GetCommandLineArgs().Skip(1).ToArray())));
            int port = _config?.GetValue("Server:Http:Port", 5080) ?? 5080;
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
                    "WindowsPowerShell", "v1.0", "powershell.exe")) {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = WindowsUpdateDir
            };
            foreach (var a in new[] {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", workerCopy,
                    "-ZipPath", zip, "-Sha256", sha256, "-AppDir", appDir,
                    "-ParentPid", Environment.ProcessId.ToString(), "-Port", port.ToString(),
                    "-Version", core, "-StatusPath", WindowsStatusPath,
                    "-RestartArgs", restartArgs, "-WorkDir", Environment.CurrentDirectory })
                psi.ArgumentList.Add(a);
            // Never an empty argument: Windows PowerShell 5.1 can drop "" under
            // -File and bind the next switch name as the value.
            if (File.Exists(mergedPath)) {
                psi.ArgumentList.Add("-MergedSettings");
                psi.ArgumentList.Add(mergedPath);
            }
            // Come back the way we were started: hidden when there is no
            // visible console (started by a task or a tray), a console otherwise.
            if (!HasVisibleConsole()) psi.ArgumentList.Add("-Hidden");

            WriteWindowsStatus("starting", $"Preparing {version}…", version);
            using var child = Process.Start(psi);
            if (child == null) {
                WriteWindowsStatus("failed", "Could not start the update worker.", version);
                return (false, "Could not start the update worker.");
            }
            _logger.LogInformation("Windows update: worker started (PID {Pid}) for {Version}", child.Id, version);
            return (true, null);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Windows update failed to start");
            WriteWindowsStatus("failed", "Update failed to start: " + ex.Message, version);
            return (false, "Update failed to start: " + ex.Message);
        } finally {
            WindowsInstallGate.Release();
        }
    }

    /// <summary>
    /// The release's settings with the operator's laid over them: every value
    /// present in <paramref name="current"/> wins, objects merge key by key,
    /// keys only the release has (new settings) are kept. Arrays are taken
    /// whole from the operator. A file that does not parse is kept verbatim,
    /// since the running Polaris was reading it.
    /// </summary>
    internal static string MergeAppSettings(string shipped, string current) {
        var options = new JsonDocumentOptions {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        };
        JsonNode? baseNode, userNode;
        try {
            userNode = JsonNode.Parse(current, documentOptions: options);
        } catch (JsonException) {
            return current;
        }
        try {
            baseNode = JsonNode.Parse(shipped, documentOptions: options);
        } catch (JsonException) {
            return current;
        }
        if (baseNode is not JsonObject b || userNode is not JsonObject u) return current;
        Overlay(b, u);
        return b.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Overlay(JsonObject target, JsonObject source) {
        foreach (var (key, value) in source.ToList()) {
            if (value is JsonObject so && target[key] is JsonObject to) {
                Overlay(to, so);
            } else {
                target[key] = value?.DeepClone();
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static bool HasVisibleConsole() {
        try {
            var h = GetConsoleWindow();
            return h != IntPtr.Zero && IsWindowVisible(h);
        } catch {
            return true;
        }
    }
}
