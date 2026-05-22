using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Headless.Services.Studio;

/// <summary>
/// Apply previously-built master frames (bias / dark / flat) to a batch
/// of raw light frames. The classical pipeline is:
///
///   calibrated = (light − dark) / normalized_flat
///
/// where <c>normalized_flat = flat_corrected / mean(flat_corrected)</c> and
/// <c>flat_corrected = master_flat − (master_dark_flat ?? master_bias)</c>.
///
/// In words: subtract the dark (removes thermal signal + bias offset),
/// then divide by the flat (corrects vignetting + dust shadows).
/// Bias is only applied directly when no dark is provided — darks already
/// contain the bias signal, so subtracting both double-counts.
///
/// Auto-matching: for each light, the service picks the master whose
/// (gain, exposure) is closest for darks and (gain, filter) matches for
/// flats. Override IDs in the request let the user pin a specific master
/// regardless of what auto-match would have picked.
///
/// Output goes to {rig}/calibrated/{target}/{filter}/cal_{originalName}.fits
/// with a CALSTAT header recording which corrections were applied (B / D / F).
/// </summary>
public class CalibrationService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<CalibrationService> _logger;
    private readonly ConcurrentDictionary<string, CalibrationProgress> _jobs = new();

    public CalibrationService(FrameLibraryService library, ProfileService profile,
                              ILogger<CalibrationService> logger) {
        _library = library;
        _profile = profile;
        _logger = logger;
    }

    public record CalibrationRequest(
        List<int> LightIds,
        // Override hooks — null = auto-match each light to nearest
        // master. Setting these pins a specific master for the whole
        // batch (useful when auto-match's nearest-neighbour pick is
        // not what the user wants).
        int? MasterDarkId,
        int? MasterFlatId,
        int? MasterBiasId);

    public string StartJob(CalibrationRequest req) {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new CalibrationProgress {
            JobId = jobId,
            InProgress = true,
            Total = req.LightIds.Count,
            Stage = "queued"
        };
        _ = Task.Run(() => RunJob(jobId, req));
        return jobId;
    }

    public CalibrationProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, CalibrationRequest req) {
        try {
            // Available masters in the library, by category. Pulled
            // once at job start — if the user creates a new master
            // mid-job they'd have to re-start anyway.
            var masters = LoadMasterIndex();
            _logger.LogInformation("Calibration job {Job}: {Lights} lights, masters available: " +
                "darks={D} flats={F} biases={B}",
                jobId, req.LightIds.Count,
                masters.Darks.Count, masters.Flats.Count, masters.Biases.Count);

            // Cache decoded masters across lights — a typical session
            // uses 1-2 darks and 1-2 flats for an entire batch.
            var loadedMasters = new Dictionary<int, BaseImageData>();
            BaseImageData LoadMaster(int id) {
                if (loadedMasters.TryGetValue(id, out var cached)) return cached;
                var row = _library.GetById(id)
                    ?? throw new InvalidOperationException($"Master {id} missing from library.");
                using var fs = File.OpenRead(row.Path);
                var img = FITSReader.Read(fs);
                loadedMasters[id] = img;
                return img;
            }

            // Precompute normalized flat per (flatId, calibratorId) key.
            var flatCache = new Dictionary<(int flat, int? cal), (double[] norm, double mean)>();
            (double[] norm, double mean) GetNormalizedFlat(int flatId, int? calibratorId) {
                var key = (flatId, calibratorId);
                if (flatCache.TryGetValue(key, out var cached)) return cached;
                var flat = LoadMaster(flatId);
                BaseImageData? cal = calibratorId.HasValue ? LoadMaster(calibratorId.Value) : null;
                var (norm, mean) = NormalizeFlat(flat, cal);
                flatCache[key] = (norm, mean);
                return (norm, mean);
            }

            var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
            var outRoot = _profile.Active.ImageOutputDir
                ?? throw new InvalidOperationException("ImageOutputDir not set.");

            int done = 0;
            int succeeded = 0;
            int failed = 0;
            string? firstError = null;

            for (int i = 0; i < req.LightIds.Count; i++) {
                var lightId = req.LightIds[i];
                _jobs[jobId] = _jobs[jobId] with {
                    Stage = $"calibrating {i + 1}/{req.LightIds.Count}",
                    Done = done
                };
                try {
                    CalibrateOne(lightId, req, masters, LoadMaster, GetNormalizedFlat,
                                 outRoot, rigName);
                    succeeded++;
                } catch (Exception ex) {
                    failed++;
                    if (firstError == null) firstError = $"{lightId}: {ex.Message}";
                    _logger.LogWarning(ex, "Calibration of frame {Id} failed", lightId);
                }
                done++;
            }

            // Re-index so the new calibrated frames appear in the browser.
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                Done = done,
                Succeeded = succeeded,
                Failed = failed,
                Error = firstError
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Calibration job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "error",
                Error = ex.Message
            };
        }
    }

    private void CalibrateOne(
            int lightId, CalibrationRequest req, MasterIndex masters,
            Func<int, BaseImageData> loadMaster,
            Func<int, int?, (double[] norm, double mean)> getNormFlat,
            string outRoot, string rigName) {
        var row = _library.GetById(lightId)
            ?? throw new InvalidOperationException($"Light {lightId} not found in library.");
        using var fs = File.OpenRead(row.Path);
        var light = FITSReader.Read(fs);
        int W = light.Properties.Width;
        int H = light.Properties.Height;
        int gain = light.MetaData.Camera.Gain;
        double exposure = light.MetaData.Exposure.ExposureTime;
        string filter = light.MetaData.Exposure.Filter ?? "";

        // ---- Pick which masters to apply --------------------------
        int? darkId = req.MasterDarkId ?? FindNearestDark(masters.Darks, exposure, gain);
        int? flatId = req.MasterFlatId ?? FindMatchingFlat(masters.Flats, filter, gain);
        // Bias only matters if we don't have a dark — darks already
        // include the bias signal. If user *explicitly* passes a bias
        // id we honour it as a flat-calibrator override.
        int? biasId = req.MasterBiasId ?? (darkId == null ? FindMatchingBias(masters.Biases, gain) : null);
        // Flat needs a calibration frame to subtract before normalising:
        // prefer master_dark_flat (matched on flat's exposure+gain),
        // fall back to master_bias.
        int? flatCalibrator = null;
        if (flatId.HasValue) {
            var flatMeta = loadMaster(flatId.Value).MetaData;
            int? darkFlatId = FindNearestDark(masters.DarkFlats,
                flatMeta.Exposure.ExposureTime, flatMeta.Camera.Gain);
            flatCalibrator = darkFlatId ?? biasId ?? req.MasterBiasId;
        }

        if (darkId == null && flatId == null && biasId == null) {
            throw new InvalidOperationException(
                $"No matching masters found for this light (gain={gain}, " +
                $"exposure={exposure}s, filter='{filter}').");
        }

        // ---- Pixel math (in-place on a copy) ----------------------
        var pixels = new ushort[light.Data.Length];
        var src = light.Data;
        ushort[]? darkPx = darkId.HasValue ? loadMaster(darkId.Value).Data : null;
        ushort[]? biasPx = (darkId == null && biasId.HasValue) ? loadMaster(biasId.Value).Data : null;
        (double[] norm, double mean)? flat = flatId.HasValue
            ? getNormFlat(flatId.Value, flatCalibrator)
            : null;

        if (darkPx != null && darkPx.Length != pixels.Length)
            throw new InvalidOperationException("Master dark dimensions don't match light.");
        if (biasPx != null && biasPx.Length != pixels.Length)
            throw new InvalidOperationException("Master bias dimensions don't match light.");
        if (flat.HasValue && flat.Value.norm.Length != pixels.Length)
            throw new InvalidOperationException("Master flat dimensions don't match light.");

        Parallel.For(0, pixels.Length, idx => {
            double v = src[idx];
            if (darkPx != null) v -= darkPx[idx];
            else if (biasPx != null) v -= biasPx[idx];
            if (flat.HasValue) {
                var n = flat.Value.norm[idx];
                if (n > 1e-6) v /= n;
            }
            pixels[idx] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        });

        // ---- Write output ----------------------------------------
        var target = string.IsNullOrEmpty(row.Target) ? "Unknown" : row.Target;
        var filterFolder = string.IsNullOrEmpty(filter) ? "L" : filter;
        var dir = Path.Combine(outRoot, Sanitize(rigName), "calibrated",
            Sanitize(target), Sanitize(filterFolder));
        Directory.CreateDirectory(dir);
        var fileName = "cal_" + Path.GetFileName(row.Path);
        var outPath = Path.Combine(dir, fileName);
        int copy = 1;
        while (File.Exists(outPath)) {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            outPath = Path.Combine(dir, $"{stem}_{copy++}.fits");
        }

        // Carry every header from the light so OBJCTRA/DEC + camera +
        // observer metadata survives calibration — Siril and PixInsight
        // expect those untouched on the calibrated frame.
        var props = new ImageProperties {
            Width = W, Height = H, BitDepth = light.Properties.BitDepth,
            BayerPattern = light.Properties.BayerPattern,
            IsBayered = light.Properties.IsBayered
        };
        var calibrated = new BaseImageData(pixels, props, light.MetaData);
        // CALSTAT = letters describing what got applied; matches the
        // SBIG convention echoed by Siril and PixInsight.
        var calstat = (biasPx != null ? "B" : "") + (darkPx != null ? "D" : "") + (flat.HasValue ? "F" : "");
        var kw = new List<KeyValuePair<string, string>> {
            new("CALSTAT", calstat)
        };
        if (darkId.HasValue)
            kw.Add(new("MDARK", Path.GetFileName(_library.GetById(darkId.Value)?.Path ?? "")));
        if (flatId.HasValue)
            kw.Add(new("MFLAT", Path.GetFileName(_library.GetById(flatId.Value)?.Path ?? "")));
        if (biasId.HasValue && darkId == null)
            kw.Add(new("MBIAS", Path.GetFileName(_library.GetById(biasId.Value)?.Path ?? "")));
        FITSWriter.Write(calibrated, outPath, customKeywords: kw);
    }

    // --- Helpers -------------------------------------------------

    private MasterIndex LoadMasterIndex() {
        // Pull from the SQLite cache. Filter by type — anything tagged
        // MASTER{X} via the ImageWriterService or the ST-3 master writer
        // qualifies. Frames captured before STUDIO indexed them as
        // IMAGETYP=LIGHT — those won't show up here, which is correct.
        var all = _library.Query(new FrameQuery(null, null, null, null, null, 500, 0));
        var index = new MasterIndex();
        foreach (var f in all) {
            switch (f.ImageType?.ToUpperInvariant()) {
                case "MASTERBIAS":     index.Biases.Add(f); break;
                case "MASTERDARK":     index.Darks.Add(f); break;
                case "MASTERFLAT":     index.Flats.Add(f); break;
                case "MASTERDARKFLAT": index.DarkFlats.Add(f); break;
            }
        }
        return index;
    }

    private static int? FindNearestDark(IReadOnlyList<FrameRow> darks, double exposure, int gain) {
        if (darks.Count == 0) return null;
        // Require exact gain match — gain affects read noise pattern,
        // can't substitute across gains. Within that, pick the closest
        // exposure (typical pattern is one master dark per (gain, exposure)
        // anyway, so the "closest" usually matches exactly).
        FrameRow? best = null;
        double bestDelta = double.MaxValue;
        foreach (var d in darks) {
            if (d.Gain != gain) continue;
            var delta = Math.Abs(d.ExposureSec - exposure);
            if (delta < bestDelta) { bestDelta = delta; best = d; }
        }
        return best?.Id;
    }

    private static int? FindMatchingFlat(IReadOnlyList<FrameRow> flats, string filter, int gain) {
        if (flats.Count == 0) return null;
        // Flats are filter+gain specific (different filters have
        // different vignetting + dust patterns at different focus).
        var match = flats.FirstOrDefault(f =>
            f.Gain == gain &&
            string.Equals(f.Filter ?? "", filter ?? "", StringComparison.OrdinalIgnoreCase));
        return match?.Id;
    }

    private static int? FindMatchingBias(IReadOnlyList<FrameRow> biases, int gain) {
        if (biases.Count == 0) return null;
        var match = biases.FirstOrDefault(b => b.Gain == gain);
        return match?.Id;
    }

    /// <summary>Build the normalised flat: subtract a bias/dark-flat
    /// calibrator if available, divide by mean. Returns a per-pixel
    /// double[] (precision matters for the division) plus the mean
    /// for diagnostics.</summary>
    private static (double[] norm, double mean) NormalizeFlat(BaseImageData flat, BaseImageData? cal) {
        var n = flat.Data.Length;
        var corrected = new double[n];
        double sum = 0;
        if (cal != null && cal.Data.Length == n) {
            for (int i = 0; i < n; i++) {
                var v = (double)flat.Data[i] - cal.Data[i];
                if (v < 0) v = 0;
                corrected[i] = v;
                sum += v;
            }
        } else {
            for (int i = 0; i < n; i++) {
                corrected[i] = flat.Data[i];
                sum += flat.Data[i];
            }
        }
        var mean = sum / n;
        if (mean < 1) mean = 1;   // pathological flat; avoid divide-by-zero
        for (int i = 0; i < n; i++) corrected[i] /= mean;
        return (corrected, mean);
    }

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }

    private record MasterIndex {
        public List<FrameRow> Biases    { get; } = new();
        public List<FrameRow> Darks     { get; } = new();
        public List<FrameRow> Flats     { get; } = new();
        public List<FrameRow> DarkFlats { get; } = new();
    }
}

public record CalibrationProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public string Stage { get; init; } = "";
    public string? Error { get; init; }
}
