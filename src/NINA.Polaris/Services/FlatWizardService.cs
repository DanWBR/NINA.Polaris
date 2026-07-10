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
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Automates flat-field acquisition: for each selected filter, perform a
/// binary search on exposure time until the median pixel value falls within
/// a tolerance band around the target ADU, then capture a configurable
/// number of flat frames at that exposure. Trained exposures are persisted
/// per (filter, binning) tuple so the next session can skip the search.
///
/// Flow:
///   1. For each filter in the request, move the filter wheel to it.
///   2. Look up a previously-trained exposure for that filter+binning;
///      use it as the initial guess. Otherwise start at midpoint of
///      [minExp, maxExp].
///   3. Capture, measure median, decide:
///        median &gt; target+tol  → exposure too long  → halve upper bound
///        median &lt; target-tol  → exposure too short → double lower bound
///        else → converged; save trained exposure.
///   4. Capture N flat frames at the converged exposure and route them
///      through ImageWriterService so they're persisted with IMAGETYP=FLAT.
/// </summary>
public class FlatWizardService {
    private readonly EquipmentManager _equip;
    private readonly ImageWriterService _imageWriter;
    private readonly ProfileService _profile;
    private readonly ILogger<FlatWizardService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private readonly object _stateLock = new();

    public FlatWizardState State { get; private set; } = FlatWizardState.Idle;
    public FlatWizardProgress Progress { get; private set; } = new();
    public string? LastError { get; private set; }

    /// <summary>Per-binning, per-filter trained exposure cache (seconds).</summary>
    public Dictionary<string, double> TrainedExposures { get; private set; } = new();

    private string _trainedExposuresPath = "";

    public FlatWizardService(EquipmentManager equip, ImageWriterService imageWriter,
        ProfileService profile, ILogger<FlatWizardService> logger, IConfiguration config) {
        _equip = equip;
        _imageWriter = imageWriter;
        _profile = profile;
        _logger = logger;
        var dir = config.GetValue("FlatWizard:TrainedExposuresDir",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris"))!;
        Directory.CreateDirectory(dir);
        _trainedExposuresPath = Path.Combine(dir, "trained-flats.json");
        LoadTrainedExposures();
    }

    public void Start(FlatWizardRequest request) {
        lock (_stateLock) {
            if (State == FlatWizardState.Running)
                throw new InvalidOperationException("Flat wizard already running");
            if (_equip.Camera == null)
                throw new InvalidOperationException("No camera connected");
            if (request.Filters == null || request.Filters.Count == 0)
                throw new ArgumentException("At least one filter must be specified");
            if (request.MinExposure <= 0 || request.MaxExposure <= request.MinExposure)
                throw new ArgumentException("MinExposure must be > 0 and < MaxExposure");
            if (request.TargetAdu <= 0)
                throw new ArgumentException("TargetAdu must be > 0");

            _cts = new CancellationTokenSource();
            State = FlatWizardState.Running;
            LastError = null;
            Progress = new FlatWizardProgress {
                StartedAt = DateTime.UtcNow,
                TotalFilters = request.Filters.Count,
                TotalFramesPerFilter = request.FramesPerFilter
            };
        }

        _runTask = Task.Run(() => RunAsync(request, _cts!.Token));
        _logger.LogInformation("Flat wizard started: {N} filters, {F} frames each, target {Adu} ADU",
            request.Filters.Count, request.FramesPerFilter, request.TargetAdu);
    }

    public void Abort() {
        lock (_stateLock) {
            if (State != FlatWizardState.Running) return;
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// Public lookup into the trained-exposures cache. Returns true and
    /// the cached exposure if a previous wizard run (or auto-find from
    /// the sequence engine) converged on this (filter, binning, gain).
    /// Falls back to the legacy gain-less key so caches written before
    /// gain became part of the key still seed the search.
    ///
    /// NOTE: callers should treat this as a SEED, not a truth — panel
    /// brightness changes between sessions, so a cached value must be
    /// validated with a probe frame (AutoFindExposureAsync does exactly
    /// that: it seeds from this cache and its first iteration confirms
    /// the median before returning).
    /// </summary>
    public bool TryGetTrainedExposure(string filter, int binning, out double exposureSec, int? gain = null) {
        if (TrainedExposures.TryGetValue(TrainedKey(filter, binning, gain), out exposureSec))
            return true;
        return gain.HasValue
            && TrainedExposures.TryGetValue(TrainedKey(filter, binning, null), out exposureSec);
    }

    /// <summary>Cache key for a trained flat exposure. Gain is part of the
    /// key when known — the same panel needs a very different exposure at
    /// gain 0 vs gain 300, so a gain-less hit poisons the whole flat set.</summary>
    private static string TrainedKey(string? filter, int binning, int? gain) {
        var key = $"{filter ?? ""}_bin{Math.Max(1, binning)}";
        return (gain.HasValue && gain.Value > 0) ? $"{key}_g{gain.Value}" : key;
    }

    /// <summary>
    /// Run the binary-search half of the wizard for a single
    /// (filter, binning) pair without capturing the N flat frames
    /// afterwards. Returns the converged exposure (seconds) on
    /// success, null when convergence fails (bracket collapsed,
    /// camera missing, etc). On success the result is also written
    /// to the trained-exposures cache so the next call hits the
    /// short path via <see cref="TryGetTrainedExposure"/>.
    ///
    /// Intended for the AUTORUN sequence engine when the user picked
    /// "Auto" for a FLAT item's exposure: lookup → if miss, call
    /// this once before the capture loop, then capture N frames at
    /// the returned value. This stays out of the wizard's main
    /// state machine (does not flip <see cref="State"/>) so the
    /// AUTORUN run owns its own progress reporting.
    /// </summary>
    public async Task<double?> AutoFindExposureAsync(
        string filter, int binning,
        int targetAdu = 30000, double tolerance = 0.05,
        double minExposure = 0.001, double maxExposure = 30.0,
        int maxIterations = 14,
        int? gain = null, int? offset = null,
        CancellationToken ct = default) {
        var camera = _equip.Camera ?? throw new InvalidOperationException("No camera connected");
        binning = Math.Max(1, binning);
        int maxVal = (1 << camera.BitDepth) - 1;
        if (maxVal <= 0) maxVal = 65535;

        // Seed: a previously-trained value if we have one, otherwise a cheap
        // 0.5 s probe. Flats are in the camera's linear regime, so the median
        // scales ~linearly with exposure — a proportional step (below) lands
        // on target in 2-3 frames, and starting short keeps each probe fast
        // (the old midpoint seed of ~15 s made every iteration crawl).
        var key = TrainedKey(filter, binning, gain);
        double exposure = TryGetTrainedExposure(filter, binning, out var trained, gain)
            ? Math.Clamp(trained, minExposure, maxExposure)
            : Math.Clamp(0.5, minExposure, maxExposure);

        try { await camera.SetBinningAsync(binning, binning, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Auto-flat: set binning {B} failed", binning); }

        // Probe under the SAME conditions the flats will be captured with.
        // Without this the search ran at whatever gain/offset the previous
        // sequence item left on the camera, converged there, and the actual
        // flats (shot at the item's gain) landed at a completely different
        // ADU (field report: search "converged" but the histogram only hit
        // ~30k after manually forcing 0.015 s).
        var opts = new NINA.Image.Interfaces.CaptureOptions(
            Gain: (gain.HasValue && gain.Value > 0) ? gain : null,
            Offset: (offset.HasValue && offset.Value > 0) ? offset : null,
            BinX: binning, BinY: binning,
            ImageType: "FLAT");

        var lower = targetAdu * (1 - tolerance);
        var upper = targetAdu * (1 + tolerance);
        for (int attempt = 0; attempt < maxIterations; attempt++) {
            ct.ThrowIfCancellationRequested();
            _logger.LogDebug("Auto-flat search iter {I} for {Filter} bin{B} gain{G}: trying {Exp}s",
                attempt + 1, filter, binning, gain ?? -1, exposure);

            var img = await CameraCaptureGate.RunAsync(() => camera.CaptureAsync(exposure, opts, ct), ct);
            img.MetaData.Exposure.ImageType = "FLAT";
            var median = ComputeMedian(img);

            if (median >= lower && median <= upper) {
                _logger.LogInformation(
                    "Auto-flat converged at {Exp}s for {Filter} bin{B} (median={Med}, target={Tgt})",
                    exposure, filter, binning, median, targetAdu);
                TrainedExposures[key] = exposure;
                SaveTrainedExposures();
                return exposure;
            }

            var next = NextFlatExposure(exposure, median, targetAdu, maxVal, minExposure, maxExposure);
            // next == exposure means we're pinned at a clamp boundary and still
            // off target — this panel/light level can't reach the target ADU
            // within [min, max], so give up rather than spin forever.
            if (Math.Abs(next - exposure) < 1e-4) {
                _logger.LogWarning(
                    "Auto-flat can't reach {Tgt} ADU for {Filter} bin{B} within [{Min},{Max}]s (median {Med} at {Exp}s)",
                    targetAdu, filter, binning, minExposure, maxExposure, median, exposure);
                return null;
            }
            exposure = next;
        }

        _logger.LogWarning("Auto-flat did not converge for {Filter} bin{B} after {N} iterations",
            filter, binning, maxIterations);
        return null;
    }

    /// <summary>Proportional next-exposure step for the flat search. Flats are
    /// linear, so median ≈ k·exposure; the next guess is exposure·(target/median),
    /// damped to avoid wild swings, with hard handling for a dark panel (median
    /// near 0 → step up fast) and saturation (median near full-well → step down).
    /// Result is clamped to [min, max].</summary>
    private static double NextFlatExposure(double exp, double median, int target,
                                           int maxVal, double min, double max) {
        double next;
        if (median < 1) {
            next = exp * 4;                       // essentially dark — climb fast
        } else if (median >= 0.97 * maxVal) {
            next = exp * 0.5;                     // saturated — back off hard
        } else {
            double ratio = Math.Clamp(target / median, 0.25, 4.0);
            next = exp * ratio;
        }
        return Math.Clamp(next, min, max);
    }

    private async Task RunAsync(FlatWizardRequest request, CancellationToken ct) {
        var camera = _equip.Camera!;
        var fw = _equip.FilterWheel;
        var binning = Math.Max(1, request.Binning);
        var maxVal = (1 << camera.BitDepth) - 1;
        if (maxVal <= 0) maxVal = 65535;

        try {
            for (int fi = 0; fi < request.Filters.Count; fi++) {
                ct.ThrowIfCancellationRequested();
                var filterName = request.Filters[fi];
                Progress = Progress with { CurrentFilterIndex = fi, CurrentFilter = filterName };

                // 1. Switch filter
                if (fw != null && !string.IsNullOrEmpty(filterName)) {
                    _logger.LogInformation("Flat wizard: switching to filter {F}", filterName);
                    try { await fw.SetFilterByNameAsync(filterName, ct); }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Filter switch failed for {F}, using current filter", filterName);
                    }
                }

                // 2. Apply binning
                try { await camera.SetBinningAsync(binning, binning, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Set binning {B} failed", binning); }

                // 3. Find converged exposure via a proportional search.
                // Seed with a trained value if known, else a cheap 0.5 s probe
                // and scale toward target (flats are linear; see
                // NextFlatExposure). Starting short keeps the first probes fast
                // instead of crawling from the [min,max] midpoint.
                var key = $"{filterName}_bin{binning}";
                double exposure = TrainedExposures.TryGetValue(key, out var trained)
                    ? Math.Clamp(trained, request.MinExposure, request.MaxExposure)
                    : Math.Clamp(0.5, request.MinExposure, request.MaxExposure);
                bool converged = false;
                var lower = request.TargetAdu * (1 - request.Tolerance);
                var upper = request.TargetAdu * (1 + request.Tolerance);

                for (int attempt = 0; attempt < request.MaxSearchIterations; attempt++) {
                    ct.ThrowIfCancellationRequested();
                    Progress = Progress with { Phase = "searching", SearchAttempt = attempt + 1, CurrentExposure = exposure };
                    _logger.LogDebug("Flat search iter {I}: trying {Exp}s", attempt + 1, exposure);

                    var img = await CameraCaptureGate.RunAsync(() => camera.CaptureAsync(exposure, ct), ct);
                    img.MetaData.Exposure.ImageType = "FLAT";
                    var median = ComputeMedian(img);
                    Progress = Progress with { LastMedian = median };

                    if (median >= lower && median <= upper) {
                        _logger.LogInformation("Converged at {Exp}s (median={Med}, target={Tgt})",
                            exposure, median, request.TargetAdu);
                        converged = true;
                        TrainedExposures[key] = exposure;
                        SaveTrainedExposures();
                        break;
                    }

                    var next = NextFlatExposure(exposure, median, request.TargetAdu, maxVal,
                                                request.MinExposure, request.MaxExposure);
                    if (Math.Abs(next - exposure) < 1e-4) {
                        _logger.LogWarning(
                            "Flat search pinned at {Exp}s, can't reach {Tgt} ADU (last median {Med})",
                            exposure, request.TargetAdu, median);
                        break;
                    }
                    exposure = next;
                }

                if (!converged) {
                    Progress.FilterResults.Add(new FlatWizardFilterResult {
                        Filter = filterName, Converged = false, FinalExposure = exposure
                    });
                    continue;
                }

                // 4. Capture N flat frames at the converged exposure
                int saved = 0;
                for (int n = 0; n < request.FramesPerFilter; n++) {
                    ct.ThrowIfCancellationRequested();
                    Progress = Progress with { Phase = "capturing", FramesCaptured = n };
                    var img = await CameraCaptureGate.RunAsync(() => camera.CaptureAsync(exposure, ct), ct);
                    img.MetaData.Exposure.ImageType = "FLAT";
                    img.MetaData.Exposure.ExposureTime = exposure;
                    if (!string.IsNullOrEmpty(filterName))
                        img.MetaData.Exposure.Filter = filterName;
                    var path = _imageWriter.SaveImage(img, targetName: "Flat", imageType: "FLAT");
                    if (path != null) saved++;
                }

                Progress.FilterResults.Add(new FlatWizardFilterResult {
                    Filter = filterName, Converged = true, FinalExposure = exposure, FramesCaptured = saved
                });
            }

            lock (_stateLock) { State = FlatWizardState.Idle; }
            _logger.LogInformation("Flat wizard complete");

        } catch (OperationCanceledException) {
            lock (_stateLock) { State = FlatWizardState.Idle; LastError = "Cancelled"; }
            _logger.LogInformation("Flat wizard cancelled");
        } catch (Exception ex) {
            lock (_stateLock) { State = FlatWizardState.Idle; LastError = ex.Message; }
            _logger.LogError(ex, "Flat wizard failed");
        }
    }

    /// <summary>O(n) median via histogram, same trick as ImageStatistics.</summary>
    private static double ComputeMedian(IImageData img) {
        var data = img.Data;
        if (data.Length == 0) return 0;
        var hist = new int[65536];
        for (int i = 0; i < data.Length; i++) hist[data[i]]++;
        long half = data.Length / 2;
        long cum = 0;
        for (int i = 0; i < hist.Length; i++) {
            cum += hist[i];
            if (cum > half) return i;
        }
        return 0;
    }

    private void LoadTrainedExposures() {
        try {
            if (!File.Exists(_trainedExposuresPath)) return;
            var json = File.ReadAllText(_trainedExposuresPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            if (loaded != null) TrainedExposures = loaded;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to load trained flats from {Path}", _trainedExposuresPath);
        }
    }

    private void SaveTrainedExposures() {
        try {
            var json = JsonSerializer.Serialize(TrainedExposures,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_trainedExposuresPath, json);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to save trained flats");
        }
    }
}

public enum FlatWizardState { Idle, Running }

public class FlatWizardRequest {
    public List<string> Filters { get; set; } = new();
    public int FramesPerFilter { get; set; } = 20;
    public int TargetAdu { get; set; } = 30000;
    public double Tolerance { get; set; } = 0.05; // ±5%
    public double MinExposure { get; set; } = 0.001;
    public double MaxExposure { get; set; } = 30.0;
    public int Binning { get; set; } = 1;
    public int MaxSearchIterations { get; set; } = 12;
}

public record FlatWizardProgress {
    public DateTime StartedAt { get; init; }
    public int TotalFilters { get; init; }
    public int CurrentFilterIndex { get; init; }
    public string CurrentFilter { get; init; } = "";
    public string Phase { get; init; } = "idle";
    public int SearchAttempt { get; init; }
    public double CurrentExposure { get; init; }
    public double LastMedian { get; init; }
    public int TotalFramesPerFilter { get; init; }
    public int FramesCaptured { get; init; }
    public List<FlatWizardFilterResult> FilterResults { get; init; } = new();
}

public class FlatWizardFilterResult {
    public string Filter { get; set; } = "";
    public bool Converged { get; set; }
    public double FinalExposure { get; set; }
    public int FramesCaptured { get; set; }
}