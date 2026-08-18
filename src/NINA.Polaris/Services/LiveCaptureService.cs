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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Server-owned LIVE capture loop — the only LIVE loop. The LIVE shutter always
/// starts/stops this; the server is the orchestrator of the LIVE session: it
/// drives every exposure and keeps going even if the browser disconnects or the
/// tab is backgrounded. The client's WASM live-stacking is then pure compute
/// offload — it consumes the relayed frames but no longer controls the cadence.
/// Where stacking runs (server-full vs client-WASM) is the orthogonal
///
/// Frame path is identical to the client-driven <c>POST /api/camera/capture</c>
/// loop: when the LIVE stacker is running the frame is fed to it
/// (<see cref="LiveStackingService.AddFrameAsync"/>, which itself relays the
/// stacked/raw result + fires FrameIntegrated → triggers / save-to-disk),
/// otherwise the raw frame is relayed as <see cref="FrameKind.Live"/> so the
/// LIVE canvas + client stacker still update. Each exposure is wrapped in
/// <see cref="CaptureProgressService"/> so the unified "Xs of Ys" countdown
/// is server-driven here too.
///
/// Between frames the loop pauses while the guider is dithering/settling or
/// an auto-focus run is active, so the trigger work owns the camera and a
/// dither lands cleanly between subs (parity with the client loop's
/// "dither only after the current capture" behaviour).
/// </summary>
public sealed class LiveCaptureService {
    private readonly EquipmentManager _equip;
    private readonly LiveStackingService _liveStack;
    private readonly ImageRelayService _relay;
    private readonly CaptureProgressService _captureProgress;
    private readonly ActiveGuiderProvider _guiders;
    private readonly AutoFocusService _autoFocus;
    private readonly AuxCaptureService _aux;
    private readonly CameraReadyGate _cameraReady;
    private readonly MeridianFlipService _meridian;
    private readonly DitherBarrier _barrier;
    private readonly ILogger<LiveCaptureService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public double ExposureSeconds { get; private set; }
    public int Gain { get; private set; }
    public int BinX { get; private set; }
    public long FrameCount { get; private set; }
    public string? LastError { get; private set; }

    public LiveCaptureService(EquipmentManager equip, LiveStackingService liveStack,
        ImageRelayService relay, CaptureProgressService captureProgress,
        ActiveGuiderProvider guiders, AutoFocusService autoFocus,
        AuxCaptureService aux, CameraReadyGate cameraReady,
        MeridianFlipService meridian, DitherBarrier barrier,
        ILogger<LiveCaptureService> logger) {
        _equip = equip;
        _liveStack = liveStack;
        _relay = relay;
        _captureProgress = captureProgress;
        _guiders = guiders;
        _autoFocus = autoFocus;
        _aux = aux;
        _cameraReady = cameraReady;
        _meridian = meridian;
        _barrier = barrier;
        _logger = logger;
    }

    /// <summary>Start the server-side LIVE loop. No-op (returns false) if a
    /// loop is already running or no camera is connected.</summary>
    public bool Start(double exposureSeconds, int gain, int binning) {
        lock (_lock) {
            if (IsRunning) return false;
            if (_equip.Camera == null || !_equip.Camera.IsConnected) {
                LastError = "No camera connected";
                return false;
            }
            ExposureSeconds = exposureSeconds > 0 ? exposureSeconds : 1.0;
            Gain = gain;
            BinX = binning > 0 ? binning : 1;
            FrameCount = 0;
            LastError = null;
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loopTask = Task.Run(() => RunLoop(ct));
            // Kick off the auxiliary camera loop alongside the main session.
            try { _aux.NotifySessionActive(true); } catch { }
            try { _barrier.Register("main", blocking: true, isPrimary: true); } catch { }
            _logger.LogInformation(
                "Server LIVE loop started: exp={Exp}s gain={Gain} bin={Bin}",
                ExposureSeconds, Gain, BinX);
            return true;
        }
    }

    /// <summary>Stop the loop. Safe to call when idle.</summary>
    public void Stop() {
        CancellationTokenSource? cts;
        lock (_lock) {
            if (!IsRunning) return;
            IsRunning = false;
            cts = _cts;
            _cts = null;
        }
        try { cts?.Cancel(); } catch { }
        _logger.LogInformation("Server LIVE loop stopped");
    }

    /// <summary>Block until the main camera is present AND connected, returning it;
    /// null means the loop was cancelled while waiting.
    ///
    /// Exists because the LIVE loop must never ask a disconnected camera for a
    /// frame. It used to check IsConnected only at Start(), so a mid-session drop
    /// (or an INDI driver restart by the watchdog) left it firing CaptureAsync once
    /// a second against a device whose CCD_EXPOSURE property no longer existed —
    /// each write failing, each failure retried, all night. Waiting is the correct
    /// response: the watchdog restarts the driver, reconnects the device and
    /// restores the cooler; this loop's only job is to stay out of the way and pick
    /// back up when the camera returns.
    ///
    /// Also the single place the camera reference is resolved, so a reconnect that
    /// swaps the ICamera instance can't leave the loop holding a dead one.</summary>
    private async Task<ICamera?> WaitForCameraReadyAsync(CancellationToken ct) {
        // Fast path AND the wait now go through the shared gate, so LIVE, AUTORUN
        // and ADV all pause for a driver recovery identically. LIVE keeps its two
        // extras on top: surface the wait in the WS status (LastError) and re-assert
        // binning on return, since a restarted driver dropped it.
        if (CameraReadyGate.IsReady(_equip.Camera)) return _equip.Camera;

        var cam = await _cameraReady.WaitAsync("Server LIVE", ct,
            onWaiting: _ => LastError = "Waiting for the camera to reconnect…");
        if (cam == null) return null;

        LastError = null;
        if (BinX > 0) {
            try { await cam.SetBinningAsync(BinX, BinX, ct); } catch { /* best effort */ }
        }
        return cam;
    }

    private async Task RunLoop(CancellationToken ct) {
        try {
            var startCam = _equip.Camera;
            if (startCam == null) return;
            if (BinX > 0) {
                try { await startCam.SetBinningAsync(BinX, BinX); } catch { /* best effort */ }
            }

            while (!ct.IsCancellationRequested) {
                // Pause between frames while a dither/settle or an auto-focus
                // run owns the camera, so subs aren't trailed and the trigger
                // work completes before the next exposure.
                while (!ct.IsCancellationRequested && ShouldPause()) {
                    try { await Task.Delay(250, ct); } catch { return; }
                }
                if (ct.IsCancellationRequested) break;

                // Don't ask a disconnected camera for a frame. IsConnected used to
                // be checked only in Start(), so if the camera dropped mid-session
                // (or the INDI watchdog restarted its driver) this loop kept firing
                // CaptureAsync into the void: the write hit a property that no
                // longer existed, failed, and the 1s back-off retried it forever.
                // Field log 2026-07-15 is exactly that, once per second:
                //   Server LIVE frame capture failed; backing off 1s
                //   CCD_EXPOSURE [120] -- WARNING: property NOT in device snapshot
                // Wait for the camera to come BACK instead. The watchdog reconnects
                // it (and restores the cooler); we just have to not stampede while
                // it does. Poll at 1s — a reconnect takes seconds, and the whole
                // point is to stop hammering.
                // Re-resolved every frame, never captured once outside the loop: a
                // reconnect can hand back a DIFFERENT ICamera instance, and a stale
                // reference would keep talking to the dead one.
                var cam = await WaitForCameraReadyAsync(ct);
                if (cam == null) break;

                // Park here if a synchronized dither round is in flight.
                await _barrier.BeforeSubAsync("main", ct);
                if (ct.IsCancellationRequested) break;

                IImageData image;
                try {
                    var opts = new CaptureOptions(
                        Gain: Gain > 0 ? Gain : (int?)null,
                        BinX: BinX, BinY: BinX,
                        ImageType: "LIGHT");
                    using (_captureProgress.Begin("live", ExposureSeconds))
                        image = await CameraCaptureGate.RunAsync(
                            () => cam.CaptureAsync(ExposureSeconds, opts, ct), ct);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Server LIVE frame capture failed; backing off 1s");
                    LastError = ex.Message;
                    try { await Task.Delay(1000, ct); } catch { break; }
                    continue;
                }

                try {
                    // When an extra imager is the selected LIVE-stack source, the
                    // main camera keeps capturing (parallel model) but must NOT
                    // feed the stack or own the LIVE canvas — that imager's loop
                    // does. Main then only archives its own subs when enabled.
                    if (_liveStack.SourceIndex != 0) {
                        _liveStack.SaveFrameIfEnabled(image);
                    } else if (_liveStack.IsRunning) {
                        if (ExposureSeconds > 0) _liveStack.AverageExposureSec = ExposureSeconds;
                        await _liveStack.AddFrameAsync(image, ct);
                    } else {
                        // Not accumulating (live view only): relay for the
                        // preview AND still archive the raw frame when the user
                        // asked to keep frames — AddFrameAsync's save path is
                        // skipped on this branch, so do it explicitly here.
                        _liveStack.SaveFrameIfEnabled(image);
                        await _relay.RelayImageAsync(image, FrameKind.Live, ct);
                    }
                    FrameCount++;
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Server LIVE frame integrate/relay failed");
                    LastError = ex.Message;
                }

                // Report the finished sub to the barrier; when this is the
                // slowest of >=2 imaging cameras it runs the synchronized dither.
                try { await _barrier.AfterSubAsync("main", ExposureSeconds, ct); }
                catch (OperationCanceledException) { break; }

                // Honour the live-stack duration cap so the server loop ends
                // when the session's max duration is reached.
                if (_liveStack.IsRunning && _liveStack.DurationCapReached) {
                    _logger.LogInformation("Server LIVE loop ending: stack duration cap reached");
                    break;
                }
            }
        } finally {
            lock (_lock) { IsRunning = false; }
            try { _aux.NotifySessionActive(false); } catch { }
            try { _barrier.Deregister("main"); } catch { }
        }
    }

    /// <summary>True while the camera should NOT start a new LIVE sub: a
    /// dither/settle is in progress or an auto-focus run is active.</summary>
    private bool ShouldPause() {
        try {
            var g = _guiders.Active;
            if (g != null && (g.IsDithering || g.IsSettling)) return true;
        } catch { /* guider not ready */ }
        try {
            if (!_autoFocus.State.ToString().Equals("Idle", StringComparison.OrdinalIgnoreCase))
                return true;
        } catch { /* autofocus not ready */ }
        // A meridian flip owns the mount: hold the loop so it doesn't start a
        // new exposure while the mount is (or is about to be) slewing. The flip
        // itself waits for the CURRENT exposure to finish before it moves (see
        // MeridianFlipService.WaitForExposureIdleAsync), so the two never fight
        // over the camera. Covers auto-flip AND a manual "flip now" mid-session.
        try {
            if (_meridian.State != MeridianFlipState.Idle) return true;
        } catch { /* flip service not ready */ }
        return false;
    }
}
