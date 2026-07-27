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
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

// Part of the NativeGuider class — guide-camera dark library + bad-pixel map
// (the in-process equivalent of PHD2's dark library / defect map). A single
// "build" capture produces both a master dark (for the current
// exposure/gain/bin) and a hot/dead-pixel map; the per-rig
// NativeGuideCalibrationMode ("off" | "dark" | "bpm" | "both") decides which
// is applied to each guide frame, so switching modes never recaptures.
public sealed partial class NativeGuider {
    // ----- Loaded calibration (applied in CaptureFullAsync) -----
    private ushort[]? _darkMaster;
    private int _darkW, _darkH;
    private string? _darkLabel;            // human-readable "{exp}ms g{gain} bin{bin}"
    private HashSet<int>? _bpmSet;
    private int _bpmW, _bpmH, _bpmCount;
    // Keys the currently-loaded artifacts were loaded for. When the live
    // exposure/gain/bin no longer matches, EnsureCalibrationLoaded re-reads
    // from disk once (caching a miss so we don't hit disk every frame).
    private string? _loadedDarkKey;
    private string? _loadedBpmKey;

    // ----- Build state (surfaced to the GUIDE card via DarkCalibration) -----
    private volatile bool _buildingDark;
    private volatile string? _buildProgress;
    private volatile string? _buildError;
    private CancellationTokenSource? _buildCts;

    private sealed record BadPixelMapFile(int Width, int Height, int[] Pixels);

    /// <summary>Status object for the GUIDE calibration card + WS payload.</summary>
    public object DarkCalibration => new {
        mode = (Rig.NativeGuideCalibrationMode ?? "off").Trim().ToLowerInvariant(),
        frames = Math.Clamp(Rig.NativeGuideDarkFrames, 1, 100),
        building = _buildingDark,
        progress = _buildProgress,
        error = _buildError,
        hasDark = _darkMaster != null,
        darkLabel = _darkLabel,
        hasBpm = _bpmSet != null && _bpmCount > 0,
        bpmPixels = _bpmCount,
    };

    // Guide darks + master dark live in the normal images tree, so they show
    // up in FILES next to every other FITS frame:
    //   {ImageOutputDir}/{rig}/calibration/guide-dark/{exp}ms_g{gain}_bin{bin}/
    //       master.fits  +  guidedark_NN.fits
    //   {ImageOutputDir}/{rig}/calibration/guide-dark/bpm_g{gain}_bin{bin}.json
    // The guide subtree is deliberately separate from the main camera's
    // calibration/dark/ so STUDIO never matches a guide dark to a science
    // light (different sensor / dimensions). When no output dir is configured
    // we fall back to the legacy DataDir location so guiding still calibrates
    // on a headless rig that never set a FILES root.
    private string GuideCalRoot {
        get {
            var outDir = (_profiles.Active.ImageOutputDir ?? "").Trim();
            return string.IsNullOrEmpty(outDir)
                ? LegacyCalDir
                : Path.Combine(outDir, SanitizeFolder(Rig.Name), "calibration", "guide-dark");
        }
    }

    // Pre-relocation builds wrote a flat layout under DataDir; kept for the
    // read/cleanup fallback so an existing library keeps working.
    private string LegacyCalDir
        => Path.Combine(_profiles.DataDir, "guide-calibration", SanitizeFolder(Rig.Id));

    private (int expMs, int gain, int bin) CalParams() {
        int expMs = Math.Max(50, Rig.NativeGuideExposureMs);
        // The EFFECTIVE gain, not the stored one: a dark set has to be keyed by
        // (and shot at) the gain the sensor really runs at. ToupTek's INDI
        // driver floors Gain at 100, so a rig left on the 40 default produced
        // darks filed under g40 that were subtracted from g100 lights.
        int gain = EffectiveGuideGain;
        int bin = Math.Clamp(Rig.NativeGuideBin <= 0 ? 1 : Rig.NativeGuideBin, 1, 4);
        return (expMs, gain, bin);
    }

    // Per-(exp,gain,bin) folder holding the master + the raw darks it was built
    // from. Not created here (read paths must not litter empty dirs); the build
    // creates it explicitly before writing.
    private string SetDir(int expMs, int gain, int bin)
        => Path.Combine(GuideCalRoot, $"{expMs}ms_g{gain}_bin{bin}");
    private string DarkPath(int expMs, int gain, int bin)
        => Path.Combine(SetDir(expMs, gain, bin), "master.fits");
    private string SubPath(int expMs, int gain, int bin, int idx)
        => Path.Combine(SetDir(expMs, gain, bin), $"guidedark_{idx:00}.fits");
    // BPM is keyed by gain+bin only: a hot pixel's *location* is stable across
    // exposure even though its intensity scales with it (PHD2 does the same).
    private string BpmPath(int gain, int bin)
        => Path.Combine(GuideCalRoot, $"bpm_g{gain}_bin{bin}.json");

    // Legacy artifact paths for the read/cleanup fallback.
    private string LegacyDarkPath(int expMs, int gain, int bin)
        => Path.Combine(LegacyCalDir, $"dark_e{expMs}_g{gain}_b{bin}.fits");
    private string LegacyBpmPath(int gain, int bin)
        => Path.Combine(LegacyCalDir, $"bpm_g{gain}_b{bin}.json");

    // Matches ImageWriterService.SanitizeFolder so the {rig} folder lines up
    // with the one lights/calibration are written under (spaces → underscore).
    private static string SanitizeFolder(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Default";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }

    /// <summary>Drop the in-memory calibration + cached load keys so the next
    /// frame re-reads from disk. Call after a build, a clear, or a mode change.</summary>
    public void ReloadGuideCalibration() {
        _loadedDarkKey = null;
        _loadedBpmKey = null;
    }

    /// <summary>Lazily load the master dark + bad-pixel map matching the current
    /// exposure/gain/bin, but only when the active mode needs them and the keys
    /// changed since the last load. A miss is cached (artifact left null) so we
    /// don't re-stat the disk every frame.</summary>
    private void EnsureCalibrationLoaded() {
        var mode = (Rig.NativeGuideCalibrationMode ?? "off").Trim().ToLowerInvariant();
        if (mode == "off") { _darkMaster = null; _bpmSet = null; _bpmCount = 0; return; }
        var (expMs, gain, bin) = CalParams();

        bool wantDark = mode is "dark" or "both";
        bool wantBpm = mode is "bpm" or "both";

        string darkKey = $"e{expMs}_g{gain}_b{bin}";
        if (wantDark && _loadedDarkKey != darkKey) {
            _loadedDarkKey = darkKey;
            _darkMaster = null; _darkLabel = null;
            try {
                var path = DarkPath(expMs, gain, bin);
                if (!File.Exists(path)) path = LegacyDarkPath(expMs, gain, bin);
                if (File.Exists(path)) {
                    using var fs = File.OpenRead(path);
                    var img = FITSReader.Read(fs);
                    _darkMaster = img.Data;
                    _darkW = img.Properties.Width;
                    _darkH = img.Properties.Height;
                    _darkLabel = $"{expMs}ms g{gain} bin{bin}";
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to load guide master dark");
            }
        } else if (!wantDark) {
            _darkMaster = null; _darkLabel = null; _loadedDarkKey = null;
        }

        string bpmKey = $"g{gain}_b{bin}";
        if (wantBpm && _loadedBpmKey != bpmKey) {
            _loadedBpmKey = bpmKey;
            _bpmSet = null; _bpmCount = 0;
            try {
                var path = BpmPath(gain, bin);
                if (!File.Exists(path)) path = LegacyBpmPath(gain, bin);
                if (File.Exists(path)) {
                    var map = JsonSerializer.Deserialize<BadPixelMapFile>(File.ReadAllText(path));
                    if (map?.Pixels != null) {
                        _bpmSet = new HashSet<int>(map.Pixels);
                        _bpmW = map.Width; _bpmH = map.Height; _bpmCount = map.Pixels.Length;
                    }
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to load guide bad-pixel map");
            }
        } else if (!wantBpm) {
            _bpmSet = null; _bpmCount = 0; _loadedBpmKey = null;
        }
    }

    /// <summary>Apply the active dark library / bad-pixel map to a freshly
    /// captured guide frame in place. No-op while building, when mode is off,
    /// or when a loaded artifact's dimensions don't match this frame (e.g. the
    /// user changed binning without rebuilding).</summary>
    private void ApplyCalibrationInPlace(ushort[] data, int width, int height) {
        if (_buildingDark) return;
        var mode = (Rig.NativeGuideCalibrationMode ?? "off").Trim().ToLowerInvariant();
        if (mode == "off") return;

        EnsureCalibrationLoaded();

        if ((mode is "dark" or "both") && _darkMaster != null
                && _darkW == width && _darkH == height) {
            GuideDarkMath.SubtractDarkInPlace(data, _darkMaster);
        }
        if ((mode is "bpm" or "both") && _bpmSet != null
                && _bpmW == width && _bpmH == height) {
            GuideDarkMath.ApplyBadPixelsInPlace(data, width, height, _bpmSet);
        }
    }

    /// <summary>Kick off a dark-library + bad-pixel-map build in the background.
    /// Stops any running loop first (the camera can only do one thing at a
    /// time). Returns immediately; progress + result surface via
    /// <see cref="DarkCalibration"/>.</summary>
    public async Task StartBuildCalibrationAsync() {
        if (_buildingDark) { RaiseAlert("A guide-dark build is already running."); return; }
        var cam = _equipment.GuideCamera;
        if (cam == null || !cam.IsConnected) {
            RaiseAlert("Connect the guide camera before building the dark library.");
            return;
        }
        await StopLoopAsync();
        _buildCts = new CancellationTokenSource();
        _ = Task.Run(() => BuildCalibrationAsync(_buildCts.Token));
    }

    /// <summary>Cancel an in-progress build (no-op if none).</summary>
    public void CancelBuildCalibration() {
        try { _buildCts?.Cancel(); } catch { }
    }

    private async Task BuildCalibrationAsync(CancellationToken ct) {
        _buildingDark = true;
        _buildError = null;
        _buildProgress = "Starting…";
        try {
            var cam = _equipment.GuideCamera!;
            var (expMs, gain, bin) = CalParams();
            int n = Math.Clamp(Rig.NativeGuideDarkFrames, 1, 100);
            var opts = new CaptureOptions(
                Gain: gain > 0 ? gain : (int?)null, BinX: bin, BinY: bin);

            try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }

            var frames = new List<ushort[]>(n);
            int w = 0, h = 0, bitDepth = 16;
            for (int i = 0; i < n; i++) {
                ct.ThrowIfCancellationRequested();
                _buildProgress = $"Capturing dark {i + 1}/{n}…";
                var img = await cam.CaptureAsync(expMs / 1000.0, opts, ct);
                if (img?.Data == null) { i--; continue; }   // dropped frame: retry this index
                if (w == 0) { w = img.Properties.Width; h = img.Properties.Height; bitDepth = img.Properties.BitDepth; }
                else if (img.Properties.Width != w || img.Properties.Height != h) {
                    throw new InvalidOperationException("Guide frame size changed mid-build.");
                }
                frames.Add(img.Data);
            }
            if (frames.Count < 1) throw new InvalidOperationException("No dark frames captured.");

            _buildProgress = "Integrating master dark…";
            var master = GuideDarkMath.MeanStack(frames, w * h);

            _buildProgress = "Mapping hot pixels…";
            var bad = GuideDarkMath.DetectBadPixels(master, sigmaK: 8.0);

            // Persist the raw darks + master dark (FITS) into the images tree so
            // they appear in FILES like every other frame, plus the bad-pixel
            // map (JSON) alongside them. Tag each DARK frame with the guide
            // camera's name so it's distinguishable from main-camera darks.
            var camName = cam.DeviceName;
            BaseImageData DarkFrame(ushort[] d) {
                var meta = new ImageMetaData {
                    CreationTime = DateTime.UtcNow,
                    Exposure = new ImageMetaData.ExposureInfo {
                        ExposureTime = expMs / 1000.0, ImageType = "DARK"
                    }
                };
                if (!string.IsNullOrWhiteSpace(camName)) meta.Camera.Name = camName;
                meta.Camera.Gain = gain;
                meta.Camera.BinX = (short)bin;
                meta.Camera.BinY = (short)bin;
                return new BaseImageData(d,
                    new ImageProperties { Width = w, Height = h, BitDepth = bitDepth }, meta);
            }

            Directory.CreateDirectory(SetDir(expMs, gain, bin));
            _buildProgress = "Saving dark frames…";
            for (int i = 0; i < frames.Count; i++) {
                ct.ThrowIfCancellationRequested();
                FITSWriter.Write(DarkFrame(frames[i]), SubPath(expMs, gain, bin, i + 1));
            }
            FITSWriter.Write(DarkFrame(master), DarkPath(expMs, gain, bin));

            var mapJson = JsonSerializer.Serialize(new BadPixelMapFile(w, h, bad));
            await File.WriteAllTextAsync(BpmPath(gain, bin), mapJson, ct);

            _logger.LogInformation(
                "Guide calibration built: {N} darks at {Exp}ms g{Gain} bin{Bin}, {Bad} bad pixels → {Dir}",
                frames.Count, expMs, gain, bin, bad.Length, SetDir(expMs, gain, bin));

            ReloadGuideCalibration();
            EnsureCalibrationLoaded();
            _buildProgress = $"Done: master dark + {bad.Length} bad pixels.";
            RaiseInfo($"Guide dark library built ({frames.Count} darks, {bad.Length} bad pixels).");
        } catch (OperationCanceledException) {
            _buildProgress = null;
            _logger.LogInformation("Guide calibration build cancelled");
        } catch (Exception ex) {
            _buildError = ex.Message;
            _buildProgress = null;
            _logger.LogError(ex, "Guide calibration build failed");
            RaiseAlert("Guide dark build failed: " + ex.Message);
        } finally {
            _buildingDark = false;
        }
    }

    /// <summary>Delete the stored master dark (current exposure/gain/bin) and
    /// bad-pixel map (current gain/bin), and clear them from memory.</summary>
    public void ClearGuideCalibration() {
        var (expMs, gain, bin) = CalParams();
        // Remove the whole per-(exp,gain,bin) folder (master + raw darks) and
        // the bad-pixel map, in both the current and the legacy locations.
        try { var sd = SetDir(expMs, gain, bin); if (Directory.Exists(sd)) Directory.Delete(sd, true); } catch { }
        try { File.Delete(BpmPath(gain, bin)); } catch { }
        try { File.Delete(LegacyDarkPath(expMs, gain, bin)); } catch { }
        try { File.Delete(LegacyBpmPath(gain, bin)); } catch { }
        _darkMaster = null; _darkLabel = null; _bpmSet = null; _bpmCount = 0;
        ReloadGuideCalibration();
        RaiseInfo("Guide dark library cleared.");
    }
}
