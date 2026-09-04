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

using NINA.Polaris.Services;
using NINA.Polaris.Services.Planetary;
using NINA.Polaris.Services.Timelapse;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Planetary video workflow, record the live camera stream to a SER
/// file (capture) and stack one into a single image (process).
/// </summary>
public static class VideoEndpoints {
    public static void MapVideoEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/video");

        // ----- Recording (capture) -----

        group.MapPost("/record/start", (VideoRecordingService rec, RecordStartRequest req) => {
            try {
                rec.Start(new RecordingConfig(
                    TargetName: req.TargetName ?? "planet",
                    MaxFrames: req.MaxFrames,
                    MaxDuration: req.MaxDurationSeconds is double s && s > 0
                        ? TimeSpan.FromSeconds(s) : null,
                    ColorMode: req.ColorMode,
                    // PLAN8: anything but an explicit 8 means 16, so a client
                    // that does not send the field keeps today's behaviour.
                    BitDepth: req.BitDepth == 8 ? 8 : 16,
                    SerDepth: req.SerDepth));
                return Results.Ok(new {
                    recording = true,
                    path = rec.OutputPath
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/record/stop", async (VideoRecordingService rec) => {
            await rec.StopAsync();
            return Results.Ok(new { recording = false });
        });

        group.MapGet("/record/status", (VideoRecordingService rec) => Results.Ok(new {
            recording = rec.IsRecording,
            path = rec.OutputPath,
            frames = rec.FrameCount,
            bytes = rec.BytesWritten,
            durationSec = rec.Duration.TotalSeconds,
            droppedFrames = rec.DroppedFrames,
            lastError = rec.LastError
        }));

        // ----- Recorded files (process picker) -----

        // List every *.ser recording, newest first. Authoritative + recursive:
        // the generic /api/files/list is non-recursive and the recordings live
        // a couple of levels down, so a client-side walk finds nothing.
        //
        // PLANPATH: two kinds of root. New clips go to
        // {ImageOutputDir}/{rig}/planetary; before that they all went to
        // {ImageOutputDir}/planetary, flat across every rig. The legacy root is
        // still scanned, so a season of existing recordings does not vanish
        // from the picker the moment the host updates. Nothing is moved --
        // that is the operator's data, and tens of gigabytes of it.
        group.MapGet("/recordings", (ProfileService profiles, FileBrowserService browser,
                                     string? dir) => {
            var outDir = profiles.Active.ImageOutputDir;

            // PLANNVME: an explicit folder overrides the capture-root scan, so a
            // clip kept on an external SSD / NVMe mount outside ~/files (which
            // the default scan never reaches) still shows in the process picker.
            // The file browser is full-disk (denylist only), so the same mounts
            // it lists are valid here; ResolveSafe just blocks the system dirs.
            if (!string.IsNullOrWhiteSpace(dir)) {
                string root;
                try { root = browser.ResolveSafe(dir!, mustExist: true); }
                catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
                if (!Directory.Exists(root))
                    return Results.BadRequest(new { error = "Not a directory: " + root });
                try {
                    var found = Directory.EnumerateFiles(root, "*.ser", SearchOption.AllDirectories)
                        .Select(p => new FileInfo(p))
                        .OrderByDescending(fi => fi.LastWriteTimeUtc)
                        .Select(fi => new {
                            path = fi.FullName,
                            name = fi.Name,
                            target = TargetOfRecording(outDir ?? "", fi.FullName),
                            rig = string.IsNullOrWhiteSpace(outDir) ? "" : RigOfRecording(outDir!, fi.FullName),
                            sizeBytes = fi.Length,
                            modifiedUtc = fi.LastWriteTimeUtc
                        })
                        .ToList();
                    return Results.Ok(new { recordings = found, scannedDir = root });
                } catch (Exception ex) {
                    return Results.Ok(new { recordings = Array.Empty<object>(), error = ex.Message });
                }
            }

            if (string.IsNullOrWhiteSpace(outDir) || !Directory.Exists(outDir))
                return Results.Ok(new { recordings = Array.Empty<object>() });

            // PLANSCAN: recordings now land at {rig}/{target}/planetary/x.ser --
            // a level deeper than the old {rig}/planetary. Walking a per-rig
            // "planetary" root with AllDirectories never reaches
            // {rig}/{target}/planetary, because that is a SIBLING of
            // {rig}/planetary, not a child. So every clip recorded with a target
            // set (Moon, Jupiter, ...) silently vanished from the process picker.
            // Scan the whole capture root (the Studio home) instead: a *.ser file
            // only ever lives in a planetary folder, so this covers every layout
            // past and present and matches nothing that is not a recording.
            try {
                var recordings = Directory.EnumerateFiles(outDir, "*.ser", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .Select(fi => new {
                        path = fi.FullName,
                        name = fi.Name,
                        target = TargetOfRecording(outDir, fi.FullName),
                        // Which rig a clip belongs to, so the picker can tell
                        // two identically named targets apart. Empty for the
                        // legacy tree, which predates the distinction.
                        rig = RigOfRecording(outDir, fi.FullName),
                        sizeBytes = fi.Length,
                        modifiedUtc = fi.LastWriteTimeUtc
                    })
                    .ToList();
                return Results.Ok(new { recordings });
            } catch (Exception ex) {
                return Results.Ok(new { recordings = Array.Empty<object>(), error = ex.Message });
            }
        });

        // ----- Rescale (salvage a right-aligned RAW16 clip) -----

        // PLANNVME/SERSCALE: rewrite an older SER whose native RAW16 was stored
        // right-aligned (0..4095 for a 12-bit sensor) into a full-range 16-bit
        // clip, so ZWO/FireCapture tools stop showing the bright colour cast.
        // Streams a multi-GB file, so it runs off the request thread. The path
        // is validated through the same (un-jailed) FileBrowserService the picker
        // uses, so a clip on an external mount is allowed.
        group.MapPost("/rescale", async (FileBrowserService browser, RescaleRequest req) => {
            if (string.IsNullOrWhiteSpace(req.SerPath))
                return Results.BadRequest(new { error = "serPath required" });
            string src;
            try { src = browser.ResolveSafe(req.SerPath, mustExist: true); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            string? outPath = null;
            if (!string.IsNullOrWhiteSpace(req.OutputPath)) {
                try { outPath = browser.ResolveSafe(req.OutputPath!, mustExist: false); }
                catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            }
            try {
                var res = await Task.Run(() => SerRescale.Rescale(src, req.Bits, outPath));
                return Results.Ok(new {
                    done = res.Done,
                    outputPath = res.OutputPath,
                    significantBits = res.SignificantBits,
                    shift = res.Shift,
                    frames = res.FrameCount,
                    message = res.Message
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ----- Stacking (process) -----

        group.MapPost("/stack/start", (PlanetaryStackerService stacker,
                                       ProfileService profiles,
                                       StackStartRequest req) => {
            if (string.IsNullOrWhiteSpace(req.SerPath))
                return Results.BadRequest(new { error = "serPath required" });
            var outDir = !string.IsNullOrWhiteSpace(req.OutputDir)
                ? req.OutputDir!
                : Path.Combine(Path.GetDirectoryName(req.SerPath) ?? ".", "stacked");
            var job = stacker.StartJob(new StackConfig(
                SerPath: req.SerPath,
                OutputDir: outDir,
                KeepPercent: req.KeepPercent ?? 50,
                OutputName: req.OutputName ?? "stack",
                AlignmentPoints: req.AlignmentPoints ?? true,
                ApHalfBox: Math.Clamp((req.ApBoxSize ?? 48) / 2, 8, 128),
                ApSearchWidth: Math.Clamp(req.ApSearchWidth ?? 14, 6, 60),
                ApFramePercent: Math.Clamp(req.ApFramePercent ?? 10, 1, 100),
                ApStructureThreshold: Math.Clamp(req.ApStructureThreshold ?? 0.04, 0, 1),
                ReferencePercent: Math.Clamp(req.ReferencePercent ?? 5, 1, 100)));
            return Results.Accepted($"/api/video/stack/{job.Id}", new { jobId = job.Id });
        });

        group.MapGet("/stack/{jobId}", (string jobId, PlanetaryStackerService stacker) => {
            var job = stacker.GetJob(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(new {
                id = job.Id,
                phase = job.Phase.ToString(),
                totalFrames = job.TotalFrames,
                framesAnalyzed = job.FramesAnalyzed,
                framesPicked = job.FramesPicked,
                framesAligned = job.FramesAligned,
                framesStacked = job.FramesStacked,
                alignmentPoints = job.AlignmentPointCount,
                outputPath = job.OutputPath,
                error = job.Error,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                done = job.Phase == StackPhase.Ok || job.Phase == StackPhase.Fail,
                // QualityScores deliberately omitted from the routine status
                // response (can be 10000+ doubles). Use /stack/{id}/qualities.
            });
        });

        group.MapGet("/stack/{jobId}/qualities", (string jobId, PlanetaryStackerService stacker) => {
            var job = stacker.GetJob(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(new { qualities = job.QualityScores ?? Array.Empty<double>() });
        });

        group.MapPost("/stack/{jobId}/abort", (string jobId, PlanetaryStackerService stacker) => {
            stacker.Abort(jobId);
            return Results.Ok(new { aborted = true });
        });

        // ----- Time-lapse (frames folder -> GIF / MP4) -----

        // Whether MP4 can be produced on this host (ffmpeg present). GIF always can.
        group.MapGet("/timelapse/ffmpeg-available", (MediaEncodeService encoder) =>
            Results.Ok(new { available = encoder.FfmpegAvailable }));

        // List the still image frames (FITS / JPG / PNG / TIFF / …) in a folder,
        // natural-sorted, for the time-lapse picker.
        group.MapGet("/timelapse/frames", (FileBrowserService browser, string? dir) => {
            if (string.IsNullOrWhiteSpace(dir))
                return Results.BadRequest(new { error = "dir is required" });
            string root;
            try { root = browser.ResolveSafe(dir!, mustExist: true); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (!Directory.Exists(root))
                return Results.BadRequest(new { error = "Not a directory: " + root });
            try {
                var frames = ImageFrames(root)
                    .Select(p => new FileInfo(p))
                    .Select(fi => new { path = fi.FullName, name = fi.Name, sizeBytes = fi.Length })
                    .ToList();
                return Results.Ok(new { frames, count = frames.Count, dir = root });
            } catch (Exception ex) {
                return Results.Ok(new { frames = Array.Empty<object>(), count = 0, error = ex.Message });
            }
        });

        group.MapPost("/timelapse/start", (MediaEncodeService encoder, FileBrowserService browser,
                                           TimelapseStartRequest req) => {
            if (string.IsNullOrWhiteSpace(req?.Dir))
                return Results.BadRequest(new { error = "dir is required" });
            string root;
            try { root = browser.ResolveSafe(req.Dir!, mustExist: true); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            var files = ImageFrames(root).ToList();
            if (files.Count == 0)
                return Results.BadRequest(new { error = "No image frames in that folder." });
            var fmt = ParseFormat(req.Format);
            if (fmt is EncodeFormat.Mp4 or EncodeFormat.Both && !encoder.FfmpegAvailable && fmt == EncodeFormat.Mp4)
                return Results.BadRequest(new { error = "MP4 needs ffmpeg, which is not installed on this host." });
            // Back-compat: the old boolean "center the disc" maps to "center".
            var alignMode = !string.IsNullOrWhiteSpace(req.AlignMode) ? req.AlignMode
                          : (req.Center == true ? "center" : null);
            var cfg = new EncodeConfig(
                OutputDir: Path.Combine(root, "timelapse"),
                OutputName: req.OutputName ?? "timelapse",
                Fps: Math.Clamp(req.Fps ?? 15, 1, 60),
                MaxDim: Math.Clamp(req.MaxDim ?? 1280, 100, 4000),
                Format: fmt,
                Loop: req.Loop ?? true,
                AlignMode: alignMode);
            var job = encoder.StartJob(
                new FolderFrameSource(files, Math.Max(1, req.EveryNth ?? 1),
                    perFrameStretch: req.AutoContrast ?? false, hdr: req.AutoHdr ?? false),
                cfg);
            return Results.Accepted($"/api/video/timelapse/{job.Id}", new { jobId = job.Id });
        });

        // Convert a recorded SER clip to MP4 (reuses the same encoder; MP4-only,
        // so it requires ffmpeg).
        group.MapPost("/ser-to-mp4", (MediaEncodeService encoder, FileBrowserService browser,
                                      SerToMp4Request req) => {
            if (string.IsNullOrWhiteSpace(req?.SerPath))
                return Results.BadRequest(new { error = "serPath is required" });
            if (!encoder.FfmpegAvailable)
                return Results.BadRequest(new { error = "MP4 needs ffmpeg, which is not installed on this host." });
            string ser;
            try { ser = browser.ResolveSafe(req.SerPath!, mustExist: true); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            SerFrameSource source;
            try { source = new SerFrameSource(new SerFileReader(ser)); }
            catch (Exception ex) { return Results.BadRequest(new { error = "Could not open SER: " + ex.Message }); }
            var cfg = new EncodeConfig(
                OutputDir: req.OutputDir ?? Path.GetDirectoryName(ser)!,
                OutputName: req.OutputName ?? Path.GetFileNameWithoutExtension(ser),
                Fps: Math.Clamp(req.Fps ?? 20, 1, 60),
                MaxDim: Math.Clamp(req.MaxDim ?? 1600, 100, 4000),
                Format: EncodeFormat.Mp4,
                Loop: true);
            var job = encoder.StartJob(source, cfg);
            return Results.Accepted($"/api/video/timelapse/{job.Id}", new { jobId = job.Id });
        });

        group.MapGet("/timelapse/{jobId}", (MediaEncodeService encoder, string jobId) => {
            var j = encoder.GetJob(jobId);
            if (j == null) return Results.NotFound(new { error = "No such job" });
            return Results.Ok(new {
                id = j.Id, phase = j.Phase.ToString(), totalFrames = j.TotalFrames,
                framesRendered = j.FramesRendered, encodedFrames = j.EncodedFrames,
                gifDone = j.GifDone, mp4Done = j.Mp4Done,
                outputPathGif = j.OutputPathGif, outputPathMp4 = j.OutputPathMp4, error = j.Error,
                done = j.Phase is EncodePhase.Ok or EncodePhase.Fail
            });
        });

        group.MapPost("/timelapse/{jobId}/abort", (MediaEncodeService encoder, string jobId) => {
            encoder.Abort(jobId);
            return Results.Ok(new { aborted = true });
        });
    }

    // Still image frames in a folder, natural-sorted (so frame_2 sorts before
    // frame_10 even when the sequence is not zero-padded).
    private static IEnumerable<string> ImageFrames(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(p => FileBrowserService.ClassifyForPreview(p)
                is PreviewKind.Fits or PreviewKind.RasterPassthrough or PreviewKind.TiffDecode)
            .OrderBy(p => System.Text.RegularExpressions.Regex.Replace(
                Path.GetFileName(p), @"\d+", m => m.Value.PadLeft(12, '0')),
                StringComparer.OrdinalIgnoreCase);

    private static EncodeFormat ParseFormat(string? f) => (f ?? "").Trim().ToLowerInvariant() switch {
        "gif" => EncodeFormat.Gif,
        "mp4" => EncodeFormat.Mp4,
        _ => EncodeFormat.Both
    };

    /// <summary>The rig a recording sits under, read back from its path.
    ///
    /// <para>Three shapes are on disk at once and all of them have to read
    /// back, because nothing rewrites clips that are already written:</para>
    /// <list type="bullet">
    ///   <item>current: <c>{rig}/{target}/planetary/x.ser</c></item>
    ///   <item>no target: <c>{rig}/planetary/x.ser</c>, and the same shape as
    ///         the previous layout's <c>{rig}/planetary/{target}/x.ser</c></item>
    ///   <item>legacy, pre per-rig folders: <c>planetary/{target}/x.ser</c>,
    ///         which genuinely does not know which rig shot it</item>
    /// </list>
    ///
    /// <para>So: look for the "planetary" segment anywhere FROM INDEX 1. Found
    /// means segment 0 is the rig. Not found leaves only the legacy tree, where
    /// segment 0 is the literal folder. Starting the search at 1 rather than 0
    /// is what keeps a rig actually named "planetary" from reading as a legacy
    /// clip.</para></summary>
    private static string RigOfRecording(string outDir, string filePath) {
        try {
            var rel = Path.GetRelativePath(outDir, filePath);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 1; i < parts.Length - 1; i++) {
                if (parts[i].Equals("planetary", StringComparison.OrdinalIgnoreCase))
                    return parts[0];
            }
        } catch { /* a path outside the capture root has no rig to report */ }
        return "";
    }

    /// <summary>The target a recording sits under, read back from its path, so
    /// the picker can label a clip "Moon" / "Jupiter" instead of the literal
    /// "planetary" folder it lives in. Handles every on-disk layout:
    /// <list type="bullet">
    ///   <item>current <c>{rig}/{target}/planetary/x.ser</c> and previous
    ///         <c>{rig}/planetary/{target}/x.ser</c> → the target folder</item>
    ///   <item>no target <c>{rig}/planetary/x.ser</c> → empty (the folder above
    ///         "planetary" is the rig, not a target)</item>
    ///   <item>legacy <c>planetary/{target}/x.ser</c> → the target folder</item>
    /// </list></summary>
    private static string TargetOfRecording(string outDir, string filePath) {
        try {
            var parent = new FileInfo(filePath).Directory;
            if (parent == null) return "";
            // Recorded directly under a "planetary" folder: the target, if any,
            // is the folder ABOVE it -- unless that folder is the rig root.
            if (parent.Name.Equals("planetary", StringComparison.OrdinalIgnoreCase)) {
                var grand = parent.Parent;
                if (grand == null) return "";
                var rig = RigOfRecording(outDir, filePath);
                if (!string.IsNullOrEmpty(rig) &&
                    grand.Name.Equals(rig, StringComparison.OrdinalIgnoreCase)) return "";
                return grand.Name.Equals("planetary", StringComparison.OrdinalIgnoreCase)
                    ? "" : grand.Name;
            }
            // Older layouts keep the clip directly inside the target folder.
            return parent.Name;
        } catch { /* an unreadable path has no target to report */ }
        return "";
    }

    public record RecordStartRequest(
        string? TargetName = null,
        int? MaxFrames = null,
        double? MaxDurationSeconds = null,
        SerColorMode? ColorMode = null,
        /// <summary>PLAN8: 8 or 16 bits per sample on disk. Omitted = 16.</summary>
        int? BitDepth = null,
        /// <summary>SERSCALE-3: sample alignment. Omitted = Auto; 16 = Off;
        /// 8..15 = treat the stream as that many significant bits.</summary>
        int? SerDepth = null);

    public record StackStartRequest(
        string SerPath,
        string? OutputDir = null,
        double? KeepPercent = null,
        string? OutputName = null,
        /// <summary>PLANETAP: local registration on an alignment-point mesh.
        /// Omitted = on. Box/search in pixels, percent of frames kept per
        /// point, structure threshold as a fraction of the best point (0..1),
        /// percent of best frames averaged into the reference.</summary>
        bool? AlignmentPoints = null,
        int? ApBoxSize = null,
        int? ApSearchWidth = null,
        double? ApFramePercent = null,
        double? ApStructureThreshold = null,
        double? ReferencePercent = null);

    public record RescaleRequest(
        string SerPath,
        /// <summary>Significant ADC depth (8..16). Omitted = auto-detect.</summary>
        int? Bits = null,
        string? OutputPath = null);

    public record TimelapseStartRequest(
        string? Dir,
        int? Fps = null,
        int? MaxDim = null,
        /// <summary>"gif" | "mp4" | "both" (default). MP4 needs ffmpeg.</summary>
        string? Format = null,
        int? EveryNth = null,
        string? OutputName = null,
        bool? Loop = null,
        /// <summary>Frame registration: "off" | "auto" | "center" (move a bright
        /// disc to the middle) | "stabilize" (lock a filled surface to frame 0).</summary>
        string? AlignMode = null,
        /// <summary>Auto-contrast: stretch each frame on its own histogram.</summary>
        bool? AutoContrast = null,
        /// <summary>Auto-HDR: asinh tone curve per frame (eclipse dynamic range).</summary>
        bool? AutoHdr = null,
        /// <summary>Legacy boolean center-the-disc option; maps to AlignMode="center".</summary>
        bool? Center = null);

    public record SerToMp4Request(
        string? SerPath,
        int? Fps = null,
        int? MaxDim = null,
        string? OutputName = null,
        string? OutputDir = null);
}