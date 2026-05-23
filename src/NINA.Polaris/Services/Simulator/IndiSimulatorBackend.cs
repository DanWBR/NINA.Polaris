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
        if (req.Devices.Count == 0) {
            _logger.LogWarning("Simulator launch requested with empty devices list — nothing to start.");
            return false;
        }

        // Already running on this port? Treat as success (idempotent).
        if (await IsRunningAsync(ct)) {
            _logger.LogInformation("indiserver already responding on port {Port}, skipping launch.", req.Port);
            _lastLaunch = req;
            return true;
        }

        // Resolve which binaries to actually pass. Defensive against
        // a stale UI checkbox whose backing driver was uninstalled
        // between detection and launch — silently drop unknowns.
        var binaries = req.Devices
            .Select(d => DeviceBinaries.TryGetValue(d, out var bin) ? bin : null)
            .Where(b => !string.IsNullOrEmpty(b))
            .ToList();
        if (binaries.Count == 0) {
            _logger.LogWarning("None of the requested devices map to a known indi_simulator_* binary: {Devices}",
                string.Join(",", req.Devices));
            return false;
        }

        var args = $"-v -p {req.Port} {string.Join(' ', binaries)}";
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
                return false;
            }
            // Drain stdout + stderr into the Polaris log so a crashed
            // driver shows up in journalctl instead of vanishing.
            _ = Task.Run(() => DrainAsync(_process.StandardOutput, "indiserver/stdout"));
            _ = Task.Run(() => DrainAsync(_process.StandardError, "indiserver/stderr"));
            _logger.LogInformation("Spawned indiserver (PID {Pid}) with: {Args}", _process.Id, args);

            // Give it a moment to bind the socket — indiserver sets
            // up the listener within ~100-300 ms typically. Probe in
            // a loop with short timeout instead of arbitrary sleep.
            for (int i = 0; i < 20; i++) {
                if (await IsRunningAsync(ct)) {
                    _lastLaunch = req;
                    return true;
                }
                await Task.Delay(100, ct);
            }
            _logger.LogWarning("indiserver started but isn't responding on port {Port} after 2s.", req.Port);
            return false;
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to launch indiserver");
            return false;
        }
    }

    public async Task ShutdownAsync(CancellationToken ct = default) {
        var p = _process;
        if (p == null) return;
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
    /// format without spawning a real subprocess.</summary>
    internal static string BuildArgs(SimulatorLaunchRequest req) {
        var binaries = req.Devices
            .Select(d => DeviceBinaries.TryGetValue(d, out var bin) ? bin : null)
            .Where(b => !string.IsNullOrEmpty(b))
            .ToList();
        return $"-v -p {req.Port} {string.Join(' ', binaries)}";
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
