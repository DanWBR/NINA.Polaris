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
using System.Globalization;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// SpectroPhotometric Color Calibration (SPCC) on a 3-channel RGB master.
/// The spectral sibling of <see cref="ColorCalibrationService"/>'s PCC mode:
/// it shares the plate-solve → star-detect → per-channel photometry → APASS
/// cone-search → match pipeline, but the gain fit integrates each matched
/// star's actual spectrum through the selected sensor+filter response
/// (see <see cref="SpccMath"/> and <see cref="SpccDatabase"/>) instead of a
/// fixed B-V slope.
///
/// The star spectrum comes from the chosen source: blackbody-from-B-V
/// (always available), the Pickles template library, or (planned) Gaia
/// per-star spectra. Writes a sibling <c>*_spcc.fits</c> next to the source
/// (same non-destructive convention as PCC), background-neutralised so the
/// output has a neutral sky too. Runs as a background job with progress.
/// </summary>
public class SpccService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ApassCatalog _catalog;
    private readonly SpccDatabase _db;
    private readonly ILogger<SpccService> _logger;
    private readonly ConcurrentDictionary<string, SpccProgress> _jobs = new();

    public SpccService(FrameLibraryService library, ProfileService profile,
                       ApassCatalog catalog, SpccDatabase db,
                       ILogger<SpccService> logger) {
        _library = library;
        _profile = profile;
        _catalog = catalog;
        _db = db;
        _logger = logger;
    }

    /// <param name="Source">"blackbody" | "pickles" | "gaia" | "auto"
    /// (auto = best installed). Unavailable sources degrade gracefully.</param>
    public record SpccRequest(
        string FramePath, string SensorId, string FilterSetId,
        string WhiteRefId, string Source = "auto");

    public string StartJob(SpccRequest req) {
        if (req == null || string.IsNullOrWhiteSpace(req.FramePath))
            throw new ArgumentException("framePath required.");
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new SpccProgress { JobId = jobId, InProgress = true, Stage = "queued" };
        _ = Task.Run(() => RunJob(jobId, req));
        return jobId;
    }

    public SpccProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    /// <summary>
    /// Read only the FITS header of a frame and suggest the SPCC sensor +
    /// filter set + OSC/mono type from it (camera model in INSTRUME, Bayer in
    /// BAYERPAT). Used by the modal to pre-select the dropdowns; the user can
    /// always override. Never throws for a missing/odd header — returns a
    /// mono/no-match suggestion instead.
    /// </summary>
    public SpccDatabase.SpccSuggestion Suggest(string framePath) {
        if (string.IsNullOrWhiteSpace(framePath) || !File.Exists(framePath))
            throw new ArgumentException($"Frame missing on disk: {framePath}");
        string camera = "", bayer = "", filter = "";
        try {
            using var fs = File.OpenRead(framePath);
            var headers = FITSReader.ReadHeadersOnly(fs);
            string H(string k) => headers.TryGetValue(k, out var c) ? c.Value?.Trim() ?? "" : "";
            camera = H("INSTRUME");
            bayer = H("BAYERPAT");
            filter = H("FILTER");
        } catch (Exception ex) {
            _logger.LogDebug(ex, "SPCC suggest: header read failed for {Path}", framePath);
        }
        return _db.Suggest(camera, bayer, filter);
    }

    private void RunJob(string jobId, SpccRequest req) {
        try {
            _jobs[jobId] = _jobs[jobId] with { Stage = "loading" };
            if (!File.Exists(req.FramePath))
                throw new InvalidOperationException($"Frame missing on disk: {req.FramePath}");
            BaseImageData img;
            using (var fs = File.OpenRead(req.FramePath)) img = FITSReader.Read(fs);
            if (img.Properties.Channels != 3)
                throw new InvalidOperationException(
                    $"SPCC requires a 3-channel RGB FITS (got {img.Properties.Channels}). " +
                    "Combine per-filter masters first.");
            int W = img.Properties.Width, H = img.Properties.Height;

            // Resolve the spectral source (auto → best installed).
            string source = string.IsNullOrWhiteSpace(req.Source) || req.Source == "auto"
                ? _db.BestSource : req.Source;

            _jobs[jobId] = _jobs[jobId] with { Stage = "computing", Source = source };
            var (gains, matched, wbSummary) = RunSpcc(img, W, H, req, source);

            // Background-neutralise too, so the calibrated master has a
            // neutral sky (same as PCC). Zero the background across channels
            // then apply the white-balance gains.
            var offsets = ColorCalibrationMath.ComputeBgOffsets(
                img.Data, W, H, "auto", null, zeroBackground: true);

            _jobs[jobId] = _jobs[jobId] with { Stage = "applying" };
            int planeSize = W * H;
            var output = new ushort[planeSize * 3];
            for (int c = 0; c < 3; c++) {
                int baseIdx = c * planeSize;
                double off = offsets[c], g = gains[c];
                for (int i = 0; i < planeSize; i++) {
                    double v = (img.Data[baseIdx + i] - off) * g;
                    output[baseIdx + i] = (ushort)Math.Clamp(v, 0, 65535);
                }
            }

            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };
            var outPath = WriteOutput(output, W, H, img.Properties.BitDepth,
                req, source, gains, offsets, matched, img);

            _logger.LogInformation(
                "SPCC {Job}: {Path} (source={Src}, sensor={Sensor}, filter={Filter}, " +
                "whiteRef={White}, {N} stars, gains R={Gr:F3} G={Gg:F3} B={Gb:F3})",
                jobId, outPath, source, req.SensorId, req.FilterSetId, req.WhiteRefId,
                matched, gains[0], gains[1], gains[2]);
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false, Stage = "done", OutputPath = outPath,
                GainR = gains[0], GainG = gains[1], GainB = gains[2],
                MatchedStars = matched, Source = source,
                WhiteBalance = wbSummary,
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "SPCC job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false, Stage = "error", Error = ex.Message };
        }
    }

    private (double[] gains, int matched, WhiteBalanceFit.Summary summary) RunSpcc(
            BaseImageData img, int W, int H, SpccRequest req, string source) {
        var wcs = img.Properties.Wcs
            ?? throw new InvalidOperationException(
                "SPCC: source FITS has no WCS (plate-solve) headers. Solve it first.");
        if (!_catalog.IsAvailable)
            throw new InvalidOperationException(
                "SPCC: APASS catalog is not installed. Download it from the color calibration panel (~80 MB).");

        // Channel responses + white reference from the selected gear.
        var (respR, respG, respB) = _db.BuildResponses(req.SensorId, req.FilterSetId);
        var whiteRef = _db.BuildWhiteRef(req.WhiteRefId);

        // Detect + measure on the green channel (best SNR), like PCC.
        int n = W * H;
        var greenPlane = new ushort[n];
        Array.Copy(img.Data, n, greenPlane, 0, n);
        var detector = new StarDetector { SigmaThreshold = 7.0, MaxStarSize = 80 };
        var stars = detector.Detect(greenPlane, W, H);
        if (stars.Count < 5)
            throw new InvalidOperationException(
                $"SPCC: only {stars.Count} stars detected; needs at least 5.");
        var phots = StarPhotometer.MeasureRgb(img.Data, W, H, stars);

        // Cone search (half-diagonal FOV + 20% pad; rotation-invariant scale).
        double xScale = Math.Sqrt(wcs.CD11 * wcs.CD11 + wcs.CD21 * wcs.CD21);
        double yScale = Math.Sqrt(wcs.CD12 * wcs.CD12 + wcs.CD22 * wcs.CD22);
        double fovH = xScale * W, fovV = yScale * H;
        double radius = 1.2 * Math.Sqrt(fovV * fovV + fovH * fovH) / 2.0;
        // magLimit 16 is a ceiling: the query returns whatever the local
        // catalog actually holds (the bundled APASS is capped at ~V=13, but a
        // deeper re-download is used transparently if present). Broader than
        // the old 13 so sparse fields still find calibration stars.
        var catalogStars = _catalog
            .QueryRegionAsync(wcs.RaDeg, wcs.DecDeg, radius, magLimit: 16.0)
            .GetAwaiter().GetResult();

        // Match catalog→detected. The radius is loosened from 3px so a
        // slightly imperfect plate-solve doesn't drop otherwise-good stars.
        const double matchRadiusPx = 5.0;
        var grid = new SpatialGrid<StarPhotometer.StarPhotometry>(matchRadiusPx);
        foreach (var p in phots) if (!p.Saturated) grid.Add(p.X, p.Y, p);

        // An external plate-solve (or a ROWORDER=TOP-DOWN frame) can store the
        // WCS with a different pixel-axis convention (vertical and/or
        // horizontal flip, or a 180° rotation) than how Polaris loads the
        // pixels, which misaligns every catalog star except near the centre.
        // Try all four axis orientations and keep whichever aligns most; a
        // self-consistent (Polaris-native) frame wins un-flipped, so this is a
        // no-op there.
        List<(double Bv, StarPhotometer.StarPhotometry Phot)> MatchAll(bool flipX, bool flipY) {
            var m = new List<(double, StarPhotometer.StarPhotometry)>();
            foreach (var c in catalogStars) {
                if (c.Bv == null) continue;
                var (px, py) = wcs.RaDecToPixel(c.Ra, c.Dec);
                if (double.IsNaN(px) || double.IsNaN(py)) continue;
                double qx = flipX ? (W + 1 - px) : px;
                double qy = flipY ? (H + 1 - py) : py;
                if (grid.TryNearest(qx, qy, matchRadiusPx, out var best, out _))
                    m.Add((c.Bv.Value, best));
            }
            return m;
        }
        var matched = MatchAll(false, false);
        string flipDesc = "none";
        foreach (var (fx, fy, name) in new[] { (true, false, "X"), (false, true, "Y"), (true, true, "XY") }) {
            var m = MatchAll(fx, fy);
            if (m.Count > matched.Count) { matched = m; flipDesc = name; }
        }

        var spccStars = new List<SpccMath.SpccStar>(matched.Count);
        foreach (var (bv, phot) in matched) {
            var spectrum = _db.StarSpectrumFromBv(source, bv);
            spccStars.Add(new SpccMath.SpccStar(phot.FluxR, phot.FluxG, phot.FluxB, spectrum));
        }
        if (flipDesc != "none")
            _logger.LogInformation("SPCC: matched with a {Flip} pixel flip " +
                "({N} stars) — external plate-solve / ROWORDER convention.",
                flipDesc, matched.Count);
        if (spccStars.Count < 5) {
            int withBv = catalogStars.Count(c => c.Bv != null);
            string diag =
                $"Detected {stars.Count} stars in the image; {catalogStars.Count} " +
                $"APASS stars in the field ({withBv} with a usable B-V colour); " +
                $"only {spccStars.Count} aligned within {matchRadiusPx:0}px.";
            string hint = withBv < 5
                ? "The catalog is sparse in this field. Try a wider field " +
                  "(the installed APASS catalog is capped at about V=13)."
                : "There are enough catalog stars but few line up with detected " +
                  "stars, so the plate-solve (WCS) is likely inaccurate. Re-solve " +
                  "the master (STUDIO -> Solve) and try again.";
            throw new InvalidOperationException(
                $"SPCC: only {spccStars.Count} catalog matches; needs at least 5. " +
                diag + " " + hint);
        }

        var gains = SpccMath.Solve(spccStars, whiteRef, respR, respG, respB);

        // White-balance summary (measured vs expected channel ratios + fit),
        // for the Siril/PixInsight-style plot the UI shows after the run.
        var (cBg, iBg, cRg, iRg) = SpccMath.ChannelRatios(spccStars, respR, respG, respB);
        var summary = WhiteBalanceFit.Build(cBg, iBg, cRg, iRg,
            gains[0], gains[1], gains[2], req.WhiteRefId ?? "", "SPCC", spccStars.Count);
        return (gains, spccStars.Count, summary);
    }

    private string WriteOutput(ushort[] data, int W, int H, int bitDepth,
            SpccRequest req, string source, double[] gains, double[] offsets,
            int matched, BaseImageData src) {
        var dir = Path.GetDirectoryName(req.FramePath);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var stem = Path.GetFileNameWithoutExtension(req.FramePath);
        var outBase = Path.Combine(dir, stem + "_spcc");
        var outPath = outBase + ".fits";
        int copy = 1;
        while (File.Exists(outPath)) outPath = outBase + "_" + (++copy) + ".fits";

        var props = new ImageProperties {
            Width = W, Height = H, BitDepth = bitDepth, Channels = 3,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None, IsBayered = false,
            Wcs = src.Properties.Wcs,
        };
        var target = string.IsNullOrEmpty(src.MetaData.Target.Name)
            ? "Unknown" : src.MetaData.Target.Name;
        var meta = new ImageMetaData {
            CreationTime = DateTime.UtcNow,
            Camera = new ImageMetaData.CameraInfo(),
            Telescope = new ImageMetaData.TelescopeInfo(),
            Observer = new ImageMetaData.ObserverInfo(),
            Target = new ImageMetaData.TargetInfo { Name = target },
            Exposure = new ImageMetaData.ExposureInfo { Filter = "RGB", ImageType = "MASTERCAL" },
        };
        var kw = new List<KeyValuePair<string, string>> {
            new("CCAL_MOD", "spcc"),
            new("SPCC_SRC", source),
            new("SPCC_SEN", req.SensorId),
            new("SPCC_FIL", req.FilterSetId),
            new("SPCC_WHT", req.WhiteRefId),
            new("SPCC_NST", matched.ToString(CultureInfo.InvariantCulture)),
            new("CCAL_GNR", Fmt(gains[0])),
            new("CCAL_GNG", Fmt(gains[1])),
            new("CCAL_GNB", Fmt(gains[2])),
            new("CCAL_OFR", Fmt(offsets[0])),
            new("CCAL_OFG", Fmt(offsets[1])),
            new("CCAL_OFB", Fmt(offsets[2])),
            new("CCAL_SRC", Path.GetFileName(req.FramePath)),
        };
        FITSWriter.Write(new BaseImageData(data, props, meta), outPath, customKeywords: kw);
        return outPath;
    }

    private static string Fmt(double v)
        => v.ToString("0.####", CultureInfo.InvariantCulture);
}

public record SpccProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public string Stage { get; init; } = "";
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
    public string? Source { get; init; }
    public int MatchedStars { get; init; }
    public double GainR { get; init; }
    public double GainG { get; init; }
    public double GainB { get; init; }
    public WhiteBalanceFit.Summary? WhiteBalance { get; init; }
}
