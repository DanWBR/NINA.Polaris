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
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Subframe grading: measure the quality of a set of light frames and
/// rank/select the best for stacking. The offline "which subs are keepers"
/// pass — reads each frame once, detects stars (on luminance for OSC),
/// derives median HFR + star count + median eccentricity, and hands the
/// metrics to <see cref="FrameGrading"/> for scoring and keep/reject
/// selection.
///
/// Runs as a background job (like <see cref="BatchStackingService"/>) with
/// progress, because star-detecting a whole night of subs is O(N) FITS
/// reads and can take minutes on an SBC. The typical use is:
///   grade a night -> take the returned <c>Selected</c> paths -> feed them
///   into <see cref="BatchStackingService"/> integration.
///
/// Frame selection (paths vs. library query) mirrors the STUDIO browser:
/// callers pass explicit <c>FramePaths</c>, or a
/// type/filter/target/date-window query resolved against the
/// <see cref="FrameLibraryService"/> index.
/// </summary>
public class FrameGradingService {
    private readonly FrameLibraryService _library;
    private readonly ILogger<FrameGradingService> _logger;
    private readonly ConcurrentDictionary<string, GradeProgress> _jobs = new();

    public FrameGradingService(FrameLibraryService library,
                               ILogger<FrameGradingService> logger) {
        _library = library;
        _logger = logger;
    }

    /// <param name="FramePaths">Explicit frames to grade. When null/empty,
    /// the library is queried with the filters below.</param>
    /// <param name="KeepBest">Keep the N highest-scoring frames.</param>
    /// <param name="HfrTolerancePct">Keep frames within this % of the best
    /// HFR (ignored when <paramref name="KeepBest"/> is set).</param>
    public record GradeRequest(
        List<string>? FramePaths,
        string? Type,
        string? Filter,
        string? Target,
        string? DateFrom,
        string? DateTo,
        int? Limit,
        int? KeepBest,
        double? HfrTolerancePct);

    public string StartJob(GradeRequest req) {
        var paths = ResolvePaths(req);
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new GradeProgress {
            JobId = jobId,
            InProgress = true,
            Total = paths.Count,
            Stage = paths.Count == 0 ? "error" : "queued",
            Error = paths.Count == 0 ? "No frames matched the request." : null,
        };
        if (paths.Count == 0) {
            _jobs[jobId] = _jobs[jobId] with { InProgress = false };
            return jobId;
        }
        _ = Task.Run(() => RunJob(jobId, paths, req.KeepBest, req.HfrTolerancePct));
        return jobId;
    }

    public GradeProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    /// <summary>Re-apply the keep rule to a finished job with new thresholds.
    ///
    /// <para>Measuring the frames is the expensive half (a star detection pass
    /// per FITS); choosing where to cut is arithmetic on numbers we already
    /// have. Separating them is what lets the operator drag a threshold and see
    /// the keep set move without re-reading a night of subs, and it keeps the
    /// rule in <see cref="FrameGrading.Rank"/> as the single definition of what
    /// "keep" means, rather than a second copy of it in the browser.</para>
    ///
    /// <para>Returns null when the job is unknown or still running.</para>
    /// </summary>
    public GradeProgress? Reselect(string jobId, int? keepBest, double? hfrTolerancePct) {
        if (!_jobs.TryGetValue(jobId, out var job)) return null;
        if (job.InProgress || job.Results.Count == 0) return null;

        var metrics = job.Results
            .Select(r => new FrameGrading.FrameMetric(
                r.Path, r.FileName, r.Stars, r.Hfr, r.Eccentricity))
            .ToList();
        var ranked = FrameGrading.Rank(metrics, keepBest, hfrTolerancePct);
        var selected = FrameGrading.Selected(ranked);

        var updated = job with {
            Results = ranked.Select(r => new GradedFrameDto(
                r.Path, r.FileName, r.Stars, r.Hfr, r.Eccentricity,
                r.Score, r.Keep)).ToList(),
            Selected = selected.ToList(),
            SelectedCount = selected.Count,
        };
        _jobs[jobId] = updated;
        return updated;
    }

    /// <summary>Resolve the frame path list: explicit paths win; otherwise
    /// query the library index. Defaults the type filter to LIGHT — grading
    /// darks/flats/bias makes no sense.</summary>
    private List<string> ResolvePaths(GradeRequest req) {
        if (req.FramePaths is { Count: > 0 })
            return req.FramePaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var q = new FrameQuery(
            Type:     string.IsNullOrWhiteSpace(req.Type) ? "LIGHT" : req.Type,
            Filter:   req.Filter,
            Target:   req.Target,
            DateFrom: req.DateFrom,
            DateTo:   req.DateTo,
            Limit:    req.Limit ?? 500,
            Offset:   0);
        return _library.Query(q).Select(r => r.Path).ToList();
    }

    private void RunJob(string jobId, IReadOnlyList<string> paths,
                        int? keepBest, double? hfrTolerancePct) {
        try {
            _jobs[jobId] = _jobs[jobId] with { Stage = "grading", Done = 0 };
            var detector = new StarDetector();
            var metrics = new List<FrameGrading.FrameMetric>(paths.Count);

            for (int i = 0; i < paths.Count; i++) {
                var path = paths[i];
                var name = Path.GetFileName(path);
                try {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
                        // Missing on disk: record as junk so it sorts to the
                        // bottom and is never kept, but still count it.
                        metrics.Add(new FrameGrading.FrameMetric(path, name, 0, 0, 0));
                    } else {
                        var (stars, hfr, ecc) = Measure(detector, path);
                        metrics.Add(new FrameGrading.FrameMetric(path, name, stars, hfr, ecc));
                    }
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Grading: failed to measure {Path}", path);
                    metrics.Add(new FrameGrading.FrameMetric(path, name, 0, 0, 0));
                }
                _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
            }

            var ranked = FrameGrading.Rank(metrics, keepBest, hfrTolerancePct);
            var selected = FrameGrading.Selected(ranked);

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                Results = ranked.Select(r => new GradedFrameDto(
                    r.Path, r.FileName, r.Stars,
                    Math.Round(r.Hfr, 3), Math.Round(r.Eccentricity, 3),
                    r.Score, r.Keep)).ToList(),
                Selected = selected.ToList(),
                SelectedCount = selected.Count,
            };
            _logger.LogInformation(
                "Grading job {Job}: graded {N} frames, selected {K} keepers",
                jobId, metrics.Count, selected.Count);
        } catch (Exception ex) {
            _logger.LogError(ex, "Grading job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false, Stage = "error", Error = ex.Message
            };
        }
    }

    /// <summary>Read one frame and return (starCount, medianHfr,
    /// medianEccentricity). Detects on luminance for OSC so the CFA mosaic
    /// isn't mistaken for structure — same approach as the batch stacker's
    /// drizzle advisor.</summary>
    private static (int Stars, double Hfr, double Ecc) Measure(StarDetector detector, string path) {
        BaseImageData img;
        using (var fs = File.OpenRead(path)) img = FITSReader.Read(fs);
        ushort[] src = img.Data;
        var pat = img.Properties.BayerPattern;
        if (pat != NINA.Core.Enum.BayerPatternEnum.None) {
            var ch = BayerDebayer.Bilinear(img.Data, img.Properties.Width, img.Properties.Height, pat);
            src = BayerDebayer.ToLuminance(ch);
        }
        var stars = detector.Detect(src, img.Properties.Width, img.Properties.Height);
        if (stars.Count == 0) return (0, 0, 0);
        var hfrs = stars.Where(s => s.HFR > 0).Select(s => s.HFR).OrderBy(v => v).ToList();
        var eccs = stars.Select(s => s.Eccentricity).OrderBy(v => v).ToList();
        double medHfr = hfrs.Count > 0 ? hfrs[hfrs.Count / 2] : 0;
        double medEcc = eccs.Count > 0 ? eccs[eccs.Count / 2] : 0;
        return (stars.Count, medHfr, medEcc);
    }
}

public record GradedFrameDto(
    string Path, string FileName, int Stars, double Hfr,
    double Eccentricity, double Score, bool Keep);

public record GradeProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public string Stage { get; init; } = "";
    public string? Error { get; init; }

    /// <summary>Every graded frame, best score first.</summary>
    public List<GradedFrameDto> Results { get; init; } = new();

    /// <summary>Paths of the keepers, ranked — feed straight to integration.</summary>
    public List<string> Selected { get; init; } = new();
    public int SelectedCount { get; init; }
}
