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
/// Star colour/fringe repair for OSC frames — fixes the blue/magenta + dark
/// one-sided fringe on bright stars that SVBony (and similar) cameras leave from
/// their debayer/CFA, which channel alignment alone can't remove. Meant to run
/// FIRST on an SVBony stack, before background extraction.
///
/// Two stages (both gated by the aggressiveness 0..1):
///   1. CHANNEL ALIGN — measure the per-channel sub-pixel centroid offset over
///      many bright stars (median R-vs-G and B-vs-G) and shift R/B onto G. Fixes
///      the field-wide lateral colour offset (atmospheric dispersion + CFA).
///   2. RADIAL STAR SYMMETRY — a star is radially symmetric, the fringe is on ONE
///      side. Per bright star, per radius ring, take the MEDIAN colour (the clean
///      sides dominate) and rebuild each pixel's colour from it; FILL the dark
///      side up to the per-ring median luminance (never lower the core, never
///      touch companion stars). The bad edge inherits the other edges' colour and
///      brightness. Pure math, no model. (Ported from polaris-ai/fix_*.py, proven
///      on a real SV605CC stack.)
///
/// Operates on plane-sequential RGB FITS (ushort[] = R plane, G plane, B plane)
/// and writes a sibling <c>{stem}_starcolor_{stamp}.fits</c>.
/// </summary>
public sealed class StarColorRepairService {
    private readonly FrameLibraryService _library;
    private readonly ILogger<StarColorRepairService> _logger;
    private readonly ConcurrentDictionary<string, StarColorRepairProgress> _jobs = new();

    public StarColorRepairService(FrameLibraryService library,
                                  ILogger<StarColorRepairService> logger) {
        _library = library;
        _logger = logger;
    }

    public record StarColorRepairRequest(
        string FramePath,
        double Aggressiveness = 1.0,   // 0..1
        bool Align = true,
        bool Fringe = true);

    public string StartJob(StarColorRepairRequest req) {
        if (req == null) throw new ArgumentNullException(nameof(req));
        if (string.IsNullOrWhiteSpace(req.FramePath) || !File.Exists(req.FramePath))
            throw new ArgumentException($"Frame not found: {req.FramePath}");
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new StarColorRepairProgress { JobId = jobId, InProgress = true, Stage = "queued" };
        _ = Task.Run(() => RunJob(jobId, req));
        return jobId;
    }

    public StarColorRepairProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, StarColorRepairRequest req) {
        try {
            double agg = Math.Clamp(req.Aggressiveness, 0.0, 1.0);

            _jobs[jobId] = _jobs[jobId] with { Stage = "loading" };
            BaseImageData img;
            using (var fs = File.OpenRead(req.FramePath)) img = FITSReader.Read(fs);
            int W = img.Properties.Width, H = img.Properties.Height, plane = W * H;
            if (img.Properties.Channels != 3)
                throw new InvalidOperationException(
                    "Star colour repair needs a 3-channel colour (OSC) frame; this is " +
                    $"{img.Properties.Channels}-channel.");

            // plane-sequential ushort -> double planes
            var src = img.Data;
            var R = new double[plane]; var G = new double[plane]; var B = new double[plane];
            for (int i = 0; i < plane; i++) { R[i] = src[i]; G[i] = src[plane + i]; B[i] = src[2 * plane + i]; }

            _jobs[jobId] = _jobs[jobId] with { Stage = "detecting" };
            var stars = DetectBrightStars(R, G, B, W, H);

            if (req.Align && agg > 0) {
                _jobs[jobId] = _jobs[jobId] with { Stage = "aligning" };
                AlignChannels(R, G, B, W, H, stars, agg);
            }
            if (req.Fringe && agg > 0) {
                _jobs[jobId] = _jobs[jobId] with { Stage = "repairing" };
                foreach (var (sx, sy) in stars) RepairStar(R, G, B, W, H, sx, sy, 22, agg);
            }

            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };
            var outData = new ushort[plane * 3];
            for (int i = 0; i < plane; i++) {
                outData[i]             = Clamp16(R[i]);
                outData[plane + i]     = Clamp16(G[i]);
                outData[2 * plane + i] = Clamp16(B[i]);
            }
            var outImg = new BaseImageData(outData, img.Properties, img.MetaData);
            var outPath = SiblingPath(req.FramePath, "starcolor");
            FITSWriter.Write(outImg, outPath, customKeywords: new List<KeyValuePair<string, string>> {
                new("STARFIX", "T"),
                new("SFAGG", agg.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)),
            });

            _logger.LogInformation("Star colour repair {Job}: {N} stars, agg={Agg}, wrote {Path}",
                jobId, stars.Count, agg, outPath);
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false, Stage = "done", StarCount = stars.Count, OutputPath = outPath,
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Star colour repair job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with { InProgress = false, Stage = "error", Error = ex.Message };
        }
    }

    // ── bright-star peak detection (self-contained; no external detector) ─────
    private static List<(int x, int y)> DetectBrightStars(
            double[] R, double[] G, double[] B, int W, int H,
            double thrFrac = 0.15, int sep = 24, int nmax = 4000) {
        int plane = W * H;
        var lum = new double[plane];
        double max = 0;
        for (int i = 0; i < plane; i++) { double l = (R[i] + G[i] + B[i]) / 3.0; lum[i] = l; if (l > max) max = l; }
        double thr = thrFrac * max;
        // collect local maxima (3x3) above threshold
        var cand = new List<(double v, int x, int y)>();
        const int M = 16;
        for (int y = M; y < H - M; y++) {
            int row = y * W;
            for (int x = M; x < W - M; x++) {
                double v = lum[row + x];
                if (v <= thr) continue;
                if (v < lum[row + x - 1] || v < lum[row + x + 1] ||
                    v < lum[row - W + x] || v < lum[row + W + x] ||
                    v < lum[row - W + x - 1] || v < lum[row - W + x + 1] ||
                    v < lum[row + W + x - 1] || v < lum[row + W + x + 1]) continue;
                cand.Add((v, x, y));
            }
        }
        cand.Sort((a, b) => b.v.CompareTo(a.v));
        var outList = new List<(int x, int y)>();
        var used = new List<(int x, int y)>();
        foreach (var c in cand) {
            bool near = false;
            foreach (var u in used)
                if (Math.Abs(c.y - u.y) < sep && Math.Abs(c.x - u.x) < sep) { near = true; break; }
            if (near) continue;
            used.Add((c.x, c.y)); outList.Add((c.x, c.y));
            if (outList.Count >= nmax) break;
        }
        return outList;
    }

    // ── stage 1: per-channel lateral alignment ────────────────────────────────
    private static void AlignChannels(double[] R, double[] G, double[] B, int W, int H,
                                      List<(int x, int y)> stars, double agg) {
        const int win = 12;
        var dRx = new List<double>(); var dRy = new List<double>();
        var dBx = new List<double>(); var dBy = new List<double>();
        foreach (var (x, y) in stars) {
            if (x < win || y < win || x >= W - win || y >= H - win) continue;
            var (gx, gy) = Centroid(G, W, x, y, win);
            var (rx, ry) = Centroid(R, W, x, y, win);
            var (bx, by) = Centroid(B, W, x, y, win);
            dRx.Add(rx - gx); dRy.Add(ry - gy);
            dBx.Add(bx - gx); dBy.Add(by - gy);
        }
        if (dRx.Count < 3) return;   // not enough stars to trust an offset
        double oRx = Median(dRx) * agg, oRy = Median(dRy) * agg;
        double oBx = Median(dBx) * agg, oBy = Median(dBy) * agg;
        // shift R/B content by the negative of their offset to land on G
        ShiftPlaneInPlace(R, W, H, -oRx, -oRy);
        ShiftPlaneInPlace(B, W, H, -oBx, -oBy);
    }

    /// <summary>Intensity-weighted centroid offset (px) within a window.</summary>
    private static (double dx, double dy) Centroid(double[] p, int W, int cx, int cy, int win) {
        // local background = window minimum (cheap, robust enough here)
        double bg = double.MaxValue;
        for (int yy = -win; yy <= win; yy++) {
            int row = (cy + yy) * W + cx;
            for (int xx = -win; xx <= win; xx++) { double v = p[row + xx]; if (v < bg) bg = v; }
        }
        double sw = 0, sxv = 0, syv = 0;
        for (int yy = -win; yy <= win; yy++) {
            int row = (cy + yy) * W + cx;
            for (int xx = -win; xx <= win; xx++) {
                double w = p[row + xx] - bg; if (w <= 0) continue;
                sw += w; sxv += w * xx; syv += w * yy;
            }
        }
        return sw <= 0 ? (0, 0) : (sxv / sw, syv / sw);
    }

    /// <summary>Bilinear shift of a whole plane by (sx,sy) (content moves by sx,sy).</summary>
    private static void ShiftPlaneInPlace(double[] p, int W, int H, double sx, double sy) {
        if (Math.Abs(sx) < 1e-3 && Math.Abs(sy) < 1e-3) return;
        var dst = new double[p.Length];
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                double srcX = x - sx, srcY = y - sy;
                int x0 = (int)Math.Floor(srcX), y0 = (int)Math.Floor(srcY);
                double fx = srcX - x0, fy = srcY - y0;
                int x1 = x0 + 1, y1 = y0 + 1;
                x0 = Math.Clamp(x0, 0, W - 1); x1 = Math.Clamp(x1, 0, W - 1);
                y0 = Math.Clamp(y0, 0, H - 1); y1 = Math.Clamp(y1, 0, H - 1);
                double v00 = p[y0 * W + x0], v01 = p[y0 * W + x1];
                double v10 = p[y1 * W + x0], v11 = p[y1 * W + x1];
                double top = v00 + (v01 - v00) * fx, bot = v10 + (v11 - v10) * fx;
                dst[y * W + x] = top + (bot - top) * fy;
            }
        }
        Array.Copy(dst, p, p.Length);
    }

    // ── stage 2: radial colour + luminance symmetry per star ──────────────────
    private static void RepairStar(double[] R, double[] G, double[] B, int W, int H,
                                   int cx, int cy, int win, double agg) {
        if (cx < win || cy < win || cx >= W - win || cy >= H - win) return;
        int n = 2 * win + 1;
        // gather window
        var lr = new double[n * n]; var lg = new double[n * n]; var lb = new double[n * n];
        var L = new double[n * n];
        for (int yy = 0; yy < n; yy++) {
            int row = (cy - win + yy) * W + (cx - win);
            for (int xx = 0; xx < n; xx++) {
                int k = yy * n + xx, src = row + xx;
                double r = R[src], g = G[src], b = B[src];
                lr[k] = r; lg[k] = g; lb[k] = b; L[k] = (r + g + b) / 3.0 + 1e-6;
            }
        }
        // sub-pixel centre via luminance centroid (background = window median)
        double bg = Median(L);
        double sw = 0, scx = 0, scy = 0;
        for (int yy = 0; yy < n; yy++)
            for (int xx = 0; xx < n; xx++) {
                double w = L[yy * n + xx] - bg; if (w <= 0) continue;
                sw += w; scx += w * (xx - win); scy += w * (yy - win);
            }
        double ccx = sw > 0 ? scx / sw : 0, ccy = sw > 0 ? scy / sw : 0;

        // radius per pixel + ring stats
        var ri = new int[n * n]; int rmax = 0;
        for (int yy = 0; yy < n; yy++)
            for (int xx = 0; xx < n; xx++) {
                double dx = (xx - win) - ccx, dy = (yy - win) - ccy;
                int r = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy));
                ri[yy * n + xx] = r; if (r > rmax) rmax = r;
            }
        double[] medL = RingMedian(L, ri, rmax);
        // ratio channel/L per ring
        var ratR = new double[n * n]; var ratG = new double[n * n]; var ratB = new double[n * n];
        for (int k = 0; k < n * n; k++) { ratR[k] = lr[k] / L[k]; ratG[k] = lg[k] / L[k]; ratB[k] = lb[k] / L[k]; }
        double[] medRR = RingMedian(ratR, ri, rmax);
        double[] medRG = RingMedian(ratG, ri, rmax);
        double[] medRB = RingMedian(ratB, ri, rmax);

        // rebuild + write back with feather (scaled by aggressiveness)
        for (int yy = 0; yy < n; yy++) {
            for (int xx = 0; xx < n; xx++) {
                int k = yy * n + xx, r = ri[k];
                double lm = medL[r];
                bool companion = L[k] > lm * 1.8 + 0.02 * 65535.0;   // protect neighbour stars
                double rr, gg, bb;
                if (companion) { rr = lr[k]; gg = lg[k]; bb = lb[k]; }
                else {
                    double lout = Math.Max(L[k], lm);                // fill dark side
                    rr = lout * medRR[r]; gg = lout * medRG[r]; bb = lout * medRB[r];
                }
                double dx = (xx - win) - ccx, dy = (yy - win) - ccy;
                double rad = Math.Sqrt(dx * dx + dy * dy);
                double w = Math.Clamp((win * 0.85 - rad) / (win * 0.2), 0, 1) * agg;
                int src = (cy - win + yy) * W + (cx - win + xx);
                R[src] = lr[k] * (1 - w) + rr * w;
                G[src] = lg[k] * (1 - w) + gg * w;
                B[src] = lb[k] * (1 - w) + bb * w;
            }
        }
    }

    private static double[] RingMedian(double[] vals, int[] ri, int rmax) {
        var med = new double[rmax + 1];
        var buckets = new List<double>[rmax + 1];
        for (int r = 0; r <= rmax; r++) buckets[r] = new List<double>();
        for (int k = 0; k < vals.Length; k++) buckets[ri[k]].Add(vals[k]);
        for (int r = 0; r <= rmax; r++) med[r] = buckets[r].Count > 0 ? Median(buckets[r]) : 0;
        return med;
    }

    private static double Median(IReadOnlyList<double> v) {
        if (v.Count == 0) return 0;
        var a = v.ToArray(); Array.Sort(a);
        int m = a.Length / 2;
        return (a.Length & 1) == 1 ? a[m] : 0.5 * (a[m - 1] + a[m]);
    }

    private static ushort Clamp16(double v)
        => (ushort)Math.Clamp(Math.Round(v), 0, 65535);

    private static string SiblingPath(string srcPath, string suffix) {
        var dir = Path.GetDirectoryName(srcPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(srcPath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(dir, $"{stem}_{suffix}_{stamp}.fits");
        int copy = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, $"{stem}_{suffix}_{stamp}_{copy++}.fits");
        return path;
    }
}

public sealed record StarColorRepairProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public string Stage { get; init; } = "";
    public int StarCount { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
}
