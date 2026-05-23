namespace NINA.Polaris.Services.Simulator;

/// <summary>
/// Hosted service that, when <c>UserProfile.SimulatorAutoStart=true</c>,
/// fires the simulator stack ~3s after Polaris boots. Mirrors the
/// <c>PHD2AutoStartService</c> pattern exactly: profile toggle wins,
/// detection happens first, launch is fire-and-forget so a missing
/// indi-bin (or wrong OS) doesn't block startup.
///
/// On top of auto-launch this service also runs a slow periodic
/// health probe so a crashed indiserver gets reflected in the WS
/// status payload within ~30 seconds without the UI polling.
/// </summary>
public class SimulatorAutoStartService : IHostedService, IDisposable {
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HealthProbeInterval = TimeSpan.FromSeconds(30);

    private readonly SimulatorService _sim;
    private readonly ProfileService _profiles;
    private readonly ILogger<SimulatorAutoStartService> _logger;
    private Task? _runner;
    private CancellationTokenSource? _cts;

    public SimulatorAutoStartService(SimulatorService sim,
                                     ProfileService profiles,
                                     ILogger<SimulatorAutoStartService> logger) {
        _sim = sim;
        _profiles = profiles;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runner = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct) {
        try {
            // Stagger to not compete with other hosted services for
            // CPU/disk during cold start. Matches PHD2's 2s + 1s extra
            // because indiserver spawning a handful of driver
            // children is heavier than launching PHD2 alone.
            await Task.Delay(StartupDelay, ct);

            if (_profiles.Active.SimulatorAutoStart) {
                await TryAutoLaunchAsync(ct);
            } else {
                _logger.LogDebug("Simulator auto-start disabled (toggle in Settings to enable).");
            }

            // Keep a slow health-probe loop running regardless of
            // auto-start so a crashed indiserver started manually
            // (or by the user via the Launch button) still gets
            // its IsRunning flipped within ~30s.
            while (!ct.IsCancellationRequested) {
                try {
                    await _sim.ProbeRunningAsync(ct);
                } catch (Exception ex) {
                    _logger.LogTrace(ex, "Simulator health probe failed (ignored)");
                }
                try { await Task.Delay(HealthProbeInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        } catch (OperationCanceledException) { /* shutdown */ }
          catch (Exception ex) {
            _logger.LogError(ex, "Simulator auto-start crashed");
        }
    }

    private async Task TryAutoLaunchAsync(CancellationToken ct) {
        // Refresh detection so a freshly installed indi-bin is picked
        // up without requiring a manual click first.
        var detect = await _sim.RefreshDetectionAsync(ct);
        if (!detect.Installed) {
            _logger.LogWarning("SimulatorAutoStart=true but install not detected: {Error}", detect.Error);
            return;
        }

        var devices = _profiles.Active.SimulatorDevices ?? SimulatorDeviceTags.Defaults;
        var port = _profiles.Active.SimulatorPort > 0
            ? _profiles.Active.SimulatorPort : 7624;
        _logger.LogInformation("Auto-launching simulator stack: {Devices} on port {Port}",
            string.Join(",", devices), port);
        var ok = await _sim.LaunchAsync(devices, port, ct);
        if (!ok) {
            _logger.LogWarning("Simulator auto-launch did not produce a listening service");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _cts?.Cancel();
        if (_runner != null) {
            try { await _runner.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* ignore */ }
        }
        // Don't auto-shutdown the simulator on app stop — user might
        // be restarting Polaris while keeping their simulated rig
        // alive. SimulatorService.Dispose handles real cleanup.
    }

    public void Dispose() {
        _cts?.Dispose();
    }
}
