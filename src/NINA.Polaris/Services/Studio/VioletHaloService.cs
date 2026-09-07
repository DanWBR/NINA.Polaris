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
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Removes the out-of-focus violet pedestal an ED doublet leaves around bright
/// stars: a broad, faint, blue-only skirt sitting under an otherwise normal
/// star.
///
/// This is a TELESCOPE defect, not a camera one, which is why it is a tool of
/// its own and not a stage of the star colour repair. The two were tried
/// together on a real SV503 stack and the combination turned every bright star
/// yellow: the fringe repair rebuilds a star's colour from its own ring
/// medians, so running it before this one leaves this one a different star to
/// measure. Keep them separate, and run either on the original frame.
///
/// Measured on an SV503 + ASI585MC stack: the three channels share the same
/// core (B/G = 1.00 out to 4 px) while blue carries 3 to 5 times the energy of
/// the other two from 6 px outwards. Red and green track each other through the
/// whole halo; blue alone stands above them. That is the pedestal, and how far
/// blue stands above the PAIR is its size.
///
/// Operates on plane-sequential RGB FITS and writes a sibling
/// <c>{stem}_violethalo_{stamp}.fits</c>.
/// </summary>
public sealed class VioletHaloService {
    private readonly FrameLibraryService _library;
    private readonly ILogger<VioletHaloService> _logger;
    private readonly ConcurrentDictionary<string, VioletHaloProgress> _jobs = new();

    public VioletHaloService(FrameLibraryService library, ILogger<VioletHaloService> logger) {
        _library = library;
        _logger = logger;
    }

    public record VioletHaloRequest(
        string FramePath,
        double Amount = 1.0,      // 0..1
        double Radius = 40.0);    // px; how far out the pedestal reaches

    public string StartJob(VioletHaloRequest req) {
        if (req == null) throw new ArgumentNullException(nameof(req));
        if (string.IsNullOrWhiteSpace(req.FramePath) || !File.Exists(req.FramePath))
            throw new ArgumentException($"Frame not found: {req.FramePath}");
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new VioletHaloProgress { JobId = jobId, InProgress = true, Stage = "queued" };
        _ = Task.Run(() => RunJob(jobId, req));
        return jobId;
    }

    public VioletHaloProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, VioletHaloRequest req) {
        try {
            double amount = Math.Clamp(req.Amount, 0.0, 1.0);
            double radius = Math.Clamp(req.Radius, 12.0, 120.0);

            _jobs[jobId] = _jobs[jobId] with { Stage = "loading" };
            BaseImageData img;
            using (var fs = File.OpenRead(req.FramePath)) img = FITSReader.Read(fs);
            int W = img.Properties.Width, H = img.Properties.Height, plane = W * H;
            if (img.Properties.Channels != 3)
                throw new InvalidOperationException(
                    "Violet halo removal needs a 3-channel colour (OSC) frame; this is " +
                    $"{img.Properties.Channels}-channel.");

            var src = img.Data;
            var R = new double[plane]; var G = new double[plane]; var B = new double[plane];
            for (int i = 0; i < plane; i++) { R[i] = src[i]; G[i] = src[plane + i]; B[i] = src[2 * plane + i]; }

            _jobs[jobId] = _jobs[jobId] with { Stage = "detecting" };
            var stars = StarColorRepairService.DetectBrightStars(R, G, B, W, H);

            var montStars = StarColorRepairService.PickMontageStars(stars, W, H);
            var beforeCrops = montStars
                .Select(s => StarColorRepairService.ExtractCropRgb(R, G, B, W, s.x, s.y)).ToList();

            _jobs[jobId] = _jobs[jobId] with { Stage = "removing halo" };
            int fixedCount = RemoveVioletHalo(R, G, B, W, H, stars, amount, radius);

            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };
            var outData = new ushort[plane * 3];
            for (int i = 0; i < plane; i++) {
                outData[i]             = StarColorRepairService.Clamp16(R[i]);
                outData[plane + i]     = StarColorRepairService.Clamp16(G[i]);
                outData[2 * plane + i] = StarColorRepairService.Clamp16(B[i]);
            }
            var outImg = new BaseImageData(outData, img.Properties, img.MetaData);
            var outPath = StarColorRepairService.SiblingPath(req.FramePath, "violethalo");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            FITSWriter.Write(outImg, outPath, customKeywords: new List<KeyValuePair<string, string>> {
                new("VIOLETFX", "T"),
                new("VIOLETAM", amount.ToString("F2", inv)),
                new("VIOLETRD", radius.ToString("F0", inv)),
            });

            string? beforePath = null, afterPath = null;
            if (montStars.Count > 0) {
                var afterCrops = montStars
                    .Select(s => StarColorRepairService.ExtractCropRgb(R, G, B, W, s.x, s.y)).ToList();
                beforePath = StarColorRepairService.WriteMontage(beforeCrops,
                    StarColorRepairService.SiblingPath(req.FramePath, "violethalo_stars_before"), img);
                afterPath = StarColorRepairService.WriteMontage(afterCrops,
                    StarColorRepairService.SiblingPath(req.FramePath, "violethalo_stars_after"), img);
            }

            _logger.LogInformation("Violet halo {Job}: {N} stars ({Fixed} corrected), amount={Amount}, wrote {Path}",
                jobId, stars.Count, fixedCount, amount, outPath);
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false, Stage = "done", StarCount = stars.Count, CorrectedCount = fixedCount,
                OutputPath = outPath, StarsBeforePath = beforePath, StarsAfterPath = afterPath,
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Violet halo job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with { InProgress = false, Stage = "error", Error = ex.Message };
        }
    }

    /// <summary>Remove the violet pedestal. Returns how many stars were touched.
    ///
    /// Four things make this work where the earlier per-pixel attempts did not:
    ///
    ///   * the target is the mean of the OTHER TWO channels, not the star's own
    ///     core colour. Keying on the core fails on a yellowish star, whose core
    ///     sits at B/G = 0.66: asking the halo to match it drives blue a third
    ///     below green and paints a green ring exactly where the violet one was.
    ///   * the SIZE of the correction is decided on a 1-D radial profile, one
    ///     ring average per pixel of radius. Out at 15 px the signal is a
    ///     hundredth of the peak and the display midtone sits near 0.002, so a
    ///     per-pixel estimate turns noise into a coloured ring. A ring holds
    ///     hundreds of pixels and is steady.
    ///   * the CEILING is decided per pixel: never take a pixel's blue below the
    ///     mean of its own red and green. Without this the ring constant is
    ///     subtracted from pixels that vary a lot within the ring, the faint
    ///     side goes negative, clips at zero, and the star grows a bright YELLOW
    ///     lobe. That is exactly what the first field run produced on the
    ///     saturated stars of an SV503 stack, and what this line prevents. Both
    ///     halves matter: the ring says how much pedestal there is, the pixel
    ///     says how much of it is actually here.
    ///   * sky comes from an annulus OUTSIDE the halo, per channel. The median
    ///     of the whole window is contaminated by the pedestal itself, which is
    ///     blue, so blue's zero point comes out too high.
    ///
    /// The core is untouched by construction: there blue already sits below the
    /// pair, so the excess is zero.</summary>
    internal static int RemoveVioletHalo(double[] R, double[] G, double[] B,
                                         int W, int H, List<(int x, int y)> stars,
                                         double amount, double rOut) {
        double rIn = Math.Max(4.0, rOut * 0.125);        // fade in
        double rFull = Math.Max(rIn + 2.0, rOut * 0.20); // full strength
        double rTaper = rOut * 0.80;                     // fade out
        double rSky0 = rOut * 1.15, rSky1 = rOut * 1.45;
        int s = (int)Math.Ceiling(rSky1);
        int nRing = (int)Math.Ceiling(rOut);

        var sumR = new double[nRing]; var sumG = new double[nRing];
        var sumB = new double[nRing]; var cnt = new int[nRing];
        var exR = new double[nRing]; var exB = new double[nRing];
        var skyR = new List<double>(); var skyG = new List<double>(); var skyB = new List<double>();
        int touched = 0;

        foreach (var (cx, cy) in stars) {
            if (cx - s < 0 || cy - s < 0 || cx + s >= W || cy + s >= H) continue;

            Array.Clear(sumR); Array.Clear(sumG); Array.Clear(sumB); Array.Clear(cnt);
            skyR.Clear(); skyG.Clear(); skyB.Clear();

            for (int dy = -s; dy <= s; dy++) {
                int row = (cy + dy) * W + cx;
                for (int dx = -s; dx <= s; dx++) {
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    int i = row + dx;
                    if (d >= rSky0 && d <= rSky1) {
                        skyR.Add(R[i]); skyG.Add(G[i]); skyB.Add(B[i]);
                    } else if (d < rOut) {
                        int k = (int)d;
                        if (k < nRing) { sumR[k] += R[i]; sumG[k] += G[i]; sumB[k] += B[i]; cnt[k]++; }
                    }
                }
            }
            if (skyG.Count < 32) continue;
            double kR = StarColorRepairService.Median(skyR);
            double kG = StarColorRepairService.Median(skyG);
            double kB = StarColorRepairService.Median(skyB);

            bool any = false;
            for (int k = 0; k < nRing; k++) {
                exR[k] = 0; exB[k] = 0;
                if (cnt[k] == 0) continue;
                double pr = sumR[k] / cnt[k] - kR;
                double pg = sumG[k] / cnt[k] - kG;
                double pb = sumB[k] / cnt[k] - kB;
                double pair = 0.5 * (Math.Max(pr, 0) + Math.Max(pg, 0));
                double w = RadialWeight(k + 0.5, rIn, rFull, rTaper, rOut) * amount;
                if (w <= 0) continue;
                exB[k] = Math.Clamp(pb - pair, 0, Math.Max(pb, 0)) * w;
                double pbAfter = pb - exB[k];
                double pairR = 0.5 * (Math.Max(pbAfter, 0) + Math.Max(pg, 0));
                exR[k] = Math.Clamp(pr - pairR, 0, Math.Max(pr, 0)) * w;
                if (exB[k] > 0 || exR[k] > 0) any = true;
            }
            if (!any) continue;

            for (int dy = -s; dy <= s; dy++) {
                int row = (cy + dy) * W + cx;
                for (int dx = -s; dx <= s; dx++) {
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d >= rOut) continue;
                    int k = (int)d;
                    if (k >= nRing || (exR[k] <= 0 && exB[k] <= 0)) continue;
                    int i = row + dx;
                    // The ring says how much pedestal there is; this pixel says
                    // how much of it is here. Read all three before writing any,
                    // so red and blue see the same starting point.
                    double lr = Math.Max(R[i] - kR, 0);
                    double lg = Math.Max(G[i] - kG, 0);
                    double lb = Math.Max(B[i] - kB, 0);
                    if (exB[k] > 0) B[i] -= Math.Min(exB[k], Math.Max(lb - 0.5 * (lr + lg), 0));
                    if (exR[k] > 0) R[i] -= Math.Min(exR[k], Math.Max(lr - 0.5 * (lb + lg), 0));
                }
            }
            touched++;
        }
        return touched;
    }

    /// <summary>0 inside the core, 1 across the pedestal, 0 again past its
    /// edge, so the correction never leaves a step.</summary>
    private static double RadialWeight(double r, double rIn, double rFull,
                                       double rTaper, double rOut) {
        if (r <= rIn || r >= rOut) return 0;
        double up = r >= rFull ? 1.0 : (r - rIn) / Math.Max(1e-9, rFull - rIn);
        double down = r <= rTaper ? 1.0 : (rOut - r) / Math.Max(1e-9, rOut - rTaper);
        return Math.Max(0, Math.Min(up, down));
    }
}

public sealed record VioletHaloProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public string Stage { get; init; } = "";
    public int StarCount { get; init; }
    public int CorrectedCount { get; init; }
    public string? OutputPath { get; init; }
    public string? StarsBeforePath { get; init; }
    public string? StarsAfterPath { get; init; }
    public string? Error { get; init; }
}
