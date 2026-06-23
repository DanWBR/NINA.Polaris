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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// The "simple" sequencer engine, flat list of <see cref="SequenceItem"/>s
/// (target, filter, exposure, count) executed in order. This is what
/// the AUTORUN tab drives. The tree-based <c>AdvancedSequencer</c>
/// lives under <c>Services/Sequencer/</c> and serves the ADV tab.
///
/// The engine coordinates the full capture loop: filter swap → camera
/// expose → save to disk → live-stack push → PHD2 dither (when
/// triggered) → meridian-flip check (delegating to
/// <see cref="MeridianFlipService"/>). State is exposed through
/// public properties + polled by <c>StatusStreamHandler</c> at 1 Hz
/// so the UI can render progress without subscribing per-frame.
///
/// Pause/Resume uses a <see cref="SemaphoreSlim"/> gate; Abort cancels
/// the run-task's <see cref="CancellationTokenSource"/>. State
/// transitions are protected only by the single-task nature of the
/// run, at most one capture is in flight at any time.
/// </summary>
public class SequenceEngine {
    private readonly EquipmentManager _equip;
    private readonly ImageRelayService _relay;
    private readonly LiveStackingService _liveStack;
    private readonly PHD2Client _phd2;
    // Dithering/guider control goes through the active guider (native or PHD2),
    // not the concrete PHD2 client, so dither-every-N-frames works on both.
    private readonly ActiveGuiderProvider _guiders;
    private readonly MeridianFlipService _meridianFlip;
    private readonly ImageWriterService _imageWriter;
    private readonly ProfileService _profile;
    private readonly ILogger<SequenceEngine> _logger;

    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private Task? _runTask;

    /// <summary>Counter of frames captured since last dither (across all items).</summary>
    private int _framesSinceDither;

    /// <summary>Tracks the loaded filter + applied focuser offset so filter
    /// changes move the wheel and apply the offset as a delta (see FilterSwitcher).</summary>
    private readonly FilterState _filterState = new();

    public List<SequenceItem> Items { get; private set; } = [];
    public SequenceState State { get; private set; } = SequenceState.Idle;
    public int CurrentItemIndex { get; private set; } = -1;
    public int CurrentFrameInItem { get; private set; }
    public int TotalFramesCompleted { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? StartedAt { get; private set; }

    /// <summary>Dither configuration. Default: disabled.</summary>
    public DitherSettings Dither { get; set; } = new();

    /// <summary>How many dithers were issued in the current run (diagnostic).</summary>
    public int DithersIssued { get; private set; }

    /// <summary>End-of-run housekeeping (park, warm, etc). Default: nothing.</summary>
    public SequenceEndActions EndActions { get; set; } = new();

    private readonly NINA.Polaris.Services.External.GraXpertService _graXpert;
    private readonly FlatWizardService _flatWizard;
    private readonly CaptureProgressService _captureProgress;
    private readonly AuxCaptureService _aux;

    public SequenceEngine(EquipmentManager equip, ImageRelayService relay,
        LiveStackingService liveStack, PHD2Client phd2, ActiveGuiderProvider guiders,
        MeridianFlipService meridianFlip,
        ImageWriterService imageWriter,
        NINA.Polaris.Services.External.GraXpertService graXpert,
        FlatWizardService flatWizard,
        ProfileService profile,
        CaptureProgressService captureProgress,
        AuxCaptureService aux,
        ILogger<SequenceEngine> logger) {
        _equip = equip;
        _relay = relay;
        _liveStack = liveStack;
        _phd2 = phd2;
        _guiders = guiders;
        _meridianFlip = meridianFlip;
        _imageWriter = imageWriter;
        _graXpert = graXpert;
        _flatWizard = flatWizard;
        _profile = profile;
        _captureProgress = captureProgress;
        _aux = aux;
        _logger = logger;
    }

    public void LoadSequence(List<SequenceItem> items) {
        if (State == SequenceState.Running)
            throw new InvalidOperationException("Cannot load sequence while running");

        Items = items;
        CurrentItemIndex = -1;
        CurrentFrameInItem = 0;
        TotalFramesCompleted = 0;
        LastError = null;
        State = SequenceState.Idle;
        _logger.LogInformation("Sequence loaded: {Count} items, {Frames} total frames",
            items.Count, items.Sum(i => i.Count));
    }

    /// <summary>Reset run progress to the start WITHOUT touching the loaded
    /// items. Used by the "restart" choice when a partially-completed run is
    /// started again; "continue" simply calls Start() which resumes from the
    /// retained CurrentItemIndex/CurrentFrameInItem.</summary>
    public void ResetProgress() {
        if (State == SequenceState.Running) return;
        CurrentItemIndex = -1;
        CurrentFrameInItem = 0;
        TotalFramesCompleted = 0;
        LastError = null;
    }

    /// <summary>True when a previous run left partial progress that can be
    /// resumed: at least one frame done but not all enabled frames, and not
    /// currently running.</summary>
    public bool HasResumableProgress {
        get {
            if (State == SequenceState.Running) return false;
            var total = Items.Where(i => i.Enabled).Sum(i => i.Count);
            return TotalFramesCompleted > 0 && TotalFramesCompleted < total;
        }
    }

    public void Start() {
        if (State == SequenceState.Running) return;

        if (Items.Count == 0) {
            LastError = "No items in sequence";
            return;
        }

        _cts = new CancellationTokenSource();
        State = SequenceState.Running;
        StartedAt = DateTime.UtcNow;
        LastError = null;
        _framesSinceDither = 0;
        DithersIssued = 0;
        // Reset filter tracking so the first filtered item moves the wheel +
        // applies its offset from a clean baseline.
        _filterState.CurrentFilter = null;
        _filterState.AppliedOffset = 0;
        _imageWriter.ResetSessionCounter();

        if (_pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        _runTask = Task.Run(() => RunAsync(_cts.Token));
        _logger.LogInformation("Sequence started (dither: {Enabled}, every {N} frames, {Px}px)",
            Dither.Enabled, Dither.EveryNFrames, Dither.Pixels);
    }

    public void Pause() {
        if (State != SequenceState.Running) return;

        if (_pauseGate.CurrentCount > 0)
            _pauseGate.Wait(0);

        State = SequenceState.Paused;
        _logger.LogInformation("Sequence paused at item {Index}, frame {Frame}",
            CurrentItemIndex, CurrentFrameInItem);
    }

    public void Resume() {
        if (State != SequenceState.Paused) return;

        State = SequenceState.Running;
        if (_pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        _logger.LogInformation("Sequence resumed");
    }

    public void Stop() {
        if (State == SequenceState.Idle) return;

        _cts?.Cancel();

        if (State == SequenceState.Paused && _pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        State = SequenceState.Idle;
        _logger.LogInformation("Sequence stopped");
    }

    public SequenceStatus GetStatus() {
        // Disabled items are skipped at run time, so they don't count
        // toward the total / progress / ETA either.
        var totalFrames = Items.Where(i => i.Enabled).Sum(i => i.Count);
        var elapsed = StartedAt.HasValue ? DateTime.UtcNow - StartedAt.Value : TimeSpan.Zero;

        double estimatedRemainingSeconds = 0;
        if (TotalFramesCompleted > 0 && totalFrames > TotalFramesCompleted) {
            var avgFrameTime = elapsed.TotalSeconds / TotalFramesCompleted;
            estimatedRemainingSeconds = avgFrameTime * (totalFrames - TotalFramesCompleted);
        }

        return new SequenceStatus {
            State = State.ToString().ToLowerInvariant(),
            Items = Items.Select((item, i) => new SequenceItemStatus {
                Name = item.Name,
                Exposure = item.Exposure,
                Count = item.Count,
                Completed = i < CurrentItemIndex ? item.Count :
                            i == CurrentItemIndex ? CurrentFrameInItem : 0,
                IsActive = i == CurrentItemIndex && State == SequenceState.Running
            }).ToList(),
            CurrentItemIndex = CurrentItemIndex,
            CurrentFrameInItem = CurrentFrameInItem,
            TotalFrames = totalFrames,
            TotalFramesCompleted = TotalFramesCompleted,
            ElapsedSeconds = elapsed.TotalSeconds,
            EstimatedRemainingSeconds = estimatedRemainingSeconds,
            LastError = LastError,
            DithersIssued = DithersIssued,
            FramesSinceDither = _framesSinceDither,
            Dither = Dither,
            EndActions = EndActions
        };
    }

    private async Task RunAsync(CancellationToken ct) {
        // Run the auxiliary camera capture loop alongside the sequence.
        try { _aux.NotifySessionActive(true); } catch { }
        try {
            // Resume point captured ONCE up front. CurrentItemIndex is rewritten
            // on every iteration below, so the per-item start-frame check must
            // compare against this snapshot — otherwise every item looked like
            // "the resumed item" and inherited the previous item's finished
            // frame counter, skipping all its frames (the 2nd-card-skipped bug).
            int resumeItem = Math.Max(0, CurrentItemIndex);
            int resumeFrame = Math.Max(0, CurrentFrameInItem);
            for (int i = resumeItem; i < Items.Count; i++) {
                ct.ThrowIfCancellationRequested();
                CurrentItemIndex = i;
                var item = Items[i];

                // Disabled items are kept in the schedule for editing but
                // skipped here (and excluded from the frame totals below).
                if (!item.Enabled) {
                    _logger.LogInformation("Sequence item {Index}/{Total}: {Name} is disabled, skipping",
                        i + 1, Items.Count, item.Name);
                    continue;
                }

                // BIAS frames are zero-second exposures by definition. If the
                // UI somehow sent a non-zero exposure, clamp it, saves the
                // user from wasting time on an obvious mistake.
                var imageType = (item.ImageType ?? "LIGHT").Trim().ToUpperInvariant();
                if (imageType == "BIAS") item.Exposure = 0;
                bool isCalibration = imageType is "DARK" or "BIAS" or "FLAT" or "DARKFLAT";

                _logger.LogInformation("Sequence item {Index}/{Total}: {Name} ({Type} {Exposure}s x {Count})",
                    i + 1, Items.Count, item.Name, imageType, item.Exposure, item.Count);

                // Slew only for LIGHT frames with explicit coords. Calibration
                // frames either don't care where the scope is pointed (darks,
                // bias) or rely on an external flat panel (flat).
                if (!isCalibration
                    && item.Ra.HasValue && item.Dec.HasValue
                    && _equip.Telescope != null) {
                    _logger.LogInformation("Slewing to {Name} (RA={Ra:F4}, Dec={Dec:F4})",
                        item.Name, item.Ra, item.Dec);

                    try {
                        await _equip.Telescope.SlewAsync(item.Ra.Value, item.Dec.Value, ct);
                        await WaitForSlewComplete(ct);
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Slew failed for {Name}, continuing with capture", item.Name);
                    }
                }

                // Set binning if specified
                if (item.Binning > 0 && _equip.Camera != null) {
                    try {
                        await _equip.Camera.SetBinningAsync(item.Binning, item.Binning, ct);
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Set binning failed");
                    }
                }

                // Switch the filter wheel + apply its focuser offset (as a delta
                // from the previous filter) before this item's frames. BIAS/DARK
                // items usually carry no filter, so this no-ops for them. Best
                // effort: a filter/focuser glitch never aborts the run.
                if (!string.IsNullOrWhiteSpace(item.Filter)) {
                    await FilterSwitcher.ApplyAsync(
                        _equip.FilterWheel, _equip.Focuser,
                        _profile.ActiveEquipmentProfile?.FilterOffsets,
                        item.Filter, _filterState, _logger, ct);
                }

                // FLAT + AutoExposure: ask the wizard to resolve the
                // exposure for this (filter, binning) before we enter
                // the capture loop. Try the trained cache first (fast
                // path; convergence skipped entirely). On miss, run the
                // search and write the result back; subsequent sessions
                // hit the fast path. On failure we SKIP the flat item
                // rather than fall back to the user's exposure: that value
                // is a light-frame length (e.g. 60 s), so shooting flats at
                // it just produces saturated frames that corrupt the master
                // flat. Skipping with a clear error is the safer outcome.
                if (imageType == "FLAT" && item.AutoExposure && _equip.Camera != null) {
                    var filterKey = item.Filter ?? "";
                    var binKey = Math.Max(1, item.Binning);
                    if (_flatWizard.TryGetTrainedExposure(filterKey, binKey, out var cachedExp)) {
                        _logger.LogInformation(
                            "Auto-flat: using trained exposure {Exp}s for filter '{F}' bin{B}",
                            cachedExp, filterKey, binKey);
                        item.Exposure = cachedExp;
                    } else {
                        _logger.LogInformation(
                            "Auto-flat: no trained exposure for filter '{F}' bin{B}, searching...",
                            filterKey, binKey);
                        double? found;
                        try {
                            found = await _flatWizard.AutoFindExposureAsync(
                                filterKey, binKey, ct: ct);
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception ex) {
                            _logger.LogError(ex,
                                "Auto-flat search threw for '{F}' bin{B}; skipping this flat set",
                                filterKey, binKey);
                            LastError = $"Auto-flat failed for '{filterKey}': {ex.Message}";
                            continue;
                        }
                        if (found.HasValue) {
                            item.Exposure = found.Value;
                        } else {
                            _logger.LogWarning(
                                "Auto-flat did not converge for '{F}' bin{B} (panel too bright/dim for the search range); skipping this flat set",
                                filterKey, binKey);
                            LastError = $"Auto-flat for '{filterKey}' could not reach the target ADU; flat set skipped";
                            continue;
                        }
                    }
                }

                // Capture frames
                int startFrame = (i == Math.Max(0, CurrentItemIndex)) ? CurrentFrameInItem : 0;
                for (int f = startFrame; f < item.Count; f++) {
                    ct.ThrowIfCancellationRequested();

                    // Check pause gate
                    await _pauseGate.WaitAsync(ct);
                    _pauseGate.Release();

                    // Meridian flip check, meaningful only for LIGHT frames
                    // pointed at a real target.
                    if (!isCalibration
                        && item.Ra.HasValue && item.Dec.HasValue
                        && _meridianFlip.Settings.Enabled
                        && _meridianFlip.ShouldFlipNow(item.Ra.Value)) {
                        _logger.LogInformation("Meridian flip due for target {Name}, executing", item.Name);
                        await _meridianFlip.ExecuteFlipAsync(item.Ra.Value, item.Dec.Value, ct);
                    }

                    CurrentFrameInItem = f;

                    if (_equip.Camera == null) {
                        LastError = "No camera connected";
                        _logger.LogError("Sequence aborted: no camera");
                        State = SequenceState.Idle;
                        return;
                    }

                    _logger.LogDebug("Capturing frame {Frame}/{Total} for {Name}",
                        f + 1, item.Count, item.Name);

                    // Push the item's gain + binning to the camera on every
                    // frame. Without this the driver kept whatever gain it had
                    // (often a low/8-bit default), so 60 s lights came back
                    // near-black even though item.Gain was only being stamped
                    // into the FITS header at save time.
                    // Offset is a per-rig setting (DefaultOffset), not per-item:
                    // a sensible bias pedestal keeps the background off the
                    // left wall of the histogram. Sent on every frame alongside
                    // gain so the camera isn't left on a stale/zero offset.
                    var rigOffset = _profile.ActiveEquipmentProfile?.DefaultOffset ?? 0;
                    var capOpts = new NINA.Image.Interfaces.CaptureOptions(
                        Gain: item.Gain > 0 ? item.Gain : (int?)null,
                        Offset: rigOffset > 0 ? rigOffset : (int?)null,
                        BinX: item.Binning > 0 ? item.Binning : (int?)null,
                        BinY: item.Binning > 0 ? item.Binning : (int?)null,
                        ImageType: imageType,
                        Filter: string.IsNullOrEmpty(item.Filter) ? null : item.Filter,
                        TargetName: string.IsNullOrEmpty(item.Name) ? null : item.Name);

                    bool frameOk = false;
                    try {
                        NINA.Image.Interfaces.IImageData imageData;
                        using (_captureProgress.Begin("autorun", item.Exposure))
                            imageData = await CameraCaptureGate.RunAsync(
                            () => _equip.Camera.CaptureAsync(item.Exposure, capOpts, ct), ct);

                        // Populate exposure-level metadata before saving / relaying
                        imageData.MetaData.Exposure.ExposureTime = item.Exposure;
                        if (!string.IsNullOrEmpty(item.Filter))
                            imageData.MetaData.Exposure.Filter = item.Filter;
                        if (!string.IsNullOrEmpty(item.Name))
                            imageData.MetaData.Target.Name = item.Name;
                        if (item.Ra.HasValue) imageData.MetaData.Target.RightAscension = item.Ra.Value;
                        if (item.Dec.HasValue) imageData.MetaData.Target.Declination = item.Dec.Value;

                        // Persist to disk with extended FITS headers (no-op if no output dir).
                        // imageType controls the calibration/light subfolder split in BuildSubDir.
                        var savedPath = _imageWriter.SaveImage(imageData, targetName: item.Name,
                            imageType: imageType, gain: item.Gain);

                        // Auto-GraXpert BGE hook. Fire-and-forget so the
                        // next exposure doesn't wait on the ~10s BGE pass.
                        // Only LIGHT frames + only when the user opted in
                        // + only when GraXpert is actually installed. Decon
                        // and Denoise never auto-run, they hurt SNR on
                        // individual lights and are best on integrated
                        // masters; offered manually in STUDIO instead.
                        if (EndActions.AutoGraXpert
                            && !string.IsNullOrEmpty(savedPath)
                            && !isCalibration
                            && _graXpert.IsAvailable) {
                            var fileToProcess = savedPath!;
                            _ = Task.Run(async () => {
                                try {
                                    var opts = new NINA.Polaris.Services.External.GraXpertOptions(
                                        Operation: NINA.Polaris.Services.External.GraXpertOperation.BackgroundExtraction);
                                    var res = await _graXpert.ProcessFrameAsync(
                                        fileToProcess, opts, CancellationToken.None);
                                    if (!string.IsNullOrEmpty(res.Error)) {
                                        _logger.LogWarning("Auto-GraXpert failed for {Path}: {Err}",
                                            fileToProcess, res.Error);
                                    }
                                } catch (Exception ex) {
                                    _logger.LogWarning(ex, "Auto-GraXpert hook threw for {Path}", fileToProcess);
                                }
                            });
                        }

                        // AUTORUN frames are saved to disk and shown in the
                        // preview, but are NOT fed into the LIVE-tab stacking
                        // accumulator. Live stacking is its own EAA loop driven
                        // by the LIVE tab; routing scheduled-capture frames into
                        // it would corrupt the stack and fire the live-stack
                        // triggers, and calibration frames (BIAS/DARK/FLAT) must
                        // never be stacked at all. Relay as Autorun so the frame
                        // lands on the AUTORUN preview only, never the LIVE canvas.
                        await _relay.RelayImageAsync(imageData, FrameKind.Autorun, ct);

                        CurrentFrameInItem = f + 1;
                        TotalFramesCompleted++;
                        frameOk = true;
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Frame {Frame} capture failed for {Name}, retrying once",
                            f + 1, item.Name);

                        // Single retry after brief pause
                        try {
                            await Task.Delay(2000, ct);
                            NINA.Image.Interfaces.IImageData imageData;
                            using (_captureProgress.Begin("autorun", item.Exposure))
                                imageData = await CameraCaptureGate.RunAsync(
                            () => _equip.Camera.CaptureAsync(item.Exposure, capOpts, ct), ct);

                            // Preview only (see note above): AUTORUN never feeds
                            // the LIVE-tab stacking accumulator, and routes to the
                            // AUTORUN preview canvas (FrameKind.Autorun), not LIVE.
                            await _relay.RelayImageAsync(imageData, FrameKind.Autorun, ct);

                            CurrentFrameInItem = f + 1;
                            TotalFramesCompleted++;
                            frameOk = true;
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception retryEx) {
                            _logger.LogError(retryEx, "Retry also failed for frame {Frame}, skipping", f + 1);
                            LastError = $"Frame {f + 1} of {item.Name} failed: {retryEx.Message}";
                        }
                    }

                    // Dither between frames (only after a successful capture, only
                    // if this isn't the very last frame of the very last item, and
                    // only for LIGHT, dithering darks/flats would corrupt the
                    // calibration master and hammer the mount needlessly).
                    if (frameOk && !isCalibration) {
                        _framesSinceDither++;
                        bool moreFramesComing = (f + 1 < item.Count) || (i + 1 < Items.Count);
                        if (moreFramesComing) {
                            await MaybeDitherAsync(ct);
                        }
                    }
                }

                _logger.LogInformation("Completed item: {Name}", item.Name);
            }

            State = SequenceState.Idle;
            _logger.LogInformation("Sequence completed: {Frames} frames in {Elapsed}",
                TotalFramesCompleted,
                StartedAt.HasValue ? (DateTime.UtcNow - StartedAt.Value).ToString(@"hh\:mm\:ss") : "??");

            // Natural completion always fires the end-actions.
            await RunEndActionsAsync(triggeredByStop: false);

        } catch (OperationCanceledException) {
            _logger.LogInformation("Sequence cancelled");
            // Stop is a user action, only run housekeeping if the user opted in.
            if (EndActions.RunOnStop) {
                await RunEndActionsAsync(triggeredByStop: true);
            }
        } catch (Exception ex) {
            LastError = ex.Message;
            State = SequenceState.Idle;
            _logger.LogError(ex, "Sequence failed");
            // Failure: still try housekeeping so the rig isn't left tracking unattended.
            await RunEndActionsAsync(triggeredByStop: true);
        } finally {
            try { _aux.NotifySessionActive(false); } catch { }
        }
    }

    /// <summary>
    /// Run the configured post-sequence actions. All failures are caught + logged;
    /// one broken action does not prevent the next from being tried. Uses a fresh
    /// cancellation token so a sequence-stop cannot cancel the cleanup itself.
    /// </summary>
    private async Task RunEndActionsAsync(bool triggeredByStop) {
        var ea = EndActions;
        if (ea == null) return;
        if (!ea.ParkMount && !ea.StopTracking && !ea.WarmCamera && !ea.DisconnectGuider) return;

        _logger.LogInformation("Running end-of-sequence actions (triggeredByStop={Stop})", triggeredByStop);
        using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Park supersedes stop-tracking, parking implies tracking off, and most
        // mounts refuse the explicit tracking-off command after they're parked.
        if (ea.ParkMount && _equip.Telescope != null) {
            try {
                _logger.LogInformation("End-action: parking mount");
                await _equip.Telescope.ParkAsync(ct.Token);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "End-action park failed");
            }
        } else if (ea.StopTracking && _equip.Telescope != null) {
            try {
                _logger.LogInformation("End-action: stopping tracking");
                await _equip.Telescope.SetTrackingAsync(false, ct.Token);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "End-action stop-tracking failed");
            }
        }

        if (ea.WarmCamera && _equip.Camera != null) {
            try {
                _logger.LogInformation("End-action: warming camera (cooler off)");
                await _equip.Camera.SetCoolerAsync(false, ct.Token);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "End-action warm-camera failed");
            }
        }

        var endGuider = _guiders.Active;
        if (ea.DisconnectGuider && endGuider.IsConnected) {
            try {
                _logger.LogInformation("End-action: stopping guiding ({Backend})", endGuider.Backend);
                await endGuider.StopAsync();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "End-action stop-guider failed");
            }
        }
    }

    private async Task WaitForSlewComplete(CancellationToken ct) {
        if (_equip.Telescope == null) return;

        for (int i = 0; i < 300; i++) {
            ct.ThrowIfCancellationRequested();
            if (!_equip.Telescope.IsSlewing) return;
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Slew did not complete within 5 minutes");
    }

    /// <summary>
    /// Issue a dither command via PHD2 if all preconditions are met and we've
    /// hit the configured frame cadence. Waits for SettleDone before returning.
    /// Silently skips when conditions aren't met, never aborts the sequence.
    /// </summary>
    private async Task MaybeDitherAsync(CancellationToken ct) {
        if (!Dither.Enabled) return;
        if (Dither.EveryNFrames <= 0) return;
        if (_framesSinceDither < Dither.EveryNFrames) return;

        // Route through the active guider (native or external PHD2) so the
        // dither-every-N-frames cadence works on whichever backend is selected.
        var g = _guiders.Active;

        if (!g.IsConnected) {
            _logger.LogDebug("Dither skipped: guider ({Backend}) not connected", g.Backend);
            _framesSinceDither = 0;
            return;
        }

        if (!g.IsGuiding) {
            _logger.LogDebug("Dither skipped: guider not guiding (state={State})", g.AppState);
            _framesSinceDither = 0;
            return;
        }

        _logger.LogInformation("Dithering {Px}px (after {N} frames, raOnly={RaOnly}, backend={Backend})",
            Dither.Pixels, _framesSinceDither, Dither.RaOnly, g.Backend);

        // Hook up SettleDone before we issue the dither to avoid race
        var settled = new TaskCompletionSource<SettleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSettled(SettleResult r) => settled.TrySetResult(r);
        g.Settled += OnSettled;

        try {
            await g.DitherAsync(
                pixels: Dither.Pixels,
                raOnly: Dither.RaOnly,
                settlePixels: Dither.SettlePixels,
                settleTime: Dither.SettleTime,
                settleTimeout: Dither.SettleTimeout,
                ct: ct);

            DithersIssued++;

            // Wait for SettleDone with a hard ceiling = configured timeout + 5s grace
            var maxWait = TimeSpan.FromSeconds(Dither.SettleTimeout + 5);
            using var settleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            settleCts.CancelAfter(maxWait);

            try {
                var result = await settled.Task.WaitAsync(settleCts.Token);
                if (result.Status == 0) {
                    _logger.LogInformation("Dither settled OK ({Total} frames, {Dropped} dropped)",
                        result.TotalFrames, result.DroppedFrames);
                } else {
                    _logger.LogWarning("Dither settle returned status {Status}: {Error}",
                        result.Status, result.Error);
                }
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                _logger.LogWarning("Dither settle timed out after {Sec}s, continuing sequence anyway",
                    Dither.SettleTimeout);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Dither command failed, continuing sequence without dither");
        } finally {
            _phd2.Settled -= OnSettled;
            _framesSinceDither = 0;
        }
    }
}
