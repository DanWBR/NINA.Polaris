using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.Simulator;

/// <summary>
/// Linux/macOS backend: spawns <c>indiserver</c> with the
/// <c>indi_simulator_*</c> drivers selected in
/// <see cref="SimulatorLaunchRequest.Devices"/>.
///
/// Pattern mirrors <c>PHD2ProcessManager</c> — single Process held
/// in a field, TCP probe for liveness, graceful shutdown via
/// SIGTERM (Process.Close on POSIX) then force-kill on timeout.
/// Detection is a one-shot <c>which</c> + <c>--version</c> parse.
/// </summary>
public class IndiSimulatorBackend : ISimulatorBackend, IDisposable {
    private readonly ILogger<IndiSimulatorBackend> _logger;
    private Process? _process;
    private SimulatorLaunchRequest? _lastLaunch;
    private string? _fifoPath;
    // Tracks which device tags we've sent a `start` to but no `stop`
    // since launch. Used by Add/RemoveDeviceAsync to decide whether
    // a command is a no-op, and surfaced via SimulatorService.GetStatus
    // so the UI can render live checkbox state without polling indi.
    private readonly HashSet<string> _runningDevices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _devicesLock = new();

    public string Kind => "indi";
    public bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
    public string DownloadInstructionsUrl =>
        "https://www.indilib.org/get-indi/download.html";

    /// <summary>Subset of well-known indi_simulator_* binaries we
    /// know how to spawn. Keyed by the canonical tag from
    /// <see cref="SimulatorDeviceTags"/>; value is the binary name.
    /// More can be added later as we surface more device kinds.</summary>
    private static readonly Dictionary<string, string> DeviceBinaries =
        new(StringComparer.OrdinalIgnoreCase) {
            [SimulatorDeviceTags.Ccd]       = "indi_simulator_ccd",
            [SimulatorDeviceTags.Telescope] = "indi_simulator_telescope",
            [SimulatorDeviceTags.Focus]     = "indi_simulator_focus",
            [SimulatorDeviceTags.Wheel]     = "indi_simulator_wheel",
            [SimulatorDeviceTags.Guide]     = "indi_simulator_guide",
            [SimulatorDeviceTags.Dome]      = "indi_simulator_dome",
            [SimulatorDeviceTags.Weather]   = "indi_simulator_weather",
        };

    public IndiSimulatorBackend(ILogger<IndiSimulatorBackend> logger) {
        _logger = logger;
    }

    public async Task<SimulatorInstall> DetectInstallAsync(CancellationToken ct = default) {
        if (!IsSupported) {
            return new SimulatorInstall(false, null, null, [],
                "INDI simulator backend only runs on Linux + macOS.");
        }

        // 1. indiserver itself
        var indiserverPath = await WhichAsync("indiserver", ct);
        if (string.IsNullOrEmpty(indiserverPath)) {
            return new SimulatorInstall(false, null, null, [],
                "indiserver not found in PATH. Install with: sudo apt install indi-bin (or brew install indi-bin on macOS).");
        }

        // 2. version string — best-effort parse
        var version = await ReadVersionAsync(indiserverPath, ct);

        // 3. which simulator drivers exist on this host
        var available = new List<string>();
        foreach (var (tag, binary) in DeviceBinaries) {
            if (!string.IsNullOrEmpty(await WhichAsync(binary, ct))) {
                available.Add(tag);
            }
        }

        if (available.Count == 0) {
            return new SimulatorInstall(true, version, indiserverPath, [],
                "indiserver installed but no indi_simulator_* drivers found. The simulator drivers ship in the same indi-bin package on most distros; on macOS you may need indi-3rdparty.");
        }

        return new SimulatorInstall(true, version, indiserverPath, available, null);
    }

    public async Task<bool> LaunchAsync(SimulatorLaunchRequest req, CancellationToken ct = default) {
        if (!IsSupported) return false;

        // Already running? Just sync the device set to what was
        // requested (idempotent + survives a missed Stop click).
        if (await IsRunningAsync(ct)) {
            _logger.LogInformation("indiserver already responding on port {Port}; syncing device set.", req.Port);
            _lastLaunch = req;
            await SyncDevicesAsync(req.Devices, ct);
            return true;
        }

        // FIFO mode: launch indiserver with NO drivers in argv, then
        // feed `start indi_simulator_*` commands through the FIFO.
        // Lets us add/remove devices mid-session via Add/RemoveDeviceAsync
        // without restarting the server (SIM-8). Without the FIFO,
        // every checkbox toggle would mean a 2s tear-down + spawn.
        //
        // FIFO path is PID-suffixed so multiple Polaris instances on
        // the same host don't trample each other.
        _fifoPath = Path.Combine(Path.GetTempPath(),
            $"polaris-indi-{Environment.ProcessId}.fifo");
        if (!await TryCreateFifoAsync(_fifoPath, ct)) {
            _logger.LogError("Could not create FIFO at {Path}", _fifoPath);
            _fifoPath = null;
            return false;
        }

        var args = $"-v -p {req.Port} -f {_fifoPath}";
        try {
            var psi = new ProcessStartInfo("indiserver", args) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _process = Process.Start(psi);
            if (_process == null) {
                _logger.LogError("Process.Start returned null for indiserver");
                CleanupFifo();
                return false;
            }
            // Drain stdout + stderr into the Polaris log so a crashed
            // driver shows up in journalctl instead of vanishing.
            _ = Task.Run(() => DrainAsync(_process.StandardOutput, "indiserver/stdout"));
            _ = Task.Run(() => DrainAsync(_process.StandardError, "indiserver/stderr"));
            _logger.LogInformation("Spawned indiserver (PID {Pid}) with: {Args}", _process.Id, args);

            // Wait for indiserver to bind the listening socket before
            // we try to write to the FIFO — otherwise the start
            // commands queue but the device might fail before the
            // first client (Polaris itself) is allowed to connect.
            for (int i = 0; i < 20; i++) {
                if (await IsRunningAsync(ct)) break;
                await Task.Delay(100, ct);
            }
            if (!await IsRunningAsync(ct)) {
                _logger.LogWarning("indiserver started but isn't responding on port {Port} after 2s.", req.Port);
                return false;
            }

            _lastLaunch = req;
            // Now feed the initial device set through the FIFO. Each
            // start command is its own line; indiserver reads them
            // one at a time + spawns the driver child.
            await SyncDevicesAsync(req.Devices, ct);
            return true;
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to launch indiserver");
            CleanupFifo();
            return false;
        }
    }

    public async Task<bool> AddDeviceAsync(string device, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(_fifoPath) || _process is null || _process.HasExited) {
            _logger.LogWarning("AddDeviceAsync called with no running indiserver; ignoring.");
            return false;
        }
        if (!DeviceBinaries.TryGetValue(device, out var binary)) {
            _logger.LogWarning("Unknown device tag for INDI backend: {Device}", device);
            return false;
        }
        lock (_devicesLock) {
            if (_runningDevices.Contains(device)) return true;  // idempotent
        }
        var ok = await WriteFifoCommandAsync($"start {binary}", ct);
        if (ok) {
            lock (_devicesLock) _runningDevices.Add(device);
            _logger.LogInformation("indiserver: started {Binary}", binary);
        }
        return ok;
    }

    public async Task<bool> RemoveDeviceAsync(string device, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(_fifoPath) || _process is null || _process.HasExited) {
            return false;
        }
        if (!DeviceBinaries.TryGetValue(device, out var binary)) return false;
        lock (_devicesLock) {
            if (!_runningDevices.Contains(device)) return true;  // already gone
        }
        var ok = await WriteFifoCommandAsync($"stop {binary}", ct);
        if (ok) {
            lock (_devicesLock) _runningDevices.Remove(device);
            _logger.LogInformation("indiserver: stopped {Binary}", binary);
        }
        return ok;
    }

    /// <summary>Reconcile the running device set against a target
    /// (used on launch + when the user clicks the whole-stack Launch
    /// button with a new device selection on an already-running
    /// server). Sequential because indiserver processes FIFO commands
    /// in order and we don't want a race between start/stop of the
    /// same driver.</summary>
    private async Task SyncDevicesAsync(IReadOnlyList<string> target, CancellationToken ct) {
        HashSet<string> targetSet = new(target, StringComparer.OrdinalIgnoreCase);
        HashSet<string> currentSnapshot;
        lock (_devicesLock) { currentSnapshot = new(_runningDevices, StringComparer.OrdinalIgnoreCase); }

        foreach (var dev in currentSnapshot.Except(targetSet, StringComparer.OrdinalIgnoreCase)) {
            await RemoveDeviceAsync(dev, ct);
        }
        foreach (var dev in target.Where(d => !currentSnapshot.Contains(d))) {
            await AddDeviceAsync(dev, ct);
        }
    }

    public async Task ShutdownAsync(CancellationToken ct = default) {
        var p = _process;
        if (p == null) { CleanupFifo(); return; }
        try {
            if (!p.HasExited) {
                // CloseMainWindow == SIGTERM on POSIX for processes
                // without a window. Graceful — indiserver passes the
                // signal to its driver children too.
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) {
                    _logger.LogWarning("indiserver didn't exit within 3s, force-killing.");
                    p.Kill(entireProcessTree: true);
                }
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Error during indiserver shutdown (ignored)");
        } finally {
            p.Dispose();
            _process = null;
            _lastLaunch = null;
            lock (_devicesLock) _runningDevices.Clear();
            CleanupFifo();
        }
        // Avoid CS1998 — keep the async signature for symmetry with
        // other backends that need real awaits.
        await Task.CompletedTask;
    }

    public async Task<bool> IsRunningAsync(CancellationToken ct = default) {
        var port = _lastLaunch?.Port ?? 7624;
        try {
            using var tcp = new TcpClient();
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await tcp.ConnectAsync("127.0.0.1", port, probeCts.Token);
            return tcp.Connected;
        } catch {
            return false;
        }
    }

    // --- helpers ---

    /// <summary>POSIX `which` equivalent — find a binary in PATH.
    /// Returns null when not found. Uses the actual <c>which</c>
    /// command rather than scanning PATH ourselves because it
    /// handles distro quirks (per-shell PATH, alternatives system).</summary>
    internal static async Task<string?> WhichAsync(string binary, CancellationToken ct = default) {
        try {
            var psi = new ProcessStartInfo(
                OperatingSystem.IsWindows() ? "where" : "which", binary) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(3));
            var stdout = await proc.StandardOutput.ReadToEndAsync(to.Token);
            await proc.WaitForExitAsync(to.Token);
            if (proc.ExitCode != 0) return null;
            // `which` may print multiple paths; take the first.
            var first = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries
                                          | StringSplitOptions.TrimEntries)
                              .FirstOrDefault();
            return string.IsNullOrEmpty(first) ? null : first;
        } catch {
            return null;
        }
    }

    /// <summary>Parse `indiserver --version` (it prints to stderr
    /// on most builds; check both streams).</summary>
    internal static async Task<string?> ReadVersionAsync(string path, CancellationToken ct = default) {
        try {
            var psi = new ProcessStartInfo(path, "--version") {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(3));
            var stdout = await proc.StandardOutput.ReadToEndAsync(to.Token);
            var stderr = await proc.StandardError.ReadToEndAsync(to.Token);
            await proc.WaitForExitAsync(to.Token);
            return ParseVersion(stdout + "\n" + stderr);
        } catch {
            return null;
        }
    }

    /// <summary>Extract the first thing that looks like a semver
    /// out of a chatty <c>--version</c> dump. Public for tests.</summary>
    internal static string? ParseVersion(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Negative lookbehind/lookahead on digit instead of \b — \b
        // doesn't trigger between a letter and a digit (e.g. "v2.1.4"
        // where v and 2 are both word chars), which would make the
        // regex miss the leading "2" and capture just "1.4".
        var match = Regex.Match(raw, @"(?<!\d)(\d+\.\d+(?:\.\d+)?)(?!\d)");
        return match.Success ? match.Value : null;
    }

    /// <summary>Compose the indiserver argv slice for a given
    /// request. Pure function — public so tests can pin the wire
    /// format without spawning a real subprocess. SIM-8 switched
    /// indiserver to FIFO mode (drivers added at runtime via
    /// <c>start</c> commands instead of positional argv), so the
    /// args no longer list driver binaries.</summary>
    internal static string BuildArgs(SimulatorLaunchRequest req, string fifoPath) {
        return $"-v -p {req.Port} -f {fifoPath}";
    }

    /// <summary>Compose the FIFO command for adding/removing one
    /// device. Pure function for test coverage of the wire format
    /// indiserver expects.</summary>
    internal static string? BuildFifoCommand(string device, bool start) {
        if (!DeviceBinaries.TryGetValue(device, out var binary)) return null;
        return (start ? "start " : "stop ") + binary;
    }

    /// <summary>Read which device tags are currently running on
    /// the FIFO server. Used by SimulatorService.GetStatus to surface
    /// the live UI state. Returns empty when nothing's running.</summary>
    public IReadOnlyList<string> ListRunningDevices() {
        lock (_devicesLock) return _runningDevices.ToList();
    }

    /// <summary>Create the named-pipe (POSIX FIFO) indiserver uses
    /// for runtime driver commands. <c>mkfifo</c> is not in the
    /// .NET BCL; shell out to the POSIX utility (Linux + macOS both
    /// ship it).</summary>
    internal static async Task<bool> TryCreateFifoAsync(string path, CancellationToken ct = default) {
        try {
            // Remove a stale FIFO from a previous run that crashed
            // without cleanup — `mkfifo` errors if the path exists.
            if (File.Exists(path)) File.Delete(path);
        } catch { /* ignore — mkfifo will surface the real error */ }
        try {
            var psi = new ProcessStartInfo("mkfifo", path) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(3));
            await proc.WaitForExitAsync(to.Token);
            return proc.ExitCode == 0 && File.Exists(path);
        } catch { return false; }
    }

    /// <summary>Append one line to the indiserver FIFO. Writes go
    /// through the kernel pipe buffer (~64KB on Linux); a single
    /// "start indi_simulator_X\n" never approaches that limit, so
    /// the write completes without blocking on indiserver's read.
    /// We don't open the FIFO with a persistent FileStream because
    /// closing it would signal EOF to indiserver — we want to be
    /// able to write again later. Each write is a fresh open/close.</summary>
    private async Task<bool> WriteFifoCommandAsync(string command, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(_fifoPath)) return false;
        try {
            // Open for write + non-blocking semantics. .NET's FileStream
            // can hang opening a FIFO if no reader is attached; we
            // know indiserver IS reading (it's the whole point of the
            // -f flag), so a plain async write is safe.
            await using var fs = new FileStream(_fifoPath, FileMode.Append,
                FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(command + "\n");
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(3));
            await fs.WriteAsync(bytes, to.Token);
            await fs.FlushAsync(to.Token);
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed writing FIFO command: {Command}", command);
            return false;
        }
    }

    private void CleanupFifo() {
        if (string.IsNullOrEmpty(_fifoPath)) return;
        try { if (File.Exists(_fifoPath)) File.Delete(_fifoPath); }
        catch (Exception ex) { _logger.LogDebug(ex, "FIFO cleanup failed (ignored)"); }
        _fifoPath = null;
    }

    private async Task DrainAsync(StreamReader stream, string tag) {
        try {
            string? line;
            while ((line = await stream.ReadLineAsync()) != null) {
                _logger.LogDebug("[{Tag}] {Line}", tag, line);
            }
        } catch (Exception ex) {
            _logger.LogTrace(ex, "Stream {Tag} drain ended", tag);
        }
    }

    public void Dispose() {
        try { ShutdownAsync().GetAwaiter().GetResult(); }
        catch { /* best-effort during disposal */ }
    }
}
