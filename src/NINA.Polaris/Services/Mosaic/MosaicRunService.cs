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

using System.Text.Json.Serialization;
using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Services.Mosaic;

/// <summary>
/// MOSAIC mode: shoot a field larger than the camera's in panels and build the
/// picture on screen as the panels come in.
///
/// <para>The run centres on the target once with Slew and Center. That solve
/// gives the camera angle the grid is laid out along, and its sync tightens
/// the mount's pointing for every panel after it. Each panel then gets a plain
/// slew, one exposure and one solve: the solve places the panel on the canvas,
/// so the mount only has to land close enough for the panels to overlap, and
/// no panel pays for centring iterations. A panel whose solve fails is placed
/// from the mount's reported position instead, and marked as such.</para>
/// </summary>
public sealed class MosaicRunService {
    private readonly EquipmentManager _equip;
    private readonly PlateSolveService _solver;
    private readonly ProfileService _profiles;
    private readonly SlewCenterService _slewCenter;
    private readonly ImageWriterService _writer;
    private readonly ActiveGuiderProvider? _guiders;
    private readonly PlateSolveProgressService? _progress;
    private readonly ILogger<MosaicRunService> _logger;
    // Resolved lazily: HostActivityService lists this service as an activity,
    // so a constructor edge back to it would close a DI cycle.
    private readonly IServiceProvider? _services;

    private readonly object _sync = new();
    private Run? _run;

    /// <summary>Settle time after each slew, before the shutter opens.</summary>
    internal TimeSpan SettleAfterSlew { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Exposures this long are guided panel by panel unless the
    /// operator says otherwise; shorter ones ride on the mount's tracking,
    /// where restarting guiding would cost more than the exposure.</summary>
    internal const double GuideEachPanelMinExposure = 30;

    // Settle for the guiding restarted on each panel: the live stack's
    // dither settle, since both wait on a fresh lock before a frame.
    internal const double PanelSettlePixels = 1.5;
    internal const int PanelSettleSeconds = 3;
    internal const int PanelSettleTimeout = 60;

    /// <summary>How long the guider may sit idle after a start before the
    /// panel stops waiting for a settle that will not come.</summary>
    internal TimeSpan GuideIdleGrace { get; set; } = TimeSpan.FromSeconds(10);

    public MosaicRunService(EquipmentManager equip, PlateSolveService solver,
            ProfileService profiles, SlewCenterService slewCenter, ImageWriterService writer,
            ILogger<MosaicRunService> logger,
            ActiveGuiderProvider? guiders = null,
            PlateSolveProgressService? progress = null,
            IServiceProvider? services = null) {
        _equip = equip;
        _solver = solver;
        _profiles = profiles;
        _slewCenter = slewCenter;
        _writer = writer;
        _logger = logger;
        _guiders = guiders;
        _progress = progress;
        _services = services;
    }

    public bool IsRunning {
        get { lock (_sync) return _run is { Finished: false }; }
    }

    /// <summary>Panel size of the active camera and rig at bin 1, from the
    /// focal length and the sensor. Null when either is unknown.</summary>
    public (double widthDeg, double heightDeg, double scaleArcsec)? EstimatePanelFov() {
        var cam = _equip.Camera;
        double fl = _profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0;
        if (cam == null || fl <= 0) return null;
        double px = cam.PixelSizeX > 0 ? cam.PixelSizeX : cam.PixelSizeY;
        double py = cam.PixelSizeY > 0 ? cam.PixelSizeY : cam.PixelSizeX;
        if (px <= 0 || cam.MaxX <= 0 || cam.MaxY <= 0) return null;
        double w = 2 * Math.Atan(px * cam.MaxX / 1000.0 / (2 * fl)) * 180 / Math.PI;
        double h = 2 * Math.Atan(py * cam.MaxY / 1000.0 / (2 * fl)) * 180 / Math.PI;
        return (w, h, 206.2648 * px / fl);
    }

    /// <summary>Start a run. Returns null on success, or the reason it
    /// cannot start.</summary>
    public string? Start(MosaicRunRequest req) {
        if (_equip.Camera == null) return "Connect a camera first.";
        if (_equip.Telescope == null) return "Connect a mount first.";
        if (!double.IsFinite(req.RaHours) || req.RaHours < 0 || req.RaHours >= 24
                || !double.IsFinite(req.DecDeg) || Math.Abs(req.DecDeg) > 90)
            return "Pick a target first.";
        if (req.ExposureSeconds <= 0) return "Exposure must be more than 0 s.";

        var busy = (_services?.GetService(typeof(HostActivityService)) as HostActivityService)?.Current();
        if (busy is { Count: > 0 })
            return $"Stop {string.Join(" and ", busy)} first.";
        var guard = _services?.GetService(typeof(MountSafetyGuardService)) as MountSafetyGuardService;
        if (!req.Force && guard is { RequireHomeAfterTrip: true })
            return "Mount safety stop is active, Find Home before slewing.";

        lock (_sync) {
            if (_run is { Finished: false }) return "A mosaic is already running.";
            var run = new Run(req) {
                TrackingWasOn = MountAbort.WasTracking(_equip.Telescope)
            };
            _run = run;
            run.Task = Task.Run(() => RunAsync(run, run.Cts.Token));
        }
        return null;
    }

    public async Task StopAsync() {
        Run? run;
        lock (_sync) run = _run;
        if (run == null || run.Finished) return;
        run.Cts.Cancel();
        var job = run.CenterJobId;
        if (job != null) await _slewCenter.CancelJobAsync(job);
        if (run.State == MosaicRunState.Slewing)
            await MountAbort.AbortAndRestoreTrackingAsync(_equip.Telescope, run.TrackingWasOn, _logger);
    }

    public MosaicRunStatus Status() {
        lock (_sync) return _run?.Snapshot() ?? new MosaicRunStatus { State = MosaicRunState.Idle };
    }

    /// <summary>The latest render of the canvas, or null before the first.</summary>
    public byte[]? CanvasJpeg() {
        lock (_sync) return _run?.Jpeg;
    }

    private async Task RunAsync(Run run, CancellationToken ct) {
        var req = run.Request;
        try {
            await StopGuidingAsync(run, ct);
            run.GuidePanels = GuidesEachPanel(req.GuideEachPanel,
                _guiders?.Active?.IsConnected == true, run.GuidingWasOn, req.ExposureSeconds);
            if (run.GuidePanels)
                run.Warn("Guiding restarts on a new guide star at each panel.");
            else if (run.GuidingWasOn)
                run.Warn("Guiding is paused for the mosaic and resumes when it completes.");

            // 1. Centre on the target. The solve gives the camera angle.
            run.Set(MosaicRunState.Centering, "Centring on the target");
            var (reference, refW, refH) = await CentreAsync(run, ct);

            // 2. Lay out the grid and the canvas.
            var geom = MosaicLayout.Plan(req.RaHours, req.DecDeg, reference, refW, refH,
                req.ZoomX, req.ZoomY, req.OverlapPercent);
            var canvas = new MosaicCanvas(geom.CanvasWidth, geom.CanvasHeight, geom.CanvasWcs);
            var (pw, ph) = MosaicLayout.FrameSizeDeg(reference, refW, refH);
            run.Begin(geom, canvas, pw, ph);
            run.Publish(canvas.RenderJpeg());
            _logger.LogInformation(
                "MOSAIC {Name}: {Cols}x{Rows} panels of {W:F2}x{H:F2} deg, overlap {Ox:P0}/{Oy:P0}",
                req.TargetName, geom.Cols, geom.Rows, pw, ph, geom.OverlapX, geom.OverlapY);

            // 3. Each panel: slew, expose, solve, composite.
            string? folder = null;
            foreach (var panel in geom.Panels) {
                ct.ThrowIfCancellationRequested();
                run.SetPanel(panel.Index, MosaicPanelState.Active);
                try {
                    var saved = await ShootPanelAsync(run, panel, reference, refW, canvas, ct);
                    folder ??= saved != null ? Path.GetDirectoryName(saved) : null;
                } catch (OperationCanceledException) {
                    throw;
                } catch (MountFailedException ex) {
                    // The mount refusing a slew is not this panel's problem and
                    // will not fix itself on the next one.
                    run.FailPanel(panel.Index, ex.Message);
                    throw;
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "MOSAIC panel {I} failed", panel.Index + 1);
                    run.FailPanel(panel.Index, ex.Message);
                }
            }

            int placed = run.PlacedCount();
            if (placed == 0) {
                await StopPanelGuidingAsync(run);
                run.Finish(MosaicRunState.Failed, "No panel could be placed.");
                return;
            }
            if (folder != null) {
                try {
                    var path = Path.Combine(folder,
                        $"{SafeName(req.TargetName)}_mosaic_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                    File.WriteAllBytes(path, canvas.RenderJpeg(92));
                    run.OutputPath = path;
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "MOSAIC: could not save the composite");
                }
            }
            int total = geom.Panels.Count;
            var summary = placed == total
                ? $"Mosaic complete: {total} panels."
                : $"Mosaic finished with {placed} of {total} panels.";
            if (run.GuidePanels && _guiders?.Active is { IsConnected: true, IsGuiding: true })
                summary += " Guiding continues on the last panel.";
            else if (run.GuidingWasOn) summary += await ResumeGuidingAsync(ct);
            run.Finish(MosaicRunState.Done, summary);
        } catch (OperationCanceledException) {
            await StopPanelGuidingAsync(run);
            run.Finish(MosaicRunState.Cancelled, "Stopped.");
        } catch (MountFailedException ex) {
            _logger.LogWarning("MOSAIC stopped: {Error}", ex.Message);
            await StopPanelGuidingAsync(run);
            run.Finish(MosaicRunState.Failed, ex.Message);
        } catch (Exception ex) {
            _logger.LogError(ex, "MOSAIC run failed");
            await StopPanelGuidingAsync(run);
            run.Finish(MosaicRunState.Failed, ex.Message);
        }
    }

    /// <summary>Whether each panel gets its own guiding. Needs a connected
    /// guider; then the operator's choice, else on when guiding was running
    /// at the start and the exposures are long enough to need it.</summary>
    internal static bool GuidesEachPanel(bool? requested, bool guiderConnected, bool guidingWasOn,
            double exposureSeconds) {
        if (!guiderConnected) return false;
        return requested ?? (guidingWasOn && exposureSeconds >= GuideEachPanelMinExposure);
    }

    /// <summary>Stop guiding before a slew: the guide star leaves the guide
    /// camera's field, and corrections sent during the move would fight it.
    /// Returns true when guiding was running.</summary>
    private async Task<bool> StopGuidingForSlewAsync(CancellationToken ct) {
        var guider = _guiders?.Active;
        try {
            if (guider is { IsConnected: true, IsGuiding: true }) {
                await guider.StopAsync(ct);
                return true;
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "MOSAIC: could not stop guiding (continuing)");
        }
        return false;
    }

    private async Task StopGuidingAsync(Run run, CancellationToken ct) {
        if (await StopGuidingForSlewAsync(ct)) run.GuidingWasOn = true;
    }

    /// <summary>After a failure or a stop, guiding started for a panel is
    /// stopped too, the same rule as the paused guiding staying off.</summary>
    private async Task StopPanelGuidingAsync(Run run) {
        if (!run.GuidePanels) return;
        try { await StopGuidingForSlewAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "MOSAIC: could not stop guiding"); }
    }

    /// <summary>Guide this panel before the shutter opens: a new star in the
    /// guide camera's new field, then wait for the guiding to settle. A panel
    /// whose guiding does not start is shot on the mount's tracking and the
    /// run says so; the mosaic carries on either way.</summary>
    private async Task GuidePanelAsync(Run run, MosaicPanelPlan panel, CancellationToken ct) {
        int n = panel.Index + 1;
        var guider = _guiders?.Active;
        if (guider is not { IsConnected: true }) {
            run.Warn($"Panel {n}: the guider is disconnected; shot on tracking alone.");
            return;
        }
        run.Set(MosaicRunState.Guiding, $"Panel {n}: guiding");
        var warning = await StartAndSettleAsync(guider, n, PanelSettleTimeout, GuideIdleGrace, _logger, ct);
        if (warning != null) run.Warn(warning);
    }

    /// <summary>Pick a star, start guiding and wait for the settle report.
    /// Returns null when guiding settled, else the sentence for the run.</summary>
    internal static async Task<string?> StartAndSettleAsync(IGuider guider, int panelNumber,
            int settleTimeout, TimeSpan idleGrace, ILogger logger, CancellationToken ct) {
        int n = panelNumber;
        var settled = new TaskCompletionSource<SettleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSettled(SettleResult r) => settled.TrySetResult(r);
        guider.Settled += OnSettled;
        try {
            try {
                await guider.AutoSelectStarAsync(ct);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                // Starting guiding without a selection picks a star itself.
                logger.LogInformation("MOSAIC panel {N}: star selection failed ({Error})", n, ex.Message);
            }
            await guider.StartGuidingAsync(PanelSettlePixels, PanelSettleSeconds, settleTimeout, ct: ct);

            // Wait for the settle report. A backend that gave up without one
            // (no star, no mount) goes idle, so stop waiting after a grace.
            // Calibration does not count against the settle timeout.
            var limit = TimeSpan.FromSeconds(settleTimeout + 5);
            var deadline = DateTime.UtcNow + limit;
            DateTime? idleSince = null;
            while (!settled.Task.IsCompleted && DateTime.UtcNow < deadline) {
                if (guider.IsCalibrating) deadline = DateTime.UtcNow + limit;
                if (guider.IsGuiding || guider.IsCalibrating || guider.IsSettling) idleSince = null;
                else if (DateTime.UtcNow - (idleSince ??= DateTime.UtcNow) > idleGrace) break;
                await Task.Delay(100, ct);
            }

            var result = settled.Task.IsCompleted ? settled.Task.Result : null;
            if (!guider.IsGuiding)
                return $"Panel {n}: guiding did not start"
                    + (result?.Error is { Length: > 0 } e ? $" ({e})" : "") + "; shot on tracking alone.";
            if (result == null)
                return $"Panel {n}: guiding had not settled after {settleTimeout} s; shot anyway.";
            if (result.Status != 0)
                return $"Panel {n}: guiding did not settle ({result.Error}); shot anyway.";
            return null;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            logger.LogWarning(ex, "MOSAIC panel {N}: guiding failed", n);
            return $"Panel {n}: guiding could not start ({ex.Message}); shot on tracking alone.";
        } finally {
            guider.Settled -= OnSettled;
        }
    }

    /// <summary>Guiding back on after a completed run, the way Slew and Center
    /// does it: the old guide star is far out of the field, so pick a new one
    /// first. The mount is on the last panel. After a failure or a stop
    /// guiding stays off rather than guide a field nobody chose. Returns the
    /// sentence for the run summary.</summary>
    private async Task<string> ResumeGuidingAsync(CancellationToken ct) {
        var guider = _guiders?.Active;
        if (guider is not { IsConnected: true }) return " Guiding was not resumed: the guider is disconnected.";
        try {
            await guider.AutoSelectStarAsync(ct);
            await guider.StartGuidingAsync(ct: ct);
            return " Guiding resumed.";
        } catch (Exception ex) {
            _logger.LogWarning(ex, "MOSAIC: could not resume guiding");
            return " Guiding could not be resumed: " + ex.Message;
        }
    }

    /// <summary>Slew and Center on the target and return the reference WCS
    /// at bin 1. Without a solve the grid is laid out north-up from the rig's
    /// nominal scale, and the run says so.</summary>
    private async Task<(WcsInfo wcs, int width, int height)> CentreAsync(Run run, CancellationToken ct) {
        var req = run.Request;
        var job = _slewCenter.StartJob(req.RaHours, req.DecDeg, toleranceArcsec: 60, force: req.Force);
        run.CenterJobId = job.Id;
        while (job.State is not (SlewCenterState.Centered or SlewCenterState.Failed or SlewCenterState.Cancelled)) {
            await Task.Delay(500, ct);
        }
        run.CenterJobId = null;
        ct.ThrowIfCancellationRequested();
        if (job.State == SlewCenterState.Cancelled) throw new OperationCanceledException();

        var solve = job.LastSolve;
        if (solve != null && job.LastSolveWidth > 0 && job.LastSolveHeight > 0) {
            if (job.State != SlewCenterState.Centered)
                run.Warn("Centring did not converge; the grid uses the last solve.");
            return (ToWcs(solve, job.LastSolveWidth, job.LastSolveHeight),
                job.LastSolveWidth, job.LastSolveHeight);
        }

        var cam = _equip.Camera ?? throw new InvalidOperationException("Camera disconnected");
        var fov = EstimatePanelFov()
            ?? throw new InvalidOperationException(
                "Centring failed and the panel size is unknown: " + (job.Error ?? "no plate solve")
                + ". Set the focal length in the rig.");
        int w = cam.MaxX, h = cam.MaxY;
        run.Warn("No plate solve at the target, so the camera angle is unknown: the grid is laid out "
            + "north-up and panels are placed by the mount's position. " + (job.Error ?? ""));
        return (WcsHeaders.FromSolveResult(req.RaHours * 15, req.DecDeg, fov.scaleArcsec, 0, w, h), w, h);
    }

    /// <summary>Shoot one panel. Returns the saved frame's path, if saved.</summary>
    private async Task<string?> ShootPanelAsync(Run run, MosaicPanelPlan panel, WcsInfo reference,
            int refWidth, MosaicCanvas canvas, CancellationToken ct) {
        var req = run.Request;
        var scope = _equip.Telescope ?? throw new InvalidOperationException("Mount disconnected");

        if (run.GuidePanels) await StopGuidingForSlewAsync(ct);
        run.Set(MosaicRunState.Slewing, $"Panel {panel.Index + 1}: slewing");
        try {
            await scope.SlewAsync(panel.RaHours, panel.DecDeg, ct);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            throw new MountFailedException($"The mount refused the slew to panel {panel.Index + 1}: {ex.Message}");
        }
        for (int i = 0; i < 300 && scope.IsSlewing; i++) await Task.Delay(1000, ct);
        await Task.Delay(SettleAfterSlew, ct);
        if (run.GuidePanels) await GuidePanelAsync(run, panel, ct);

        var camera = _equip.Camera ?? throw new InvalidOperationException("Camera disconnected");
        run.Set(MosaicRunState.Exposing, $"Panel {panel.Index + 1}: exposing {req.ExposureSeconds:0.#} s");
        run.ExposureStartedAt = DateTime.UtcNow;
        var image = await CameraCaptureGate.RunAsync(() => camera.CaptureAsync(req.ExposureSeconds,
            new CaptureOptions(Gain: req.Gain, Offset: RigCaptureDefaults.Offset(_profiles),
                BinX: req.Binning, BinY: req.Binning, ImageType: "LIGHT"), ct), ct);
        run.ExposureStartedAt = null;
        int width = image.Properties.Width, height = image.Properties.Height;

        string? saved = null;
        if (req.SaveFrames) {
            saved = _writer.SaveImage(image, targetName: $"{req.TargetName} mosaic",
                imageType: "LIGHT", gain: req.Gain ?? 0);
        }

        run.Set(MosaicRunState.Solving, $"Panel {panel.Index + 1}: plate solving");
        var solve = await SolveAsync(image, panel, ct);

        WcsInfo wcs;
        bool solved = solve is { Success: true };
        if (solved) {
            wcs = ToWcs(solve!, width, height);
        } else {
            // Placed from where the mount says it points, in the reference
            // orientation scaled to this frame's binning.
            double ra = scope.RightAscension, dec = scope.Declination;
            if (!double.IsFinite(ra) || !double.IsFinite(dec)) { ra = panel.RaHours; dec = panel.DecDeg; }
            double s = (double)refWidth / width;
            wcs = new WcsInfo {
                RaDeg = ra * 15, DecDeg = dec,
                RefPixelX = (width + 1) / 2.0, RefPixelY = (height + 1) / 2.0,
                CD11 = reference.CD11 * s, CD12 = reference.CD12 * s,
                CD21 = reference.CD21 * s, CD22 = reference.CD22 * s,
            };
        }

        run.Set(MosaicRunState.Compositing, $"Panel {panel.Index + 1}: adding to the mosaic");
        var tile = MosaicTile.FromFrame(image.Data, width, height, ResolveBayer(image.Properties),
            wcs, canvas.ReductionFor(wcs));
        var quad = canvas.Add(tile);
        run.Publish(canvas.RenderJpeg());
        run.CompletePanel(panel.Index, quad, solved,
            solved ? null : "Plate solve failed, placed by the mount position: " + (solve?.Error ?? "no solver"),
            saved);
        return saved;
    }

    private async Task<PlateSolveResult?> SolveAsync(IImageData image, MosaicPanelPlan panel, CancellationToken ct) {
        if (!_solver.IsAvailable) return null;
        var rig = _profiles.ActiveEquipmentProfile;
        var cam = _equip.Camera;
        var geom = PlateSolveHints.From(rig?.FocalLengthMm ?? 0,
            cam?.PixelSizeY > 0 ? cam.PixelSizeY : (cam?.PixelSizeX ?? 0),
            image.Properties.Height, cam?.MaxY ?? 0);
        PlateSolveHints.StampFocalLength(image, rig?.FocalLengthMm ?? 0);
        var temp = Path.Combine(Path.GetTempPath(), $"polaris_mosaic_{Guid.NewGuid():N}.fits");
        _progress?.Begin("MOSAIC", ct);
        try {
            FITSWriter.Write(image, temp);
            var options = new PlateSolveOptions {
                CaptureContext = CaptureWcsContext.Create(_equip, _profiles, image),
                HintRa = panel.RaHours,
                HintDec = panel.DecDeg,
                SearchRadiusDeg = 5,
            };
            PlateSolveHints.Apply(options, geom);
            return await _solver.SolveAsync(temp, options, _progress?.Token ?? ct,
                _progress != null ? _progress.Append : null);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "MOSAIC: plate solve threw");
            return PlateSolveResult.Failed(ex.Message);
        } finally {
            _progress?.End();
            try { File.Delete(temp); } catch { /* temp file */ }
        }
    }

    internal static WcsInfo ToWcs(PlateSolveResult r, int width, int height) {
        double raDeg = r.RaDeg != 0 ? r.RaDeg : r.RaHours * 15;
        return r.HasCdMatrix
            ? WcsHeaders.FromCdMatrix(raDeg, r.DecDeg, r.CrPix1, r.CrPix2,
                r.CD11!.Value, r.CD12!.Value, r.CD21!.Value, r.CD22!.Value, width, height)
            : WcsHeaders.FromSolveResult(raDeg, r.DecDeg, r.ScaleArcsecPerPixel, r.RotationDeg, width, height);
    }

    /// <summary>The frame's own pattern, else the rig's override, else mono.</summary>
    private BayerPatternEnum ResolveBayer(ImageProperties props) {
        if (props.BayerPattern is not (BayerPatternEnum.None or BayerPatternEnum.Auto))
            return props.BayerPattern;
        var raw = _profiles.GetActiveCameraQuirks()?.BayerPatternOverride;
        if (string.IsNullOrWhiteSpace(raw)) raw = _profiles.ActiveEquipmentProfile?.BayerPatternOverride;
        return !string.IsNullOrWhiteSpace(raw)
               && Enum.TryParse<BayerPatternEnum>(raw, ignoreCase: true, out var p)
               && p is not (BayerPatternEnum.None or BayerPatternEnum.Auto)
            ? p : BayerPatternEnum.None;
    }

    private sealed class MountFailedException : Exception {
        public MountFailedException(string message) : base(message) { }
    }

    private static string SafeName(string s) {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string((s ?? "").Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return string.IsNullOrEmpty(clean) ? "mosaic" : clean;
    }

    /// <summary>One run's state. Written by the run task, read by the status
    /// endpoint through <see cref="Snapshot"/>, both under the lock.</summary>
    private sealed class Run {
        public MosaicRunRequest Request { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? Task { get; set; }
        public bool TrackingWasOn { get; set; }
        public bool GuidingWasOn { get; set; }
        /// <summary>Guiding is restarted on each panel instead of paused for the run.</summary>
        public bool GuidePanels { get; set; }
        public string? CenterJobId { get; set; }
        public DateTime? ExposureStartedAt {
            get { lock (_lock) return _exposureStartedAt; }
            set { lock (_lock) _exposureStartedAt = value; }
        }
        public string? OutputPath {
            get { lock (_lock) return _outputPath; }
            set { lock (_lock) _outputPath = value; }
        }
        public byte[]? Jpeg { get; private set; }

        private readonly object _lock = new();
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private DateTime? _finishedAt;
        private MosaicRunState _state = MosaicRunState.Centering;
        private string _message = "Starting";
        private readonly List<string> _warnings = new();
        private MosaicGeometry? _geom;
        private double _panelW, _panelH;
        private MosaicPanelStatus[] _panels = Array.Empty<MosaicPanelStatus>();
        private int _current = -1;
        private int _version;
        private DateTime? _exposureStartedAt;
        private string? _outputPath;

        public Run(MosaicRunRequest req) { Request = req; }

        public MosaicRunState State { get { lock (_lock) return _state; } }
        public bool Finished { get { lock (_lock) return _finishedAt != null; } }

        public void Set(MosaicRunState s, string message) {
            lock (_lock) { _state = s; _message = message; }
        }

        public void Warn(string w) { lock (_lock) _warnings.Add(w.Trim()); }

        public void Begin(MosaicGeometry g, MosaicCanvas c, double panelW, double panelH) {
            lock (_lock) {
                _geom = g; _panelW = panelW; _panelH = panelH;
                _panels = g.Panels.Select(p => new MosaicPanelStatus {
                    Index = p.Index, Row = p.Row, Col = p.Col,
                    RaHours = p.RaHours, DecDeg = p.DecDeg,
                    X0 = p.X0, Y0 = p.Y0, X1 = p.X1, Y1 = p.Y1,
                }).ToArray();
            }
        }

        public void Publish(byte[] jpeg) {
            lock (_lock) { Jpeg = jpeg; _version++; }
        }

        public void SetPanel(int i, MosaicPanelState s) {
            lock (_lock) { _current = i; _panels[i].State = s; }
        }

        public void CompletePanel(int i, (double x, double y)[]? quad, bool solved, string? note, string? saved) {
            lock (_lock) {
                var p = _panels[i];
                p.State = MosaicPanelState.Done;
                p.Solved = solved;
                p.Quad = quad?.Select(q => new[] { q.x, q.y }).ToArray();
                p.Note = note;
                p.SavedPath = saved;
            }
        }

        public void FailPanel(int i, string error) {
            lock (_lock) { _panels[i].State = MosaicPanelState.Failed; _panels[i].Note = error; }
        }

        public int PlacedCount() {
            lock (_lock) return _panels.Count(p => p.State == MosaicPanelState.Done);
        }

        public void Finish(MosaicRunState s, string message) {
            lock (_lock) {
                _state = s; _message = message; _finishedAt = DateTime.UtcNow; _current = -1;
                _exposureStartedAt = null;
            }
        }

        public MosaicRunStatus Snapshot() {
            lock (_lock) {
                var g = _geom;
                return new MosaicRunStatus {
                    State = _state,
                    Message = _message,
                    Warnings = _warnings.ToList(),
                    TargetName = Request.TargetName,
                    RaHours = Request.RaHours,
                    DecDeg = Request.DecDeg,
                    ExposureSeconds = Request.ExposureSeconds,
                    ExposureStartedAt = _exposureStartedAt,
                    Cols = g?.Cols ?? 0,
                    Rows = g?.Rows ?? 0,
                    OverlapX = g?.OverlapX ?? 0,
                    OverlapY = g?.OverlapY ?? 0,
                    PanelWidthDeg = _panelW,
                    PanelHeightDeg = _panelH,
                    CanvasWidth = g?.CanvasWidth ?? 0,
                    CanvasHeight = g?.CanvasHeight ?? 0,
                    CurrentPanel = _current,
                    Panels = _panels.Select(p => p.Clone()).ToList(),
                    CanvasVersion = _version,
                    StartedAt = _startedAt,
                    FinishedAt = _finishedAt,
                    OutputPath = _outputPath,
                };
            }
        }
    }
}

public sealed class MosaicRunRequest {
    public string TargetName { get; set; } = "Target";
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    /// <summary>Field width, in panel widths.</summary>
    public double ZoomX { get; set; } = 2;
    /// <summary>Field height, in panel heights.</summary>
    public double ZoomY { get; set; } = 2;
    public double OverlapPercent { get; set; } = 20;
    public double ExposureSeconds { get; set; } = 10;
    public int? Gain { get; set; }
    public int Binning { get; set; } = 1;
    public bool SaveFrames { get; set; } = true;
    /// <summary>Restart guiding on each panel. Null decides from the exposure
    /// and whether guiding was running at the start.</summary>
    public bool? GuideEachPanel { get; set; }
    /// <summary>Operator confirmed past the post-safety-stop home requirement.</summary>
    public bool Force { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MosaicRunState { Idle, Centering, Slewing, Guiding, Exposing, Solving, Compositing, Done, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MosaicPanelState { Pending, Active, Done, Failed }

public sealed class MosaicPanelStatus {
    public int Index { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    /// <summary>Planned footprint on the canvas, 0-based pixel edges.</summary>
    public double X0 { get; set; }
    public double Y0 { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public MosaicPanelState State { get; set; }
    /// <summary>True when placed by its own plate solve, false when placed
    /// by the mount position.</summary>
    public bool Solved { get; set; }
    /// <summary>Where the frame actually landed: four canvas points.</summary>
    public double[][]? Quad { get; set; }
    public string? Note { get; set; }
    public string? SavedPath { get; set; }

    public MosaicPanelStatus Clone() => (MosaicPanelStatus)MemberwiseClone();
}

public sealed class MosaicRunStatus {
    public MosaicRunState State { get; set; }
    public string Message { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
    public string? TargetName { get; set; }
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    public double ExposureSeconds { get; set; }
    public DateTime? ExposureStartedAt { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public double OverlapX { get; set; }
    public double OverlapY { get; set; }
    public double PanelWidthDeg { get; set; }
    public double PanelHeightDeg { get; set; }
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public int CurrentPanel { get; set; }
    public List<MosaicPanelStatus> Panels { get; set; } = new();
    public int CanvasVersion { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? OutputPath { get; set; }
}
