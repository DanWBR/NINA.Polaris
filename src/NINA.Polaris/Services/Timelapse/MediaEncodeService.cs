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
using NINA.Polaris.Services.External;
using SkiaSharp;

namespace NINA.Polaris.Services.Timelapse;

public enum EncodeFormat { Gif, Mp4, Both }
public enum EncodePhase { Preparing, Rendering, Encoding, Ok, Fail }

/// <summary>
/// Turns an <see cref="IFrameSource"/> (a folder of stills for a time-lapse, or
/// a recorded SER for SER→MP4) into an animated GIF and/or an MP4. Async job
/// model + WS progress mirror <c>PlanetaryStackerService</c>: fire
/// <see cref="StartJob"/>, watch the <c>mediaEncode</c> WS block (or poll
/// <see cref="GetJob"/>), cancel via <see cref="Abort"/>.
///
/// Each frame is rendered once to a temp JPEG (the <see cref="IFrameSource"/>
/// does the decode + stretch + downscale); ffmpeg encodes those JPEGs to MP4,
/// and the self-contained <see cref="GifEncoder"/> reads them back for the GIF.
/// The GIF path always works; MP4 needs ffmpeg present, else it is skipped
/// (or the job fails if MP4 was the only requested format).
/// </summary>
public class MediaEncodeService {
    private readonly FfmpegService _ffmpeg;
    private readonly ILogger<MediaEncodeService> _logger;
    private readonly ConcurrentDictionary<string, EncodeJob> _jobs = new();

    public EncodeJob? CurrentJob { get; private set; }
    public event Action<EncodeJob>? JobUpdated;
    public bool FfmpegAvailable => _ffmpeg.IsAvailable;

    public MediaEncodeService(FfmpegService ffmpeg, ILogger<MediaEncodeService> logger) {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public EncodeJob StartJob(IFrameSource source, EncodeConfig cfg) {
        var job = new EncodeJob {
            Id = Guid.NewGuid().ToString("N"),
            Phase = EncodePhase.Preparing,
            StartedAt = DateTime.UtcNow,
            Config = cfg,
            Source = source
        };
        _jobs[job.Id] = job;
        JobRetention.TrimFinished(_jobs, j => j.StartedAt, j => j.CompletedAt != null);
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunAsync(job, job.Cts.Token));
        return job;
    }

    public EncodeJob? GetJob(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public void Abort(string id) { if (_jobs.TryGetValue(id, out var j)) j.Cts?.Cancel(); }

    private async Task RunAsync(EncodeJob job, CancellationToken ct) {
        var source = job.Source!;
        var cfg = job.Config;
        string? tmp = null;
        try {
            SetPhase(job, EncodePhase.Preparing);
            int count = source.Count;
            if (count <= 0) { Fail(job, "This source has no frames."); return; }
            job.TotalFrames = count;

            bool wantGif = cfg.Format is EncodeFormat.Gif or EncodeFormat.Both;
            bool wantMp4 = cfg.Format is EncodeFormat.Mp4 or EncodeFormat.Both;
            if (wantMp4 && !_ffmpeg.IsAvailable) {
                if (!wantGif) { Fail(job, "MP4 needs ffmpeg, which is not installed on this host."); return; }
                wantMp4 = false;   // keep the GIF; drop the MP4 silently
            }

            Directory.CreateDirectory(cfg.OutputDir);
            tmp = Path.Combine(cfg.OutputDir, ".timelapse-tmp", job.Id);
            Directory.CreateDirectory(tmp);

            // Render each frame once to a temp JPEG (shared by both encoders).
            SetPhase(job, EncodePhase.Rendering);
            for (int i = 0; i < count; i++) {
                ct.ThrowIfCancellationRequested();
                var jpeg = source.RenderJpeg(i, cfg.MaxDim, quality: 90);
                await File.WriteAllBytesAsync(Path.Combine(tmp, $"frame_{i:D5}.jpg"), jpeg, ct);
                job.FramesRendered = i + 1;
                if (i % 5 == 0 || i == count - 1) Notify(job);
            }

            SetPhase(job, EncodePhase.Encoding);
            var stamp = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss");
            var baseName = SanitizeName(cfg.OutputName) + "_" + stamp;

            if (wantMp4) {
                var outMp4 = Path.Combine(cfg.OutputDir, baseName + ".mp4");
                await _ffmpeg.EncodeAsync(tmp, "frame_%05d.jpg", cfg.Fps, outMp4,
                    onFrame: n => { job.EncodedFrames = n; Notify(job); }, ct: ct);
                job.Mp4Done = true; job.OutputPathMp4 = outMp4; Notify(job);
            }
            if (wantGif) {
                var outGif = Path.Combine(cfg.OutputDir, baseName + ".gif");
                using (var fs = File.Create(outGif)) {
                    GifEncoder.Encode(fs, count,
                        i => DecodeJpegToGifFrame(Path.Combine(tmp, $"frame_{i:D5}.jpg")),
                        cfg.Fps, cfg.Loop, ct);
                }
                job.GifDone = true; job.OutputPathGif = outGif; Notify(job);
            }

            SetPhase(job, EncodePhase.Ok);
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("Media encode OK: {N} frames -> gif={Gif} mp4={Mp4}",
                count, job.OutputPathGif, job.OutputPathMp4);
            Notify(job);
        } catch (OperationCanceledException) {
            Fail(job, "Cancelled");
        } catch (Exception ex) {
            _logger.LogError(ex, "Media encode failed");
            Fail(job, ex.Message);
        } finally {
            if (tmp != null) { try { Directory.Delete(tmp, recursive: true); } catch { } }
            try { source.Dispose(); } catch { }
        }
    }

    // Decode a rendered temp JPEG to packed RGB for the GIF encoder. SkiaSharp
    // has a native asset only on Linux (the deploy target); this only ever runs
    // there, which is why the GifEncoder itself stays Skia-free.
    private static GifFrame DecodeJpegToGifFrame(string path) {
        using var bmp = SKBitmap.Decode(path)
            ?? throw new InvalidOperationException("Could not read frame: " + Path.GetFileName(path));
        int w = bmp.Width, h = bmp.Height;
        var px = bmp.Pixels;                 // one native copy, SKColor[]
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < px.Length; i++) {
            int o = i * 3; rgb[o] = px[i].Red; rgb[o + 1] = px[i].Green; rgb[o + 2] = px[i].Blue;
        }
        return new GifFrame(rgb, w, h);
    }

    private static string SanitizeName(string? name) {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return "timelapse";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 80 ? name[..80] : name;
    }

    private void SetPhase(EncodeJob job, EncodePhase p) { job.Phase = p; Notify(job); }
    private void Fail(EncodeJob job, string error) {
        job.Error = error; job.Phase = EncodePhase.Fail; job.CompletedAt = DateTime.UtcNow;
        _logger.LogWarning("Media encode failed: {Error}", error);
        Notify(job);
    }
    private void Notify(EncodeJob job) { try { JobUpdated?.Invoke(job); } catch { } }
}

public record EncodeConfig(
    string OutputDir,
    string OutputName,
    int Fps = 15,
    int MaxDim = 1280,
    EncodeFormat Format = EncodeFormat.Gif,
    bool Loop = true);

public class EncodeJob {
    public string Id { get; set; } = "";
    public EncodePhase Phase { get; set; }
    public int TotalFrames { get; set; }
    public int FramesRendered { get; set; }
    public int EncodedFrames { get; set; }
    public bool GifDone { get; set; }
    public bool Mp4Done { get; set; }
    public string? OutputPathGif { get; set; }
    public string? OutputPathMp4 { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    internal EncodeConfig Config { get; set; } = new("", "");
    internal IFrameSource? Source { get; set; }
    internal Task? Task { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
}
