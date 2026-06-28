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
        double Aggressiveness = 1.0,    // 0..1
        double ExclusionRadius = 9.0,   // px; how close a neighbour star is "masked off"
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

            // Pick the largest/brightest stars for the before/after montage and
            // snapshot their ORIGINAL crops now (before align/repair mutate R/B).
            var montStars = PickMontageStars(stars, W, H);
            var beforeCrops = montStars.Select(s => ExtractCropRgb(R, G, B, W, s.x, s.y)).ToList();

            if (req.Align && agg > 0) {
                _jobs[jobId] = _jobs[jobId] with { Stage = "aligning" };
                AlignChannels(R, G, B, W, H, stars, agg);
            }
            if (req.Fringe && agg > 0) {
                _jobs[jobId] = _jobs[jobId] with { Stage = "repairing" };
                double excl = Math.Clamp(req.ExclusionRadius, 3.0, 20.0);
                foreach (var (sx, sy) in stars) RepairStar(R, G, B, W, H, sx, sy, 22, agg, stars, excl);
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

            // Largest-stars before/after montages (same layout) for the comparator,
            // since the fringe is invisible at full-frame scale.
            string? beforePath = null, afterPath = null;
            if (montStars.Count > 0) {
                var afterCrops = montStars.Select(s => ExtractCropRgb(R, G, B, W, s.x, s.y)).ToList();
                beforePath = WriteMontage(beforeCrops, SiblingPath(req.FramePath, "starcolor_stars_before"), img);
                afterPath  = WriteMontage(afterCrops,  SiblingPath(req.FramePath, "starcolor_stars_after"),  img);
            }

            _logger.LogInformation("Star colour repair {Job}: {N} stars, agg={Agg}, wrote {Path}",
                jobId, stars.Count, agg, outPath);
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false, Stage = "done", StarCount = stars.Count, OutputPath = outPath,
                StarsBeforePath = beforePath, StarsAfterPath = afterPath,
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
    // Neighbour-aware: when bright stars are close, the window overlaps them, so
    // we (1) refine the centre only in the core (neighbours don't pull it),
    // (2) EXCLUDE neighbour pixels from the per-ring medians, and (3) don't
    // rewrite neighbour pixels (each star is handled by its own call).
    private static void RepairStar(double[] R, double[] G, double[] B, int W, int H,
                                   int cx, int cy, int win, double agg,
                                   List<(int x, int y)> allStars, double excl) {
        if (cx < win || cy < win || cx >= W - win || cy >= H - win) return;
        int n = 2 * win + 1, nn = n * n;

        // neighbour centres in window-local coords (exclude self)
        var neigh = new List<(double x, double y)>();
        double inc = win + excl;
        foreach (var s in allStars) {
            if (s.x == cx && s.y == cy) continue;
            double lx = s.x - cx, ly = s.y - cy;
            if (Math.Abs(lx) <= inc && Math.Abs(ly) <= inc) neigh.Add((lx, ly));
        }
        double excl2 = excl * excl;
        var isNeigh = new bool[nn];
        if (neigh.Count > 0)
            for (int yy = 0; yy < n; yy++)
                for (int xx = 0; xx < n; xx++) {
                    double lx = xx - win, ly = yy - win;
                    foreach (var nb in neigh) {
                        double dx = lx - nb.x, dy = ly - nb.y;
                        if (dx * dx + dy * dy < excl2) { isNeigh[yy * n + xx] = true; break; }
                    }
                }

        // gather window
        var lr = new double[nn]; var lg = new double[nn]; var lb = new double[nn]; var L = new double[nn];
        for (int yy = 0; yy < n; yy++) {
            int row = (cy - win + yy) * W + (cx - win);
            for (int xx = 0; xx < n; xx++) {
                int k = yy * n + xx, src = row + xx;
                double r = R[src], g = G[src], b = B[src];
                lr[k] = r; lg[k] = g; lb[k] = b; L[k] = (r + g + b) / 3.0 + 1e-6;
            }
        }

        // sub-pixel centre: only the core (r<=6 of the detected peak), neighbours excluded
        double bg = Median(L);
        double sw = 0, scx = 0, scy = 0;
        for (int yy = 0; yy < n; yy++)
            for (int xx = 0; xx < n; xx++) {
                int k = yy * n + xx;
                if (isNeigh[k]) continue;
                double lx = xx - win, ly = yy - win;
                if (lx * lx + ly * ly > 36) continue;        // core only
                double w = L[k] - bg; if (w <= 0) continue;
                sw += w; scx += w * lx; scy += w * ly;
            }
        double ccx = sw > 0 ? scx / sw : 0, ccy = sw > 0 ? scy / sw : 0;

        // radius per pixel
        var ri = new int[nn]; int rmax = 0;
        for (int yy = 0; yy < n; yy++)
            for (int xx = 0; xx < n; xx++) {
                double dx = (xx - win) - ccx, dy = (yy - win) - ccy;
                int r = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy));
                ri[yy * n + xx] = r; if (r > rmax) rmax = r;
            }

        // per-ring medians, EXCLUDING neighbour pixels (count<3 => invalid)
        var ratR = new double[nn]; var ratG = new double[nn]; var ratB = new double[nn];
        for (int k = 0; k < nn; k++) { ratR[k] = lr[k] / L[k]; ratG[k] = lg[k] / L[k]; ratB[k] = lb[k] / L[k]; }
        var (medL, ok) = RingMedian(L, ri, rmax, isNeigh);
        var (medRR, _) = RingMedian(ratR, ri, rmax, isNeigh);
        var (medRG, _) = RingMedian(ratG, ri, rmax, isNeigh);
        var (medRB, _) = RingMedian(ratB, ri, rmax, isNeigh);

        // rebuild + write back
        for (int yy = 0; yy < n; yy++) {
            for (int xx = 0; xx < n; xx++) {
                int k = yy * n + xx, r = ri[k];
                if (isNeigh[k] || !ok[r]) continue;          // leave neighbours/invalid rings
                double lm = medL[r];
                bool companion = L[k] > lm * 1.8 + 0.02 * 65535.0;
                double rr, gg, bb;
                if (companion) { rr = lr[k]; gg = lg[k]; bb = lb[k]; }
                else {
                    double lout = Math.Max(L[k], lm);
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

    /// <summary>Per-ring median ignoring masked pixels; ok[r]=false if a ring had
    /// fewer than 3 valid samples (can't trust a symmetric estimate there).</summary>
    private static (double[] med, bool[] ok) RingMedian(double[] vals, int[] ri, int rmax, bool[] mask) {
        var buckets = new List<double>[rmax + 1];
        for (int r = 0; r <= rmax; r++) buckets[r] = new List<double>();
        for (int k = 0; k < vals.Length; k++) if (!mask[k]) buckets[ri[k]].Add(vals[k]);
        var med = new double[rmax + 1]; var ok = new bool[rmax + 1];
        for (int r = 0; r <= rmax; r++) {
            if (buckets[r].Count >= 3) { med[r] = Median(buckets[r]); ok[r] = true; }
        }
        return (med, ok);
    }

    private static double Median(IReadOnlyList<double> v) {
        if (v.Count == 0) return 0;
        var a = v.ToArray(); Array.Sort(a);
        int m = a.Length / 2;
        return (a.Length & 1) == 1 ? a[m] : 0.5 * (a[m - 1] + a[m]);
    }

    private static ushort Clamp16(double v)
        => (ushort)Math.Clamp(Math.Round(v), 0, 65535);

    // ── largest-stars before/after montage ────────────────────────────────────
    private const int CropPx = 48;        // crop side per star
    private const int MontCols = 4;       // montage columns
    private const int MontMax = 12;       // up to N largest stars

    private static List<(int x, int y)> PickMontageStars(List<(int x, int y)> stars, int W, int H) {
        int half = CropPx / 2;
        var outl = new List<(int x, int y)>();
        foreach (var s in stars) {            // stars[] already brightest-first
            if (s.x >= half && s.y >= half && s.x < W - half && s.y < H - half) {
                outl.Add(s);
                if (outl.Count >= MontMax) break;
            }
        }
        return outl;
    }

    /// <summary>Plane-sequential ushort RGB crop (CropPx²) centred on a star.</summary>
    private static ushort[] ExtractCropRgb(double[] R, double[] G, double[] B, int W, int cx, int cy) {
        int half = CropPx / 2, cp = CropPx * CropPx;
        var crop = new ushort[cp * 3];
        for (int yy = 0; yy < CropPx; yy++) {
            int sy = cy - half + yy, drow = yy * CropPx;
            int srow = sy * W + (cx - half);
            for (int xx = 0; xx < CropPx; xx++) {
                int d = drow + xx, s = srow + xx;
                crop[d] = Clamp16(R[s]); crop[cp + d] = Clamp16(G[s]); crop[2 * cp + d] = Clamp16(B[s]);
            }
        }
        return crop;
    }

    private string WriteMontage(List<ushort[]> crops, string outPath, BaseImageData template) {
        int n = crops.Count;
        int cols = Math.Min(MontCols, n);
        int rows = (int)Math.Ceiling(n / (double)cols);
        int gap = 4;
        int mw = cols * CropPx + (cols - 1) * gap;
        int mh = rows * CropPx + (rows - 1) * gap;
        int mplane = mw * mh, cp = CropPx * CropPx;
        var data = new ushort[mplane * 3];   // plane-sequential RGB, black background
        for (int k = 0; k < n; k++) {
            int gc = k % cols, gr = k / cols;
            int ox = gc * (CropPx + gap), oy = gr * (CropPx + gap);
            var crop = crops[k];
            for (int yy = 0; yy < CropPx; yy++) {
                int mrow = (oy + yy) * mw + ox, crow = yy * CropPx;
                for (int xx = 0; xx < CropPx; xx++) {
                    int m = mrow + xx, c = crow + xx;
                    data[m] = crop[c]; data[mplane + m] = crop[cp + c]; data[2 * mplane + m] = crop[2 * cp + c];
                }
            }
        }
        var props = new NINA.Image.ImageData.ImageProperties {
            Width = mw, Height = mh, BitDepth = 16,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None, IsBayered = false, Channels = 3,
        };
        var meta = new NINA.Image.ImageData.ImageMetaData();
        FITSWriter.Write(new BaseImageData(data, props, meta), outPath);
        return outPath;
    }

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
    public string? StarsBeforePath { get; init; }
    public string? StarsAfterPath { get; init; }
    public string? Error { get; init; }
}
