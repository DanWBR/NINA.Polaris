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

using System;
using System.Collections.Generic;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Removes dust motes — the soft, roughly circular shadows a speck of dust on
/// the sensor, filter or camera window casts on the sky. A mote is not added
/// darkness, it is a smooth MULTIPLICATIVE dip: the dust transmits a fraction
/// t(x,y) &lt; 1 of the light. The physically correct fix is therefore to divide
/// the image by that transmission, i.e. a LOCAL synthetic flat, rather than to
/// paint the hole in. Dividing also restores the stars that happen to sit on a
/// mote instead of erasing them, which distinguishes this from inpainting.
///
/// <para>The transmission is estimated from the sky the mote sits in:</para>
/// <list type="number">
///   <item>a star-rejected background <c>B</c> (the smooth sky, stars removed);</item>
///   <item>a large-scale background <c>Blarge</c> (the sky WITHOUT the mote);</item>
///   <item><c>ratio = B / Blarge</c> dips below 1 exactly on the motes — that is
///         the detector;</item>
///   <item>per channel, multiply by <c>S / B</c> inside each mote (S = the sky
///         level in a ring just outside it), tapered to 1 over a feather so no
///         ring is left behind.</item>
/// </list>
///
/// <para>Everything is computed on a downscaled WORKING image (the correction is
/// a low-frequency field, so a ~1k-px copy captures it fully) and the resulting
/// smooth per-channel gain map is bilinearly upsampled and multiplied into the
/// full-resolution frame. Stars stay sharp because they are only ever multiplied
/// by a smooth map, and the cost is paid at 1 MP not 20. Working in double is
/// deliberate: on a faint stretched sky (a few hundred counts) a 1.6% dip is a
/// handful of counts, and a gain map quantised to 16-bit would band.</para>
/// </summary>
public static class DustMoteRemoval {

    /// <summary>User-facing knobs. Sizes are a PERCENT OF THE LONG SIDE so they
    /// mean the same thing on the preview and on the full frame.</summary>
    public sealed record Params(
        double SensitivityPct = 0.6,   // how deep a dip counts as a mote
        double MinSizePct     = 2.0,   // reject detections smaller than this radius
        double FeatherPct     = 2.5,   // softness of the correction edge
        double StrengthPct    = 100.0, // 0..100, scales how much of S/B is applied
        int    WorkingLongSide = 1024);

    /// <summary>A detected mote, in WORKING-image pixels.</summary>
    public sealed record Mote(double X, double Y, double R);

    /// <summary>The correction, computed on the working image and ready to apply
    /// at either resolution. <see cref="GainWork"/> holds one smooth gain plane
    /// per channel (working size); <see cref="WorkOriginal"/> holds the matching
    /// downscaled source planes so a preview can render before/after without
    /// recomputing.</summary>
    public sealed record Plan(
        double[][] GainWork,
        ushort[][] WorkOriginal,
        IReadOnlyList<Mote> Motes,
        int WorkWidth, int WorkHeight, int Channels);

    // --- public API ---------------------------------------------------------

    /// <summary>Detect the motes and build the correction on the working image.
    /// <paramref name="pixels"/> is plane-sequential (R plane, then G, then B),
    /// the FITS/PixInsight convention. <paramref name="channels"/> is 1 or 3.</summary>
    public static Plan Analyze(ushort[] pixels, int width, int height, int channels, Params p) {
        if (pixels == null) throw new ArgumentNullException(nameof(pixels));
        if (channels != 1 && channels != 3)
            throw new ArgumentException("channels must be 1 or 3", nameof(channels));

        int longSide = Math.Max(width, height);
        int work = Math.Clamp(p.WorkingLongSide, 256, 2048);
        double scale = longSide > work ? (double)work / longSide : 1.0;
        int ww = Math.Max(16, (int)Math.Round(width * scale));
        int wh = Math.Max(16, (int)Math.Round(height * scale));
        int wn = ww * wh;
        int wl = Math.Max(ww, wh);

        // Downscale every channel to the working grid (area average).
        var chanWork = new ushort[channels][];
        var chanD    = new double[channels][];
        for (int c = 0; c < channels; c++) {
            chanWork[c] = Downscale(pixels, width, height, c, ww, wh);
            var d = new double[wn];
            for (int i = 0; i < wn; i++) d[i] = chanWork[c][i];
            chanD[c] = d;
        }

        // Working luminance (mean of channels) drives detection.
        var lum = new double[wn];
        for (int i = 0; i < wn; i++) {
            double s = 0; for (int c = 0; c < channels; c++) s += chanD[c][i];
            lum[i] = s / channels;
        }

        // Radii, in working pixels, from the percent-of-long-side knobs.
        int bgR    = Math.Max(2, (int)Math.Round(0.008 * wl)); // smooths out stars, keeps the mote
        int largeR = Math.Max(bgR + 2, (int)Math.Round(0.060 * wl)); // large scale = sky without the mote
        double featherPx = Math.Max(2.0, p.FeatherPct / 100.0 * wl);
        double minRadius = Math.Max(2.0, p.MinSizePct  / 100.0 * wl);
        double sens = Math.Clamp(p.SensitivityPct, 0.05, 10.0) / 100.0;
        // Up to 200%: at 100% the correction lifts the mote to the surrounding
        // sky; past that it overshoots, which is the lever for a mote 100% still
        // leaves visibly dark.
        double strength = Math.Clamp(p.StrengthPct, 0.0, 200.0) / 100.0;

        // Star-rejected luminance background and its large-scale version.
        double sky = Median(lum);
        double sigma = SigmaMad(lum, sky);
        var bLum = StarRejectedBackground(lum, ww, wh, bgR, sky, sigma);
        var bLarge = Smooth((double[])bLum.Clone(), ww, wh, largeR);
        var ratio = new double[wn];
        for (int i = 0; i < wn; i++) ratio[i] = bLum[i] / Math.Max(bLarge[i], 1e-9);

        // Detect motes on the ratio field.
        double skyB = Median(bLum);
        var nebula = NebulaMask(bLum, bLarge, ww, wh, skyB, (int)Math.Round(0.012 * wl));
        var motes = DetectMotes(ratio, nebula, ww, wh, sens, minRadius, featherPx);

        // Build one smooth gain plane per channel: S / Bc inside each mote,
        // tapered to 1 over the feather. Each channel gets its own background
        // so a faintly chromatic mote is corrected per colour.
        var gain = new double[channels][];
        for (int c = 0; c < channels; c++) {
            var g = new double[wn];
            for (int i = 0; i < wn; i++) g[i] = 1.0;
            gain[c] = g;
        }
        if (motes.Count > 0) {
            for (int c = 0; c < channels; c++) {
                double skyC = Median(chanD[c]);
                double sigC = SigmaMad(chanD[c], skyC);
                var bc = StarRejectedBackground(chanD[c], ww, wh, bgR, skyC, sigC);
                ApplyCorrectionField(gain[c], bc, ww, wh, motes, featherPx, strength);
            }
        }

        return new Plan(gain, chanWork, motes, ww, wh, channels);
    }

    /// <summary>Render the working ORIGINAL as a packed plane-sequential buffer
    /// (for the "hold to compare" side of a preview).</summary>
    public static ushort[] PackWorkingOriginal(Plan plan) {
        int wn = plan.WorkWidth * plan.WorkHeight;
        var outp = new ushort[wn * plan.Channels];
        for (int c = 0; c < plan.Channels; c++)
            Array.Copy(plan.WorkOriginal[c], 0, outp, c * wn, wn);
        return outp;
    }

    /// <summary>Render the working CORRECTED image (original × gain) as a packed
    /// plane-sequential buffer (the preview the operator actually sees).</summary>
    public static ushort[] PackWorkingCorrected(Plan plan) {
        int wn = plan.WorkWidth * plan.WorkHeight;
        var outp = new ushort[wn * plan.Channels];
        for (int c = 0; c < plan.Channels; c++) {
            var src = plan.WorkOriginal[c];
            var g = plan.GainWork[c];
            for (int i = 0; i < wn; i++)
                outp[c * wn + i] = Clamp16(src[i] * g[i]);
        }
        return outp;
    }

    /// <summary>Apply a plan to the FULL-resolution frame: upsample each smooth
    /// gain plane and multiply it into the matching channel. Returns a new
    /// buffer; the input is left untouched.</summary>
    public static ushort[] ApplyFull(ushort[] pixels, int width, int height, int channels, Plan plan) {
        int plane = width * height;
        var outp = new ushort[pixels.Length];
        for (int c = 0; c < channels; c++) {
            var g = plan.GainWork[c];
            int off = c * plane;
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    double gv = SampleBilinear(g, plan.WorkWidth, plan.WorkHeight,
                                               (double)x / Math.Max(1, width - 1) * (plan.WorkWidth - 1),
                                               (double)y / Math.Max(1, height - 1) * (plan.WorkHeight - 1));
                    int idx = off + y * width + x;
                    outp[idx] = Clamp16(pixels[idx] * gv);
                }
            }
        }
        // Carry any channels the plan does not cover (defensive; channels match
        // in practice).
        for (int c = channels; c * plane < pixels.Length; c++)
            Array.Copy(pixels, c * plane, outp, c * plane, plane);
        return outp;
    }

    // --- detection ----------------------------------------------------------

    private readonly record struct Cand(double Cx, double Cy, double Reff, double Fill);

    /// <summary>Find the motes in the ratio field.
    ///
    /// <para>A single global threshold does not work: motes and the faint dark
    /// lanes between them merge into one sprawling, non-circular blob at a
    /// shallow cut, while a deep cut fragments a mote's own core. So sweep a
    /// RANGE of depths (an MSER-style idea) and keep every blob that is compact
    /// and round at ANY level. A real mote is a stable, circular island across a
    /// band of thresholds; diffuse structure never becomes both small and round.
    /// Candidates from all levels are then clustered by proximity so each mote is
    /// reported once, at the level where it looked cleanest.</para></summary>
    private static List<Mote> DetectMotes(double[] ratio, bool[] nebula, int ww, int wh,
                                          double sens, double minRadius, double featherPx) {
        int wl = Math.Max(ww, wh);
        double minArea = Math.PI * (0.5 * minRadius) * (0.5 * minRadius);
        // A dust mote is a local speck's shadow, not a swathe of the frame. Cap
        // the radius so a large diffuse dark region (a nebula edge, a gradient
        // residual) can never masquerade as one.
        double maxRadius = 0.12 * wl;
        double maxArea = Math.PI * maxRadius * maxRadius;
        const double fillMin = 0.55;

        var cands = new List<Cand>();
        // Depth multipliers on `sens`, shallow → deep. A mote surfaces as a
        // compact island somewhere along this band regardless of its exact depth.
        double[] mult = { 0.7, 1.0, 1.3, 1.6, 2.0, 2.5, 3.0 };
        foreach (var m in mult) {
            double t = 1.0 - m * sens;
            if (t <= 0) continue;
            CollectCompactBlobs(ratio, nebula, ww, wh, t, minArea, maxArea, minRadius, fillMin, cands);
        }

        // Cluster: strongest (roundest) first, drop anything whose centre falls
        // inside an already-accepted mote.
        cands.Sort((a, b) => b.Fill.CompareTo(a.Fill));
        var motes = new List<Mote>();
        var centers = new List<(double x, double y, double r)>();
        foreach (var c in cands) {
            bool covered = false;
            foreach (var k in centers) {
                double dx = c.Cx - k.x, dy = c.Cy - k.y;
                if (dx * dx + dy * dy < k.r * k.r) { covered = true; break; }
            }
            if (covered) continue;
            // Depth gate: the CORE must genuinely darken, not just be a compact
            // patch of shallow background noise. A real mote here sits ~2% down;
            // requiring the core median below 1 − 1.2·sens rejects the faint
            // gradient/noise blobs that survive the compactness test.
            double coreDepth = CoreMedian(ratio, ww, wh, c.Cx, c.Cy,
                                          Math.Max(3.0, 0.4 * c.Reff));
            if (coreDepth >= 1.0 - 1.2 * sens) continue;
            centers.Add((c.Cx, c.Cy, Math.Max(c.Reff, minRadius)));
            double rRec = RecoveryRadius(ratio, ww, wh, c.Cx, c.Cy, Math.Min(wh, ww));
            motes.Add(new Mote(c.Cx, c.Cy, Math.Max(c.Reff, rRec)));
        }
        return motes;
    }

    /// <summary>Label the connected dark regions at one threshold and append the
    /// compact, round, correctly-sized ones as candidates.</summary>
    private static void CollectCompactBlobs(double[] ratio, bool[] nebula, int ww, int wh,
                                            double t, double minArea, double maxArea,
                                            double minRadius, double fillMin, List<Cand> outCands) {
        int wn = ww * wh;
        var label = new int[wn];
        var stack = new Stack<int>();
        int next = 1;
        for (int start = 0; start < wn; start++) {
            if (label[start] != 0 || nebula[start] || ratio[start] >= t) continue;
            stack.Push(start); label[start] = next;
            long area = 0; double sumX = 0, sumY = 0;
            var xs = new List<int>(); var ys = new List<int>();
            while (stack.Count > 0) {
                int i = stack.Pop();
                int x = i % ww, y = i / ww;
                area++; sumX += x; sumY += y; xs.Add(x); ys.Add(y);
                if (x > 0    && label[i - 1]  == 0 && !nebula[i - 1]  && ratio[i - 1]  < t) { label[i - 1]  = next; stack.Push(i - 1); }
                if (x < ww-1 && label[i + 1]  == 0 && !nebula[i + 1]  && ratio[i + 1]  < t) { label[i + 1]  = next; stack.Push(i + 1); }
                if (y > 0    && label[i - ww] == 0 && !nebula[i - ww] && ratio[i - ww] < t) { label[i - ww] = next; stack.Push(i - ww); }
                if (y < wh-1 && label[i + ww] == 0 && !nebula[i + ww] && ratio[i + ww] < t) { label[i + ww] = next; stack.Push(i + ww); }
            }
            next++;
            if (area < minArea || area > maxArea) continue;
            double cx = sumX / area, cy = sumY / area;
            double reff = Math.Sqrt(area / Math.PI);
            if (reff < minRadius) continue;
            double maxR2 = 0;
            for (int k = 0; k < xs.Count; k++) {
                double dx = xs[k] - cx, dy = ys[k] - cy;
                double r2 = dx * dx + dy * dy; if (r2 > maxR2) maxR2 = r2;
            }
            double fill = area / (Math.PI * maxR2 + 1e-9);
            if (fill < fillMin) continue;
            outCands.Add(new Cand(cx, cy, reff, fill));
        }
    }

    /// <summary>Median ratio within <paramref name="r"/> px of a centre — the
    /// mote's core depth, robust to the odd star pixel.</summary>
    private static double CoreMedian(double[] ratio, int ww, int wh,
                                     double cx, double cy, double r) {
        var vals = new List<double>();
        int x0 = (int)Math.Max(0, cx - r), x1 = (int)Math.Min(ww - 1, cx + r);
        int y0 = (int)Math.Max(0, cy - r), y1 = (int)Math.Min(wh - 1, cy + r);
        double r2 = r * r;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) {
                double dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r2) vals.Add(ratio[y * ww + x]);
            }
        if (vals.Count == 0) return 1.0;
        vals.Sort();
        return vals[vals.Count / 2];
    }

    /// <summary>Walk outward from a mote centre in radial bins and return the
    /// radius at which the median ratio has climbed back above ~1 and stays
    /// there — the outer edge of the shadow including its soft wings.</summary>
    private static double RecoveryRadius(double[] ratio, int ww, int wh,
                                         double cx, double cy, int maxR) {
        const int step = 4;
        int bins = Math.Max(4, maxR / step);
        var prof = new double[bins];
        var tmp = new List<double>();
        for (int b = 0; b < bins; b++) {
            double r0 = b * step, r1 = r0 + step;
            tmp.Clear();
            int x0 = (int)Math.Max(0, cx - r1), x1 = (int)Math.Min(ww - 1, cx + r1);
            int y0 = (int)Math.Max(0, cy - r1), y1 = (int)Math.Min(wh - 1, cy + r1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d >= r0 && d < r1) tmp.Add(ratio[y * ww + x]);
                }
            prof[b] = tmp.Count > 0 ? Median(tmp.ToArray()) : 1.0;
        }
        int kMin = 0; double vMin = double.MaxValue;
        for (int b = 0; b < bins; b++) if (prof[b] < vMin) { vMin = prof[b]; kMin = b; }
        for (int b = kMin; b < bins; b++) {
            bool ok = true;
            for (int j = b; j < Math.Min(bins, b + 2); j++) if (prof[j] <= 0.999) ok = false;
            if (ok) return b * step;
        }
        return Math.Min(maxR, (kMin + 4) * step);
    }

    /// <summary>For each mote, divide by the local transmission S/Bc inside a
    /// disk, tapered smoothly to 1 across the feather so no ring is left.</summary>
    private static void ApplyCorrectionField(double[] gain, double[] bc, int ww, int wh,
                                             IReadOnlyList<Mote> motes, double featherPx,
                                             double strength) {
        foreach (var m in motes) {
            double rOut = m.R + featherPx;
            double ringLo = rOut, ringHi = rOut + Math.Max(featherPx, 0.03 * Math.Max(ww, wh));
            // Sky level in a ring just outside the mote.
            var ring = new List<double>();
            int x0 = (int)Math.Max(0, m.X - ringHi), x1 = (int)Math.Min(ww - 1, m.X + ringHi);
            int y0 = (int)Math.Max(0, m.Y - ringHi), y1 = (int)Math.Min(wh - 1, m.Y + ringHi);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) {
                    double d = Math.Sqrt((x - m.X) * (x - m.X) + (y - m.Y) * (y - m.Y));
                    if (d >= ringLo && d < ringHi) ring.Add(bc[y * ww + x]);
                }
            if (ring.Count == 0) continue;
            double S = Median(ring.ToArray());
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) {
                    double d = Math.Sqrt((x - m.X) * (x - m.X) + (y - m.Y) * (y - m.Y));
                    if (d >= rOut) continue;
                    int i = y * ww + x;
                    double corr = Math.Clamp(S / Math.Max(bc[i], 1e-9), 1.0, 2.0);
                    double t = Math.Clamp((rOut - d) / featherPx, 0.0, 1.0);
                    double w = t * t * (3 - 2 * t); // smoothstep: 1 inside R, 0 at R+feather
                    gain[i] *= 1.0 + strength * w * (corr - 1.0);
                }
        }
    }

    // --- background estimation ---------------------------------------------

    /// <summary>Smooth sky with stars rejected. A star spike must be capped
    /// BEFORE the first blur — blurring first would smear its huge value across
    /// a dozen pixels and leave a bump the later passes cannot fully undo, which
    /// both hides the true sky under a star sitting on a mote and fakes a ring of
    /// ratio&lt;1 around every star on clean sky. So hard-clip to a sky ceiling up
    /// front (motes are darker than sky, never clipped), then two blur-and-reject
    /// passes clean the star wings.</summary>
    private static double[] StarRejectedBackground(double[] src, int ww, int wh, int radius,
                                                   double sky, double sigma) {
        int wn = ww * wh;
        double k = 2.5;
        double sig = Math.Max(sigma, 1.0);
        double ceiling = sky + k * sig;
        var cur = new double[wn];
        for (int i = 0; i < wn; i++) cur[i] = Math.Min(src[i], ceiling);
        var bg = Smooth((double[])cur.Clone(), ww, wh, radius);
        for (int pass = 0; pass < 2; pass++) {
            for (int i = 0; i < wn; i++)
                if (cur[i] > bg[i] + k * sig) cur[i] = bg[i];
            bg = Smooth((double[])cur.Clone(), ww, wh, radius);
        }
        return bg;
    }

    /// <summary>Where NOT to look for motes. Two exclusions: the bright object
    /// itself (<c>B &gt; 1.15·sky</c>), and — crucially — its HALO. A bright
    /// nebula lifts the large-scale background <c>Blarge</c> over a region wider
    /// than the nebula, and just outside its edge <c>B</c> has returned to sky
    /// while <c>Blarge</c> has not, so <c>ratio = B/Blarge</c> dips below 1 in a
    /// ring and fakes a mote. Excluding where <c>Blarge &gt; 1.06·sky</c> removes
    /// that whole zone of influence; clean sky keeps Blarge ≈ sky and is
    /// untouched.</summary>
    private static bool[] NebulaMask(double[] b, double[] bLarge, int ww, int wh,
                                     double sky, int dilate) {
        int wn = ww * wh;
        var m = new bool[wn];
        for (int i = 0; i < wn; i++) m[i] = b[i] > sky * 1.15 || bLarge[i] > sky * 1.06;
        // Grayscale-free binary dilation by repeated 3×3 max.
        for (int it = 0; it < dilate; it++) {
            var n = (bool[])m.Clone();
            for (int y = 0; y < wh; y++)
                for (int x = 0; x < ww; x++) {
                    int i = y * ww + x;
                    if (m[i]) continue;
                    if ((x > 0 && m[i - 1]) || (x < ww - 1 && m[i + 1]) ||
                        (y > 0 && m[i - ww]) || (y < wh - 1 && m[i + ww])) n[i] = true;
                }
            m = n;
        }
        return m;
    }

    // --- low-level maths ----------------------------------------------------

    /// <summary>Three box-blur passes ≈ a Gaussian, but O(n) regardless of
    /// radius (running sums), so even the large-scale pass is cheap. Replicate
    /// edges. Operates in place on a working copy and returns it.</summary>
    private static double[] Smooth(double[] data, int ww, int wh, int radius) {
        if (radius < 1) return data;
        var a = data;
        var b = new double[a.Length];
        for (int pass = 0; pass < 3; pass++) {
            BoxH(a, b, ww, wh, radius);
            BoxV(b, a, ww, wh, radius);
        }
        return a;
    }

    private static void BoxH(double[] src, double[] dst, int ww, int wh, int r) {
        double inv = 1.0 / (2 * r + 1);
        for (int y = 0; y < wh; y++) {
            int row = y * ww;
            double sum = src[row] * (r + 1);
            for (int x = 1; x <= r; x++) sum += src[row + Math.Min(x, ww - 1)];
            for (int x = 0; x < ww; x++) {
                dst[row + x] = sum * inv;
                int add = Math.Min(x + r + 1, ww - 1);
                int sub = Math.Max(x - r, 0);
                sum += src[row + add] - src[row + sub];
            }
        }
    }

    private static void BoxV(double[] src, double[] dst, int ww, int wh, int r) {
        double inv = 1.0 / (2 * r + 1);
        for (int x = 0; x < ww; x++) {
            double sum = src[x] * (r + 1);
            for (int y = 1; y <= r; y++) sum += src[Math.Min(y, wh - 1) * ww + x];
            for (int y = 0; y < wh; y++) {
                dst[y * ww + x] = sum * inv;
                int add = Math.Min(y + r + 1, wh - 1);
                int sub = Math.Max(y - r, 0);
                sum += src[add * ww + x] - src[sub * ww + x];
            }
        }
    }

    /// <summary>Area-average downscale of one plane-sequential channel to the
    /// working grid. Scatter accumulate, then divide by the hit count.</summary>
    private static ushort[] Downscale(ushort[] pixels, int width, int height, int channel,
                                      int ww, int wh) {
        int plane = width * height, off = channel * plane, wn = ww * wh;
        var acc = new double[wn];
        var cnt = new int[wn];
        for (int y = 0; y < height; y++) {
            int by = Math.Min(wh - 1, (int)((long)y * wh / height));
            for (int x = 0; x < width; x++) {
                int bx = Math.Min(ww - 1, (int)((long)x * ww / width));
                int bi = by * ww + bx;
                acc[bi] += pixels[off + y * width + x];
                cnt[bi]++;
            }
        }
        var outp = new ushort[wn];
        for (int i = 0; i < wn; i++)
            outp[i] = cnt[i] > 0 ? Clamp16(acc[i] / cnt[i]) : (ushort)0;
        return outp;
    }

    private static double SampleBilinear(double[] map, int ww, int wh, double fx, double fy) {
        if (fx < 0) fx = 0; else if (fx > ww - 1) fx = ww - 1;
        if (fy < 0) fy = 0; else if (fy > wh - 1) fy = wh - 1;
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, ww - 1), y1 = Math.Min(y0 + 1, wh - 1);
        double dx = fx - x0, dy = fy - y0;
        double v00 = map[y0 * ww + x0], v10 = map[y0 * ww + x1];
        double v01 = map[y1 * ww + x0], v11 = map[y1 * ww + x1];
        return v00 * (1 - dx) * (1 - dy) + v10 * dx * (1 - dy)
             + v01 * (1 - dx) * dy + v11 * dx * dy;
    }

    private static double Median(double[] v) {
        if (v.Length == 0) return 0;
        // Median of a subsample keeps this cheap on the full working plane.
        int stride = Math.Max(1, v.Length / 40000);
        var s = new List<double>(v.Length / stride + 1);
        for (int i = 0; i < v.Length; i += stride) s.Add(v[i]);
        s.Sort();
        return s[s.Count / 2];
    }

    private static double SigmaMad(double[] v, double median) {
        int stride = Math.Max(1, v.Length / 40000);
        var s = new List<double>(v.Length / stride + 1);
        for (int i = 0; i < v.Length; i += stride) s.Add(Math.Abs(v[i] - median));
        if (s.Count == 0) return 1.0;
        s.Sort();
        double mad = s[s.Count / 2];
        return Math.Max(1e-6, mad * 1.4826);
    }

    private static ushort Clamp16(double v) =>
        (ushort)Math.Clamp(Math.Round(v), 0, 65535);
}
