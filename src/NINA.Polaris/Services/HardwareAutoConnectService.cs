using NINA.INDI.Client;
using NINA.Polaris.Services.Alpaca;

namespace NINA.Polaris.Services;

/// <summary>
/// Boot-time hosted service that, when <c>profile.AutoConnectOnStartup</c>
/// is enabled, dials INDI, runs an Alpaca local-network discovery, and
/// then re-binds + connects every device the active rig has a saved
/// selection for. Each step pushes a <see cref="NotificationService"/>
/// entry so the browser surfaces a toast — the user lands on the page
/// with hardware already connected and a feed of "what just happened
/// before you got here."
///
/// Modeled after <see cref="PHD2AutoStartService"/>: stagger ~2s after
/// host startup so we don't fight other hosted services for CPU /
/// network on cold boot, then run the whole sequence in fire-and-forget
/// Task.Run so we never block IHostedService.StartAsync. Single attempt
/// per boot — if INDI isn't up yet, the user does it manually from the
/// Rigs tab. Replicating the PHD2 retry loop here would race with the
/// user clicking Connect.
/// </summary>
public class HardwareAutoConnectService : IHostedService {
    private readonly IConfiguration _config;
    private readonly IndiClient _indiClient;
    private readonly AlpacaDiscovery _alpaca;
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<HardwareAutoConnectService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _runner;

    public HardwareAutoConnectService(
        IConfiguration config,
        IndiClient indiClient,
        AlpacaDiscovery alpaca,
        EquipmentManager equip,
        ProfileService profiles,
        NotificationService notify,
        ILogger<HardwareAutoConnectService> logger) {
        _config = config;
        _indiClient = indiClient;
        _alpaca = alpaca;
        _equip = equip;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        var enabled = _profiles.Active.AutoConnectOnStartup
                   || _config.GetValue("AutoConnect:OnStartup", false);
        if (!enabled) {
            _logger.LogDebug("Hardware auto-connect disabled (toggle in Settings or set AutoConnect:OnStartup=true)");
            return Task.CompletedTask;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runner = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _cts?.Cancel();
        if (_runner != null) {
            try { await _runner.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); } catch { }
        }
    }

    private async Task RunAsync(CancellationToken ct) {
        try {
            // Stagger so PHD2AutoStartService + SimulatorAutoStartService
            // (also 2-3s staggers) don't all hammer the network at once.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            // -------- INDI --------
            bool indiOk = await TryConnectIndiAsync(ct);

            // -------- Alpaca (independent — runs even if INDI fails) --------
            await TryDiscoverAlpacaAsync(ct);

            // -------- Active rig equipment --------
            // Equipment selections in the rig might point at INDI device
            // names; without INDI they can't be bound. Skip silently when
            // INDI is down. (Alpaca-only rigs would need their own path —
            // not in scope yet; today every Select* binds via _indiClient.)
            if (indiOk) {
                await TryConnectActiveRigAsync(ct);
            }
        } catch (OperationCanceledException) {
            // shutdown
        } catch (Exception ex) {
            _logger.LogError(ex, "Hardware auto-connect crashed");
            _notify.Push("error", "Auto-connect crashed: " + ex.Message);
        }
    }

    private async Task<bool> TryConnectIndiAsync(CancellationToken ct) {
        if (_indiClient.IsConnected) {
            _notify.Push("ok", $"INDI already connected ({_indiClient.Host}:{_indiClient.Port})");
            return true;
        }
        try {
            _notify.Push("info", $"Connecting INDI {_indiClient.Host}:{_indiClient.Port}…", 2500);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            await _indiClient.ConnectAsync(timeoutCts.Token);
            // Devices populate asynchronously as getProperties responses
            // come in. Give the server up to 2s to enumerate before we
            // try to bind rig devices — without this the device list is
            // empty and every Select* lookup misses.
            for (int i = 0; i < 20 && _indiClient.Devices.Count == 0; i++) {
                await Task.Delay(100, ct);
            }
            _notify.Push("ok",
                $"INDI connected ({_indiClient.Host}:{_indiClient.Port}) · {_indiClient.Devices.Count} device(s)");
            return true;
        } catch (Exception ex) {
            _logger.LogInformation(ex, "Auto-connect to INDI {Host}:{Port} failed", _indiClient.Host, _indiClient.Port);
            _notify.Push("warn", $"INDI unavailable at {_indiClient.Host}:{_indiClient.Port} — connect manually from Rigs.");
            return false;
        }
    }

    private async Task TryDiscoverAlpacaAsync(CancellationToken ct) {
        try {
            _notify.Push("info", "Discovering Alpaca devices on local network…", 2500);
            var servers = await _alpaca.DiscoverServersAsync(TimeSpan.FromSeconds(3));
            int deviceCount = servers.Sum(s => s.Devices?.Count ?? 0);
            if (servers.Count == 0) {
                _notify.Push("info", "No Alpaca devices found on the LAN.");
            } else {
                _notify.Push("ok",
                    $"Alpaca: {servers.Count} server(s), {deviceCount} device(s) discovered.");
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Alpaca discovery failed");
            _notify.Push("warn", "Alpaca discovery failed: " + ex.Message);
        }
    }

    private async Task TryConnectActiveRigAsync(CancellationToken ct) {
        var rig = _profiles.ActiveEquipmentProfile;
        if (rig == null) {
            _notify.Push("info", "No active rig — skipping equipment auto-connect.");
            return;
        }

        var available = new HashSet<string>(_indiClient.GetDeviceNames(), StringComparer.OrdinalIgnoreCase);

        // Each entry: friendly name shown in the toast + saved device
        // name from the rig + the bind+connect callback. Camera and
        // Telescope honour driver override; the rest are INDI-only
        // today so we don't pass a driver.
        var devices = new (string Label, string? Name, Func<string, Task> Bind)[] {
            ("Camera",       rig.Camera,      async name => { var c = _equip.SelectCamera(rig.CameraDriver ?? "indi", name);    await c.ConnectAsync(ct); }),
            ("Mount",        rig.Telescope,   async name => { var t = _equip.SelectTelescope(rig.TelescopeDriver ?? "indi", name); await t.ConnectAsync(ct); }),
            ("Focuser",      rig.Focuser,     async name => { var f = _equip.SelectFocuser(name);                                await f.ConnectAsync(ct); }),
            ("Filter wheel", rig.FilterWheel, async name => { var w = _equip.SelectFilterWheel(name);                            await w.ConnectAsync(ct); }),
            ("Rotator",      rig.Rotator,     async name => { var r = _equip.SelectRotator(name);                                await r.ConnectAsync(ct); }),
            ("Flat panel",   rig.FlatDevice,  async name => { var p = _equip.SelectFlatDevice(name);                             await p.ConnectAsync(ct); }),
            ("Dome",         rig.Dome,        async name => { var d = _equip.SelectDome(name);                                   await d.ConnectAsync(ct); }),
            ("Weather",      rig.Weather,     async name => { var w = _equip.SelectWeather(name);                                await w.ConnectAsync(ct); }),
        };

        int connected = 0, missing = 0, failed = 0;
        foreach (var (label, name, bind) in devices) {
            if (string.IsNullOrWhiteSpace(name)) continue;

            // For INDI-backed devices, validate the saved name still
            // exists on the live server before attempting connect. For
            // non-INDI camera/mount drivers (e.g. canon-edsdk), trust
            // the binder — the available[] set is INDI-only.
            bool isIndi = label switch {
                "Camera" => (rig.CameraDriver ?? "indi") == "indi",
                "Mount"  => (rig.TelescopeDriver ?? "indi") == "indi",
                _        => true,
            };
            if (isIndi && !available.Contains(name)) {
                _notify.Push("warn", $"{label} '{name}' not present on INDI server.");
                missing++;
                continue;
            }

            try {
                await bind(name);
                _notify.Push("ok", $"{label} connected: {name}");
                connected++;
            } catch (Exception ex) {
                _logger.LogInformation(ex, "Auto-connect of {Label} '{Name}' failed", label, name);
                _notify.Push("warn", $"{label} '{name}' failed: {ex.Message}");
                failed++;
            }
        }

        if (connected == 0 && missing == 0 && failed == 0) {
            _notify.Push("info", "Active rig has no saved device selections — pick devices in Rigs.");
        } else {
            _notify.Push("ok",
                $"Rig '{rig.Name}': {connected} connected, {missing} missing, {failed} failed.");
        }
    }
}
