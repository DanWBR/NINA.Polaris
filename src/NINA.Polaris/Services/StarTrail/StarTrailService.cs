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

using System.Collections.Concurrent;
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.Studio;
using NINA.Polaris.Services.Timelapse;

namespace NINA.Polaris.Services.StarTrail;

/// <summary>
/// Star-trails capture + composite. The camera is fixed (tracking OFF), so the
/// sky drifts across the frame and each star paints an arc; the composite is a
/// per-pixel MAX (lighten) blend of every sub, which is the one blend mode the
/// mean-only stackers (LiveStackingService / PlanetaryStackerService) don't do.
///
/// One self-contained async job owns the whole thing: turn tracking off, run a
/// capture loop (mirroring <see cref="LiveCaptureService"/>'s loop), MAX-blend
/// each sub into a running buffer, relay the growing composite live
/// (<see cref="FrameKind.StarTrail"/>), and on stop write a FITS master + a
/// stretched JPEG. Optionally the same subs feed the time-lapse builder for a
/// rotating-sky movie. Job model mirrors <see cref="Planetary.PlanetaryStackerService"/>.
///
/// Max-blend is brutal on fixed hot pixels (a fixed camera means a hot pixel
/// never trails, so it becomes a permanent bright dot), so each sub is run
/// through the same <see cref="CosmeticCorrection"/> the live stack uses before
/// it is blended.
/// </summary>
public class StarTrailService {
    private readonly EquipmentManager _equip;
    private readonly ImageRelayService _relay;
    private readonly CaptureProgressService _captureProgress;
    private readonly CameraReadyGate _cameraReady;
    private readonly ImageWriterService _imageWriter;
    private readonly MediaEncodeService _mediaEncode;
    private readonly ILogger<StarTrailService> _logger;
    private readonly ConcurrentDictionary<string, StarTrailJob> _jobs = new();

    public StarTrailJob? CurrentJob { get; private set; }
    public event Action<StarTrailJob>? JobUpdated;

    public StarTrailService(EquipmentManager equip, ImageRelayService relay,
        CaptureProgressService captureProgress, CameraReadyGate cameraReady,
        ImageWriterService imageWriter, MediaEncodeService mediaEncode,
        ILogger<StarTrailService> logger) {
        _equip = equip;
        _relay = relay;
        _captureProgress = captureProgress;
        _cameraReady = cameraReady;
        _imageWriter = imageWriter;
        _mediaEncode = mediaEncode;
        _logger = logger;
    }

    public StarTrailJob StartJob(StarTrailConfig cfg) {
        var job = new StarTrailJob {
            Id = Guid.NewGuid().ToString("N"),
            Config = cfg,
            Phase = StarTrailPhase.Preparing,
            ExposureSeconds = cfg.ExposureSeconds,
            StartedAt = DateTime.UtcNow
        };
        _jobs[job.Id] = job;
        JobRetention.TrimFinished(_jobs, j => j.StartedAt, j => j.CompletedAt != null);
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunAsync(job, job.Cts.Token));
        return job;
    }

    public StarTrailJob? GetJob(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>Graceful stop: break the loop and finalize the master.</summary>
    public void Stop(string id) { if (_jobs.TryGetValue(id, out var j)) j.StopRequested = true; }

    /// <summary>Cancel. The partial composite is still finalized (a star trail
    /// is a valid image at any length).</summary>
    public void Abort(string id) { if (_jobs.TryGetValue(id, out var j)) j.Cts?.Cancel(); }

    private async Task RunAsync(StarTrailJob job, CancellationToken ct) {
        var cfg = job.Config;
        ushort[]? max = null;
        ushort[]? cosmeticScratch = null;
        int w0 = 0, h0 = 0;
        BayerPatternEnum bayer0 = BayerPatternEnum.None;
        ImageMetaData? lastMeta = null;
        string? subDir = null;
        bool trackingTurnedOff = false;
        bool priorTracking = false;
        ITelescope? scope = null;

        try {
            SetPhase(job, StarTrailPhase.Preparing);
            if (_equip.Camera == null || !_equip.Camera.IsConnected) {
                Fail(job, "No camera connected."); return;
            }

            // Tracking OFF so the sky drifts and stars trail. Best-effort: a
            // static tripod / no-mount setup just skips this. Remembered so we
            // restore the prior state when the job ends.
            scope = _equip.Telescope;
            if (cfg.TurnTrackingOff && scope != null && scope.IsConnected
                    && scope.Capabilities.SupportsTrackingToggle) {
                priorTracking = scope.IsTracking;
                try {
                    await scope.SetTrackingAsync(false, ct);
                    trackingTurnedOff = true;
                    job.TrackingOff = true;
                    Notify(job);
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Star trail: could not disable mount tracking");
                }
            }

            SetPhase(job, StarTrailPhase.Capturing);
            try {
                while (!ct.IsCancellationRequested && !job.StopRequested) {
                    if (cfg.MaxFrames is int cap && job.FramesCaptured >= cap) break;

                    var cam = await WaitForCameraReadyAsync(ct);
                    if (cam == null) break;

                    IImageData image;
                    var opts = new CaptureOptions(
                        Gain: cfg.Gain > 0 ? cfg.Gain : (int?)null,
                        BinX: cfg.Binning, BinY: cfg.Binning,
                        ImageType: "LIGHT");
                    using (_captureProgress.Begin("startrail", cfg.ExposureSeconds))
                        image = await CameraCaptureGate.RunAsync(
                            () => cam.CaptureAsync(cfg.ExposureSeconds, opts, ct), ct);

                    var props = image.Properties;
                    lastMeta = image.MetaData;
                    var raw = image.Data;

                    // Cosmetic (hot/cold pixel) correction on a COPY — never
                    // mutate the raw frame the archive keeps. Skipped when off.
                    var work = raw;
                    var resolvedBayer = props.BayerPattern;
                    bool cfa = resolvedBayer != BayerPatternEnum.None
                               && resolvedBayer != BayerPatternEnum.Auto;
                    if (cfg.CosmeticCorrection) {
                        if (cosmeticScratch == null || cosmeticScratch.Length != raw.Length)
                            cosmeticScratch = new ushort[raw.Length];
                        Array.Copy(raw, cosmeticScratch, raw.Length);
                        work = cosmeticScratch;
                        try {
                            CosmeticCorrection.Apply(work, props.Width, props.Height, 1,
                                sigmaCold: 5.0, sigmaHot: 3.0, amount: 1.0, cfa: cfa);
                        } catch (Exception ex) {
                            _logger.LogWarning(ex, "Star trail: cosmetic correction failed; using raw sub");
                        }
                    }

                    // MAX-blend into the running composite.
                    if (max == null) {
                        max = new ushort[work.Length];
                        Array.Copy(work, max, work.Length);
                        w0 = props.Width; h0 = props.Height; bayer0 = resolvedBayer;
                    } else if (work.Length == max.Length) {
                        MaxInto(max, work);
                    } else {
                        _logger.LogWarning(
                            "Star trail: frame size changed ({New} vs {Have}); skipping this sub",
                            work.Length, max.Length);
                        continue;
                    }

                    job.FramesCaptured++;

                    // Archive the RAW sub when the user wants the frames kept or
                    // a time-lapse is requested; remember the folder for the movie.
                    if (cfg.SaveSubs || cfg.AlsoTimelapse) {
                        try {
                            var saved = _imageWriter.SaveImage(image, cfg.OutputName, "LIGHT", cfg.Gain);
                            if (saved != null && subDir == null) subDir = Path.GetDirectoryName(saved);
                        } catch (Exception ex) {
                            _logger.LogWarning(ex, "Star trail: could not archive sub");
                        }
                    }

                    // Relay the GROWING composite so the browser shows the trails
                    // build up in real time.
                    try {
                        var preview = BuildComposite(max, w0, h0, bayer0, lastMeta);
                        await _relay.RelayImageAsync(preview, FrameKind.StarTrail, ct);
                    } catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Star trail: preview relay failed"); }

                    Notify(job);

                    if (cfg.IntervalSeconds > 0) {
                        try { await Task.Delay(TimeSpan.FromSeconds(cfg.IntervalSeconds), ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            } catch (OperationCanceledException) {
                // Aborted mid-session — fall through and finalize what we have.
            }

            // Finalize: write the master (+ a stretched JPEG) from whatever the
            // composite holds. Any captured frame makes a valid trail.
            SetPhase(job, StarTrailPhase.Finalizing);
            if (max == null || job.FramesCaptured == 0) {
                Fail(job, "No frames were captured."); return;
            }

            var master = BuildComposite(max, w0, h0, bayer0, lastMeta ?? new ImageMetaData());
            var fitsPath = _imageWriter.SaveImage(master, cfg.OutputName, "MASTER",
                cfg.Gain, stacked: true, stackedFolderName: "startrails");
            if (fitsPath == null) {
                Fail(job, "Set an image output folder first (Settings), then run again."); return;
            }
            job.OutputPathFits = fitsPath;

            // A stretched, full-resolution JPEG to share. master.Data is the raw
            // MAX buffer for mono, or the debayered planar RGB for OSC.
            try {
                int maxDim = Math.Max(w0, h0);
                byte[] jpg = bayer0 == BayerPatternEnum.None
                    ? FitsThumbnailer.RenderJpegFromBuffer(master.Data, w0, h0, 16, maxDim, 90)
                    : FitsThumbnailer.RenderJpegFromRgbPlanes(master.Data, w0, h0, 16, maxDim, 90);
                var jpgPath = Path.ChangeExtension(fitsPath, ".jpg");
                await File.WriteAllBytesAsync(jpgPath, jpg, CancellationToken.None);
                job.OutputPathJpg = jpgPath;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Star trail: could not write the shareable JPEG");
            }

            // Optional rotating-sky movie from the same subs.
            if (cfg.AlsoTimelapse && subDir != null) {
                try {
                    var files = Directory.GetFiles(subDir)
                        .Where(f => { var e = Path.GetExtension(f).ToLowerInvariant();
                                      return e is ".fits" or ".fit" or ".fts"; })
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (files.Count > 1) {
                        _mediaEncode.StartJob(new FolderFrameSource(files, 1),
                            new EncodeConfig(subDir, cfg.OutputName + "_trail",
                                Fps: 20, MaxDim: 1280, Format: EncodeFormat.Gif, Loop: true));
                    }
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Star trail: could not start the time-lapse");
                }
            }

            SetPhase(job, StarTrailPhase.Ok);
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("Star trail OK: {N} frames -> {Path}", job.FramesCaptured, fitsPath);
            Notify(job);
        } catch (Exception ex) {
            _logger.LogError(ex, "Star trail failed");
            Fail(job, ex.Message);
        } finally {
            // Restore tracking to how we found it (use None so a cancelled ct
            // doesn't skip the restore).
            if (trackingTurnedOff && priorTracking && scope != null) {
                try { await scope.SetTrackingAsync(true, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Star trail: could not restore tracking"); }
            }
        }
    }

    /// <summary>Per-pixel MAX (lighten) blend of <paramref name="src"/> into the
    /// running composite <paramref name="dst"/>. This is the whole of star-trail
    /// stacking: keep the brightest value each pixel has ever held.</summary>
    public static void MaxInto(ushort[] dst, ushort[] src) {
        int n = Math.Min(dst.Length, src.Length);
        for (int i = 0; i < n; i++)
            if (src[i] > dst[i]) dst[i] = src[i];
    }

    /// <summary>Materialize the composite buffer as a viewable image: mono
    /// straight through, OSC debayered to planar RGB (one debayer, same as the
    /// planetary stacker's final combine).</summary>
    public static BaseImageData BuildComposite(ushort[] max, int w, int h,
            BayerPatternEnum bayer, ImageMetaData? meta) {
        meta ??= new ImageMetaData();
        if (bayer == BayerPatternEnum.None || bayer == BayerPatternEnum.Auto) {
            return new BaseImageData(max, new ImageProperties {
                Width = w, Height = h, BitDepth = 16, Channels = 1,
                IsBayered = false, BayerPattern = BayerPatternEnum.None
            }, meta);
        }
        var ch = BayerDebayer.Bilinear(max, w, h, bayer);
        int n = w * h;
        var planar = new ushort[n * 3];
        Array.Copy(ch.R, 0, planar, 0, n);
        Array.Copy(ch.G, 0, planar, n, n);
        Array.Copy(ch.B, 0, planar, n * 2, n);
        return new BaseImageData(planar, new ImageProperties {
            Width = w, Height = h, BitDepth = 16, Channels = 3,
            IsBayered = false, BayerPattern = BayerPatternEnum.None
        }, meta);
    }

    // Wait for the main camera to be present + connected, re-resolving it each
    // frame (a driver recovery can hand back a different instance). Mirrors
    // LiveCaptureService.
    private async Task<ICamera?> WaitForCameraReadyAsync(CancellationToken ct) {
        if (CameraReadyGate.IsReady(_equip.Camera)) return _equip.Camera;
        return await _cameraReady.WaitAsync("Star trail", ct);
    }

    private void SetPhase(StarTrailJob job, StarTrailPhase p) { job.Phase = p; Notify(job); }

    private void Fail(StarTrailJob job, string error) {
        job.Error = error;
        job.Phase = StarTrailPhase.Fail;
        job.CompletedAt = DateTime.UtcNow;
        _logger.LogWarning("Star trail failed: {Error}", error);
        Notify(job);
    }

    private void Notify(StarTrailJob job) { try { JobUpdated?.Invoke(job); } catch { } }
}

public record StarTrailConfig(
    double ExposureSeconds,
    int Gain,
    int Binning = 1,
    int IntervalSeconds = 0,
    int? MaxFrames = null,
    bool TurnTrackingOff = true,
    bool CosmeticCorrection = true,
    bool SaveSubs = false,
    bool AlsoTimelapse = false,
    string OutputName = "startrail");

public class StarTrailJob {
    public string Id { get; set; } = "";
    public StarTrailConfig Config { get; set; } = new(1.0, 0);
    public StarTrailPhase Phase { get; set; }
    public int FramesCaptured { get; set; }
    public double ExposureSeconds { get; set; }
    public bool TrackingOff { get; set; }
    public string? OutputPathFits { get; set; }
    public string? OutputPathJpg { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    internal bool StopRequested { get; set; }
    internal Task? Task { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
}

public enum StarTrailPhase { Preparing, Capturing, Finalizing, Ok, Fail }
