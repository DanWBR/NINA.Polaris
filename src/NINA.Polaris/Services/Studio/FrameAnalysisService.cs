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

using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Read-only optical diagnostics for a single FITS frame, powering the
/// STUDIO "Analyze" tool (Tilt + Aberration tabs). It detects stars once
/// (<see cref="StarDetector"/>), bins them into a 3x3 zone grid, and
/// derives:
///
/// - Tilt: an asymmetric HFR gradient across the frame (one corner pair
///   sharper than the opposite) -> sensor/optics not square to the light
///   cone. Reported as a relative corner-HFR spread %, not microns (we
///   don't know pixel size / backfocus geometry here).
/// - Aberration: star shape across the field. Elongation pointing
///   radially from the frame centre = coma; elongation that isn't radial
///   = astigmatism; round-but-soft edges with a sharp centre = field
///   curvature.
///
/// Nothing is written to disk. The result also carries a capped per-star
/// shape list so the UI can overlay ellipses/arrows on the preview.
///
/// Thresholds below are heuristic (tuned to match what CCDInspector /
/// ASTAP users expect) and live in one place so they're easy to revise.
/// </summary>
public class FrameAnalysisService {
    private readonly ILogger<FrameAnalysisService> _logger;

    public FrameAnalysisService(ILogger<FrameAnalysisService> logger) {
        _logger = logger;
    }

    // Min stars in a zone before its medians are trusted.
    private const int MinZoneStars = 4;
    // Relative corner-HFR spread bands (% of the frame median HFR).
    private const double TiltMildPct = 15.0;
    private const double TiltStrongPct = 35.0;
    // Aberration classification thresholds.
    private const double EccHigh = 0.45;       // elongated stars
    private const double RadialityHigh = 0.60; // elongation aligned with radius
    private const double CurvatureRatio = 1.35; // edge HFR / centre HFR

    public FrameAnalysis Analyze(string fitsPath) {
        if (string.IsNullOrWhiteSpace(fitsPath))
            throw new ArgumentException("path is required", nameof(fitsPath));
        if (!File.Exists(fitsPath))
            throw new FileNotFoundException("FITS not found", fitsPath);

        BaseImageData src;
        using (var fs = File.OpenRead(fitsPath)) {
            src = FITSReader.Read(fs);
        }

        int w = src.Properties.Width;
        int h = src.Properties.Height;
        int channels = src.Properties.Channels == 3 ? 3 : 1;
        var lum = ToLuminance(src.Data, w, h, channels);

        var detector = new StarDetector {
            MaxStars = 2000,
            // Soft corners (tilt / curvature) and bright stars in masters
            // can spread well past the live-tracking default of 200 px;
            // a generous cap keeps those stars in the sample instead of
            // silently dropping exactly the ones we want to measure.
            MaxStarSize = 1500,
            BorderExclusion = Math.Max(8, Math.Min(w, h) / 100)
        };
        var stars = detector.Detect(lum, w, h);

        double cxImg = w / 2.0, cyImg = h / 2.0;

        // 3x3 binning. Accumulate per-zone star lists.
        var zoneStars = new List<DetectedStar>[9];
        for (int i = 0; i < 9; i++) zoneStars[i] = new List<DetectedStar>();
        foreach (var s in stars) {
            int col = Clamp((int)(s.X * 3 / w), 0, 2);
            int row = Clamp((int)(s.Y * 3 / h), 0, 2);
            zoneStars[row * 3 + col].Add(s);
        }

        var zones = new ZoneStat[9];
        for (int r = 0; r < 3; r++) {
            for (int c = 0; c < 3; c++) {
                int zi = r * 3 + c;
                var list = zoneStars[zi];
                double medHfr = Median(list.Select(s => s.HFR));
                double meanEcc = list.Count > 0 ? list.Average(s => s.Eccentricity) : double.NaN;
                double radiality = WeightedRadiality(list, cxImg, cyImg);
                zones[zi] = new ZoneStat(
                    r, c,
                    (c + 0.5) * w / 3.0, (r + 0.5) * h / 3.0,
                    list.Count,
                    list.Count >= MinZoneStars ? medHfr : double.NaN,
                    list.Count >= MinZoneStars ? meanEcc : double.NaN,
                    list.Count >= MinZoneStars ? radiality : double.NaN);
            }
        }

        double frameMedHfr = Median(stars.Select(s => s.HFR));
        var tilt = ComputeTilt(zones, frameMedHfr);
        var aberr = ComputeAberration(zones);

        // Cap the per-star overlay payload. `stars` is flux-sorted desc.
        var shapes = stars.Take(400)
            .Select(s => new StarShape(
                Math.Round(s.X, 1), Math.Round(s.Y, 1),
                Math.Round(s.HFR, 2), Math.Round(s.Eccentricity, 3),
                Math.Round(s.OrientationRad, 4)))
            .ToList();

        _logger.LogInformation(
            "FrameAnalysis {Path}: {Stars} stars, medHFR={Hfr:0.00}, tilt={Tilt}, aberr={Ab}",
            fitsPath, stars.Count, frameMedHfr, tilt.Severity, aberr.DominantType);

        return new FrameAnalysis(
            w, h, stars.Count, Math.Round(frameMedHfr, 2),
            zones.Select(RoundZone).ToArray(), shapes, tilt, aberr);
    }

    // --- tilt -------------------------------------------------------

    private static TiltResult ComputeTilt(ZoneStat[] zones, double frameMedHfr) {
        // corners: TL=0, TR=2, BL=6, BR=8
        var names = new[] { "top-left", "top-right", "bottom-left", "bottom-right" };
        int[] idx = { 0, 2, 6, 8 };
        var hfrs = idx.Select(i => zones[i].MedianHfr).ToArray();
        var valid = Enumerable.Range(0, 4).Where(i => !double.IsNaN(hfrs[i])).ToList();
        if (valid.Count < 3 || frameMedHfr <= 0)
            return new TiltResult(0, "", "", "unknown",
                "Not enough stars in the corners to judge tilt. Use a frame with more stars.");

        int worst = valid.OrderByDescending(i => hfrs[i]).First();
        int best = valid.OrderBy(i => hfrs[i]).First();
        double spreadPct = (hfrs[worst] - hfrs[best]) / frameMedHfr * 100.0;

        string severity = spreadPct < TiltMildPct ? "good"
                        : spreadPct < TiltStrongPct ? "mild" : "strong";
        string verdict = severity switch {
            "good" => $"Corners are even (spread {spreadPct:0}%). No meaningful tilt.",
            "mild" => $"Slight tilt: {names[worst]} is the softest corner "
                      + $"(spread {spreadPct:0}%). Usually fine; tweak only if chasing perfection.",
            _ => $"Strong tilt: {names[worst]} corner is much softer than {names[best]} "
                 + $"(spread {spreadPct:0}%). Adjust the tilt adjuster toward {names[worst]}.",
        };
        return new TiltResult(Math.Round(spreadPct, 1), names[worst], names[best], severity, verdict);
    }

    // --- aberration -------------------------------------------------

    private static AberrationResult ComputeAberration(ZoneStat[] zones) {
        int[] cornerIdx = { 0, 2, 6, 8 };
        var corners = cornerIdx.Select(i => zones[i]).Where(z => !double.IsNaN(z.MedianHfr)).ToList();
        var centre = zones[4];
        if (corners.Count < 3 || double.IsNaN(centre.MedianHfr) || centre.MedianHfr <= 0)
            return new AberrationResult(0, 0, 0, "unknown", "unknown",
                "Not enough stars to analyse aberrations. Use a frame with more stars.");

        double edgeHfr = corners.Average(z => z.MedianHfr);
        double ratio = edgeHfr / centre.MedianHfr;
        double edgeEcc = corners.Average(z => z.MeanEcc);
        double edgeRad = corners.Where(z => !double.IsNaN(z.Radiality)).DefaultIfEmpty().Average(z => z?.Radiality ?? 0);

        string type, severity, verdict;
        if (edgeEcc >= EccHigh && edgeRad >= RadialityHigh) {
            type = "coma";
            severity = edgeEcc >= EccHigh + 0.2 ? "strong" : "mild";
            verdict = $"Coma: edge stars are stretched outward from the centre "
                      + $"(elongation {edgeEcc:0.00}). Check collimation / coma corrector spacing.";
        } else if (edgeEcc >= EccHigh) {
            type = "astigmatism";
            severity = edgeEcc >= EccHigh + 0.2 ? "strong" : "mild";
            verdict = $"Astigmatism: edge stars are elongated but not radially "
                      + $"(elongation {edgeEcc:0.00}). Often spacing / pinched optics / sensor tilt.";
        } else if (ratio >= CurvatureRatio) {
            type = "field-curvature";
            severity = ratio >= CurvatureRatio + 0.4 ? "strong" : "mild";
            verdict = $"Field curvature: stars stay round but get softer toward the edges "
                      + $"(edge HFR {ratio:0.0}x the centre). A flattener / correct spacing helps.";
        } else {
            type = "none";
            severity = "good";
            verdict = $"Stars are round and even across the field "
                      + $"(edge elongation {edgeEcc:0.00}, edge/centre HFR {ratio:0.0}x). Optics look clean.";
        }

        return new AberrationResult(
            Math.Round(ratio, 2), Math.Round(edgeEcc, 3), Math.Round(edgeRad, 3),
            type, severity, verdict);
    }

    // --- helpers ----------------------------------------------------

    /// <summary>Radial alignment of star elongation within a zone, flux-
    /// independent but eccentricity-weighted so round stars (whose angle
    /// is just noise) don't drown out the real signal. 1 = elongation
    /// points at/away from the frame centre (coma); 0 = tangential.</summary>
    private static double WeightedRadiality(List<DetectedStar> stars, double cx, double cy) {
        double num = 0, den = 0;
        foreach (var s in stars) {
            double toCentre = Math.Atan2(s.Y - cy, s.X - cx);
            double d = s.OrientationRad - toCentre;
            double align = Math.Abs(Math.Cos(2 * d)); // axis, not direction
            num += s.Eccentricity * align;
            den += s.Eccentricity;
        }
        return den > 0 ? num / den : double.NaN;
    }

    private static ushort[] ToLuminance(ushort[] data, int w, int h, int channels) {
        if (channels != 3) return data;
        int plane = w * h;
        var lum = new ushort[plane];
        for (int i = 0; i < plane; i++) {
            int v = (data[i] + data[plane + i] + data[2 * plane + i]) / 3;
            lum[i] = (ushort)v;
        }
        return lum;
    }

    private static double Median(IEnumerable<double> values) {
        var arr = values.Where(v => !double.IsNaN(v)).ToArray();
        if (arr.Length == 0) return double.NaN;
        Array.Sort(arr);
        int m = arr.Length / 2;
        return arr.Length % 2 == 1 ? arr[m] : (arr[m - 1] + arr[m]) / 2.0;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private static ZoneStat RoundZone(ZoneStat z) => z with {
        CenterX = Math.Round(z.CenterX, 1),
        CenterY = Math.Round(z.CenterY, 1),
        MedianHfr = double.IsNaN(z.MedianHfr) ? z.MedianHfr : Math.Round(z.MedianHfr, 2),
        MeanEcc = double.IsNaN(z.MeanEcc) ? z.MeanEcc : Math.Round(z.MeanEcc, 3),
        Radiality = double.IsNaN(z.Radiality) ? z.Radiality : Math.Round(z.Radiality, 3),
    };
}

// --- DTOs -----------------------------------------------------------

public record ZoneStat(
    int Row, int Col, double CenterX, double CenterY,
    int Count, double MedianHfr, double MeanEcc, double Radiality);

public record StarShape(double X, double Y, double Hfr, double Ecc, double AngleRad);

public record TiltResult(
    double SpreadPct, string WorstCorner, string BestCorner,
    string Severity, string Verdict);

public record AberrationResult(
    double CenterEdgeRatio, double EdgeEcc, double EdgeRadiality,
    string DominantType, string Severity, string Verdict);

public record FrameAnalysis(
    int Width, int Height, int StarCount, double MedianHfr,
    ZoneStat[] Zones, List<StarShape> Stars,
    TiltResult Tilt, AberrationResult Aberration);