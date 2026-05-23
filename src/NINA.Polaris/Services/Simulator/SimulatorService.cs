namespace NINA.Polaris.Services.Simulator;

/// <summary>
/// Orchestrator on top of <see cref="ISimulatorBackend"/>. Picks the
/// right backend at startup based on the host OS, caches the latest
/// install-detection snapshot so the WS status payload doesn't reshell
/// every tick, tracks who-spawned-what for shutdown safety, and
/// surfaces a uniform <see cref="GetStatus"/> the endpoints + UI bind to.
///
/// All mutating methods (<see cref="LaunchAsync"/>, <see cref="ShutdownAsync"/>,
/// <see cref="RefreshDetectionAsync"/>) are serialised through a
/// SemaphoreSlim so a click-spamming user can't race the subprocess
/// lifecycle into a confused state.
/// </summary>
public class SimulatorService : IDisposable {
    private readonly IEnumerable<ISimulatorBackend> _backends;
    private readonly ILogger<SimulatorService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ISimulatorBackend ActiveBackend { get; }

    /// <summary>Last <see cref="ISimulatorBackend.DetectInstallAsync"/>
    /// result. Null until the first detection completes (kicked off
    /// in the constructor). UI binds to <see cref="GetStatus"/> which
    /// surfaces this safely.</summary>
    public SimulatorInstall? LastDetect { get; private set; }

    public bool IsRunning { get; private set; }
    public IReadOnlyList<string> RunningDevices { get; private set; } = [];
    public DateTime? LaunchedAt { get; private set; }
    public string? LastError { get; private set; }

    public SimulatorService(IEnumerable<ISimulatorBackend> backends,
                            ILogger<SimulatorService> logger) {
        _backends = backends;
        _logger = logger;

        // Pick the first backend that says it supports this OS.
        // Order of registration in DI = priority; today there's one
        // per platform so picking any matching backend is fine.
        ActiveBackend = backends.FirstOrDefault(b => b.IsSupported)
            ?? new NoopSimulatorBackend();
        _logger.LogInformation("Simulator backend selected: {Kind}", ActiveBackend.Kind);

        // Fire detection in the background so the constructor stays
        // cheap. UI shows a loading state until LastDetect populates.
        _ = Task.Run(async () => {
            try { await RefreshDetectionAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Initial simulator detection failed"); }
        });
    }

    public async Task<SimulatorInstall> RefreshDetectionAsync(CancellationToken ct = default) {
        await _lock.WaitAsync(ct);
        try {
            LastDetect = await ActiveBackend.DetectInstallAsync(ct);
            return LastDetect;
        } finally {
            _lock.Release();
        }
    }

    public async Task<bool> LaunchAsync(IReadOnlyList<string> devices, int port,
                                        CancellationToken ct = default) {
        await _lock.WaitAsync(ct);
        try {
            if (LastDetect is null or { Installed: false }) {
                LastError = LastDetect?.Error ?? "Simulator install not detected.";
                return false;
            }
            // Drop devices that aren't actually available on this host
            // (UI might still send a stale list).
            var avail = LastDetect.AvailableDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var filtered = devices.Where(d => avail.Contains(d)).ToList();
            if (filtered.Count == 0) {
                LastError = $"None of the requested devices ({string.Join(",", devices)}) are available on this host.";
                return false;
            }

            var req = new SimulatorLaunchRequest(filtered, port);
            var ok = await ActiveBackend.LaunchAsync(req, ct);
            if (ok) {
                IsRunning = true;
                RunningDevices = filtered;
                LaunchedAt = DateTime.UtcNow;
                LastError = null;
                _logger.LogInformation("Simulator launched: {Devices} on port {Port}",
                    string.Join(",", filtered), port);
            } else {
                LastError = "Backend rejected launch — see Polaris logs for details.";
                _logger.LogWarning("Simulator launch failed: {Devices}", string.Join(",", filtered));
            }
            return ok;
        } finally {
            _lock.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken ct = default) {
        await _lock.WaitAsync(ct);
        try {
            await ActiveBackend.ShutdownAsync(ct);
            IsRunning = false;
            RunningDevices = [];
            LaunchedAt = null;
            LastError = null;
        } finally {
            _lock.Release();
        }
    }

    /// <summary>Cheap TCP probe — periodic health check called from
    /// the auto-start service. Updates <see cref="IsRunning"/> if the
    /// subprocess died between launch and now.</summary>
    public async Task<bool> ProbeRunningAsync(CancellationToken ct = default) {
        var alive = await ActiveBackend.IsRunningAsync(ct);
        if (IsRunning && !alive) {
            _logger.LogWarning("Simulator was running but health probe failed — marking down.");
            IsRunning = false;
            RunningDevices = [];
            LastError = "Simulator process exited unexpectedly.";
        }
        return alive;
    }

    /// <summary>Compose the snapshot the WS payload + REST status
    /// endpoint share. Centralised so the field names stay in sync.</summary>
    public SimulatorStatus GetStatus() => new(
        Kind: ActiveBackend.Kind,
        IsSupported: ActiveBackend.IsSupported,
        Installed: LastDetect?.Installed ?? false,
        Version: LastDetect?.Version,
        DevicesAvailable: LastDetect?.AvailableDevices ?? [],
        IsRunning: IsRunning,
        RunningDevices: RunningDevices,
        LaunchedAt: LaunchedAt,
        LastError: LastError,
        DownloadUrl: ActiveBackend.DownloadInstructionsUrl);

    public void Dispose() {
        _lock.Dispose();
        (ActiveBackend as IDisposable)?.Dispose();
    }
}

/// <summary>Snapshot of where the simulator stack is right now —
/// serialised verbatim into the WS payload and the
/// <c>GET /api/simulator/status</c> response. Keep field names
/// stable; the UI binds to them.</summary>
public record SimulatorStatus(
    string Kind,
    bool IsSupported,
    bool Installed,
    string? Version,
    IReadOnlyList<string> DevicesAvailable,
    bool IsRunning,
    IReadOnlyList<string> RunningDevices,
    DateTime? LaunchedAt,
    string? LastError,
    string DownloadUrl);

/// <summary>Fallback when the host OS matches no registered backend.
/// Reports "unsupported" cleanly instead of throwing; UI shows a
/// "no backend for this OS" banner.</summary>
internal sealed class NoopSimulatorBackend : ISimulatorBackend {
    public string Kind => "none";
    public bool IsSupported => false;
    public string DownloadInstructionsUrl => "";
    public Task<SimulatorInstall> DetectInstallAsync(CancellationToken ct = default)
        => Task.FromResult(new SimulatorInstall(false, null, null, [],
            "No simulator backend supports this OS."));
    public Task<bool> LaunchAsync(SimulatorLaunchRequest req, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> IsRunningAsync(CancellationToken ct = default) => Task.FromResult(false);
}
