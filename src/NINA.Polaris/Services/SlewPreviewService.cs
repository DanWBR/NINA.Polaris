using NINA.Polaris.Services.Planetary;

namespace NINA.Polaris.Services;

/// <summary>
/// Auto-runs the camera stream while the mount is slewing, so the user
/// can see what the camera sees as it sweeps the sky. Politely yields
/// the camera whenever any other capture is active (sequence, AF,
/// preview snap, manual stream, recording, meridian flip, flat wizard).
///
/// Lifecycle: BackgroundService polls every 1s. Toggle via the
/// <see cref="Enabled"/> property (persisted in profile through the
/// settings endpoint).
/// </summary>
public class SlewPreviewService : BackgroundService {
    private readonly EquipmentManager _equip;
    private readonly CameraStreamService _stream;
    private readonly SequenceEngine _sequence;
    private readonly AutoFocusService _autoFocus;
    private readonly MeridianFlipService _flip;
    private readonly FlatWizardService _flatWizard;
    private readonly VideoRecordingService _recording;
    private readonly ILogger<SlewPreviewService> _logger;

    public bool Enabled { get; set; } = true;
    public bool IsPreviewActive { get; private set; }
    public bool LastDecision_Slewing { get; private set; }
    public bool LastDecision_CaptureIdle { get; private set; }
    public DateTime? LastCheckedAt { get; private set; }
    public string? LastError { get; private set; }

    public SlewPreviewService(EquipmentManager equip,
                              CameraStreamService stream,
                              SequenceEngine sequence,
                              AutoFocusService autoFocus,
                              MeridianFlipService flip,
                              FlatWizardService flatWizard,
                              VideoRecordingService recording,
                              ILogger<SlewPreviewService> logger) {
        _equip = equip;
        _stream = stream;
        _sequence = sequence;
        _autoFocus = autoFocus;
        _flip = flip;
        _flatWizard = flatWizard;
        _recording = recording;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // Brief startup stagger so PHD2AutoStart / EquipmentManager have
        // time to populate before we start asking them questions.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested) {
            try { await TickAsync(); }
            catch (Exception ex) {
                LastError = ex.Message;
                _logger.LogDebug(ex, "Slew preview tick failed (non-fatal)");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
        // Service shutdown, leave any stream alone if user started it.
        if (IsPreviewActive && _streamWasStartedByUs) {
            try { await _stream.StopAsync(); } catch { }
        }
    }

    private bool _streamWasStartedByUs;

    private async Task TickAsync() {
        LastCheckedAt = DateTime.UtcNow;

        if (!Enabled) {
            if (IsPreviewActive && _streamWasStartedByUs) {
                await _stream.StopAsync();
                IsPreviewActive = false;
                _streamWasStartedByUs = false;
            }
            return;
        }

        var slewing = _equip.Telescope?.IsConnected == true && _equip.Telescope.IsSlewing;
        LastDecision_Slewing = slewing;

        // Capture-idle aggregator: every surface that touches the
        // camera must be quiet. If we missed one and slew preview
        // collides with an active capture, the surface owner wins,
        // we just don't try to start while it's busy.
        var captureIdle =
            (_sequence?.GetStatus().State?.Equals("running", StringComparison.OrdinalIgnoreCase) != true) &&
            (_autoFocus?.State.ToString().Equals("Idle", StringComparison.OrdinalIgnoreCase) == true) &&
            _flip.State.ToString().Equals("Idle", StringComparison.OrdinalIgnoreCase) &&
            _flatWizard.State.ToString().Equals("Idle", StringComparison.OrdinalIgnoreCase) &&
            !_recording.IsRecording &&
            // Stream may already be on, only consider idle when nobody
            // else started it (we own _streamWasStartedByUs).
            (!_stream.IsRunning || _streamWasStartedByUs);
        LastDecision_CaptureIdle = captureIdle;

        if (slewing && captureIdle && !IsPreviewActive) {
            // Start preview, short exposure, default gain. Use loop
            // fallback by default (consistent fps even on cameras
            // without native streaming).
            try {
                _stream.Start(new StreamConfig(ExposureSeconds: 0.1));
                IsPreviewActive = true;
                _streamWasStartedByUs = true;
                _logger.LogInformation("Slew preview: stream ON ({Mode})", _stream.Mode);
            } catch (Exception ex) {
                LastError = ex.Message;
                _logger.LogDebug(ex, "Slew preview start failed");
            }
            return;
        }

        if (IsPreviewActive && _streamWasStartedByUs && (!slewing || !captureIdle)) {
            try {
                await _stream.StopAsync();
                _logger.LogInformation("Slew preview: stream OFF ({Slew} / {Idle})", slewing, captureIdle);
            } catch (Exception ex) {
                LastError = ex.Message;
                _logger.LogDebug(ex, "Slew preview stop failed");
            }
            IsPreviewActive = false;
            _streamWasStartedByUs = false;
        }
    }
}
