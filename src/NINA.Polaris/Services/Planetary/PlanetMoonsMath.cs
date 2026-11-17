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

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Metering and compositing for the planet + moons capture: the planet is
/// exposed for its disc, the moons for themselves (the disc saturates), and
/// the two stacks are merged into one picture.
///
/// <para>Metering works on 2x2-binned frames, which treats a Bayer mosaic and
/// a mono frame alike and halves the noise. Frames arrive on the 16-bit scale
/// every backend widens to, so full scale is 65535.</para>
/// </summary>
public static class PlanetMoonsMath {
    public const double FullScale = 65535.0;

    /// <summary>What one frame says about the planet.</summary>
    public readonly record struct DiscReading(
        bool Found, double Background, double Noise, double CenterX, double CenterY,
        double Radius, double Signal, double SaturatedFraction);

    /// <summary>What one frame says about the moons: the peak of the
    /// brightest point source outside the disc, above the background.</summary>
    public readonly record struct MoonReading(bool Found, double Signal, int Count);

    /// <summary>2x2 mean binning. Returns the binned plane and its size.</summary>
    public static (float[] Plane, int Width, int Height) Bin2(ushort[] data, int width, int height) {
        int w = width / 2, h = height / 2;
        var o = new float[Math.Max(0, w * h)];
        for (int y = 0; y < h; y++) {
            int r0 = 2 * y * width, r1 = r0 + width;
            for (int x = 0; x < w; x++) {
                int c = 2 * x;
                o[y * w + x] = (data[r0 + c] + data[r0 + c + 1] + data[r1 + c] + data[r1 + c + 1]) * 0.25f;
            }
        }
        return (o, w, h);
    }

    /// <summary>Background (median) and noise (1.4826 x MAD) from a strided
    /// sample. The planet covers a small part of a planetary frame, so the
    /// median is the sky.</summary>
    public static (double Background, double Noise) BackgroundAndNoise(float[] plane) {
        int step = Math.Max(1, plane.Length / 20000);
        var s = new List<float>(plane.Length / step + 1);
        for (int i = 0; i < plane.Length; i += step) s.Add(plane[i]);
        if (s.Count == 0) return (0, 1);
        s.Sort();
        double med = s[s.Count / 2];
        var dev = s.Select(v => Math.Abs(v - med)).ToList();
        dev.Sort();
        double noise = Math.Max(1.0, 1.4826 * dev[dev.Count / 2]);
        return (med, noise);
    }

    /// <summary>Find the disc: the pixels above a quarter of the way from the
    /// background to the peak. Signal is the 99.5th percentile of the disc
    /// over the background, the level the planet exposure aims at.</summary>
    public static DiscReading ReadDisc(float[] plane, int width, int height) {
        var (bg, noise) = BackgroundAndNoise(plane);
        float peak = 0;
        foreach (var v in plane) if (v > peak) peak = v;
        if (peak - bg < 20 * noise) return new DiscReading(false, bg, noise, 0, 0, 0, 0, 0);
        double thr = bg + 0.25 * (peak - bg);
        // The disc is the largest blob above the threshold; a bright moon is
        // a separate, smaller one and stays out of the centre and the radius.
        var above = new bool[plane.Length];
        for (int i = 0; i < plane.Length; i++) above[i] = plane[i] >= thr;
        var blob = LargestComponent(above, width, height);
        double sx = 0, sy = 0, sw = 0;
        var disc = new List<float>();
        long saturated = 0;
        double sat = 0.97 * FullScale;
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                if (!blob[y * width + x]) continue;
                float v = plane[y * width + x];
                double wgt = v - bg;
                sx += x * wgt; sy += y * wgt; sw += wgt;
                disc.Add(v);
                if (v >= sat) saturated++;
            }
        }
        if (disc.Count < 4 || sw <= 0) return new DiscReading(false, bg, noise, 0, 0, 0, 0, 0);
        disc.Sort();
        double p995 = disc[Math.Min(disc.Count - 1, (int)(disc.Count * 0.995))];
        return new DiscReading(true, bg, noise, sx / sw, sy / sw,
            Math.Sqrt(disc.Count / Math.PI), p995 - bg, saturated / (double)disc.Count);
    }

    /// <summary>The brightest point source outside the disc. A source is a
    /// local maximum over its 5x5 neighbourhood, at least 8 sigma over the
    /// background, with at least 3 of its 8 neighbours above half its height
    /// (a hot pixel stands alone). Everything within 1.4 disc radii, or in the
    /// disc's glow, is skipped.</summary>
    public static MoonReading ReadMoons(float[] plane, int width, int height, DiscReading disc) {
        if (!disc.Found) return new MoonReading(false, 0, 0);
        double bg = disc.Background, noise = disc.Noise;
        double exclude = Math.Max(4, disc.Radius * 1.4);
        double ex2 = exclude * exclude;
        double best = 0; int count = 0;
        for (int y = 2; y < height - 2; y++) {
            for (int x = 2; x < width - 2; x++) {
                float v = plane[y * width + x];
                double s = v - bg;
                if (s < 8 * noise) continue;
                double dx = x - disc.CenterX, dy = y - disc.CenterY;
                if (dx * dx + dy * dy < ex2) continue;
                bool isMax = true;
                for (int j = -2; j <= 2 && isMax; j++)
                    for (int i = -2; i <= 2; i++) {
                        if (i == 0 && j == 0) continue;
                        float n = plane[(y + j) * width + x + i];
                        if (n > v || (n == v && (j < 0 || (j == 0 && i < 0)))) { isMax = false; break; }
                    }
                if (!isMax) continue;
                int support = 0;
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++) {
                        if (i == 0 && j == 0) continue;
                        if (plane[(y + j) * width + x + i] - bg > 0.5 * s) support++;
                    }
                if (support < 3) continue;
                // A source sitting in the disc's glow is the glow's own ripple:
                // its surroundings must fall back towards the sky.
                double ring = 0; int rn = 0;
                for (int i = -2; i <= 2; i++) {
                    ring += plane[(y - 2) * width + x + i] + plane[(y + 2) * width + x + i]; rn += 2;
                }
                if (ring / rn - bg > 0.6 * s) continue;
                count++;
                if (s > best) best = s;
            }
        }
        return new MoonReading(count > 0, best, count);
    }

    /// <summary>Next exposure towards a target signal. Signal is linear in
    /// exposure; the step is clamped to x0.25..x4 so one noisy reading cannot
    /// throw it far, and a saturated reading always steps down.</summary>
    public static double NextExposure(double exposure, double signal, double target, bool saturated,
                                      double min, double max) {
        double factor;
        if (saturated) factor = 0.5;
        else if (signal <= 0) factor = 4;
        else factor = Math.Clamp(target / signal, 0.25, 4.0);
        return Math.Clamp(exposure * factor, min, max);
    }

    /// <summary>Within tolerance of the target, as a fraction.</summary>
    public static bool Converged(double signal, double target, double tolerance) =>
        signal > 0 && Math.Abs(signal / target - 1) <= tolerance;

    // ---------------------------------------------------------------
    // Composite
    // ---------------------------------------------------------------

    /// <summary>One stacked image, planar (channel-major), any channel count.</summary>
    public sealed record Plane16(ushort[] Data, int Width, int Height, int Channels);

    public sealed record CompositeResult(float[] Rgb, int Width, int Height, int Channels,
                                         double ShiftX, double ShiftY, double DiscRadius);

    private static float[] Luminance(Plane16 p) {
        int n = p.Width * p.Height;
        var l = new float[n];
        for (int c = 0; c < p.Channels; c++)
            for (int i = 0; i < n; i++) l[i] += p.Data[c * n + i];
        float inv = 1f / p.Channels;
        for (int i = 0; i < n; i++) l[i] *= inv;
        return l;
    }

    private static double Percentile(List<float> values, double q) {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[Math.Clamp((int)(values.Count * q), 0, values.Count - 1)];
    }

    /// <summary>Bilinear shift of a plane by (dx, dy): out(x, y) = in(x - dx, y - dy).</summary>
    public static float[] Shift(float[] plane, int width, int height, double dx, double dy) {
        var o = new float[plane.Length];
        for (int y = 0; y < height; y++) {
            double sy = y - dy;
            int y0 = (int)Math.Floor(sy); double fy = sy - y0;
            for (int x = 0; x < width; x++) {
                double sx = x - dx;
                int x0 = (int)Math.Floor(sx); double fx = sx - x0;
                if (x0 < 0 || y0 < 0 || x0 + 1 >= width || y0 + 1 >= height) continue;
                int i = y0 * width + x0;
                o[y * width + x] = (float)(
                    plane[i] * (1 - fx) * (1 - fy) + plane[i + 1] * fx * (1 - fy) +
                    plane[i + width] * (1 - fx) * fy + plane[i + width + 1] * fx * fy);
            }
        }
        return o;
    }

    /// <summary>
    /// Merge the planet stack (short exposure, disc well exposed) and the
    /// moons stack (long exposure, disc saturated) into one display-ready
    /// image in 0..1, planar.
    ///
    /// <para>Each stack is stretched for its own subject: the planet so its
    /// disc tops out at 0.95, the moons so the brightest one sits at 0.85.
    /// The moons stack is shifted onto the planet stack by the disc centres,
    /// and a feathered mask over the disc and its glow takes the planet from
    /// the short stack and everything else from the long one.</para>
    /// </summary>
    public static CompositeResult Composite(Plane16 planet, Plane16 moons) {
        if (planet.Width != moons.Width || planet.Height != moons.Height)
            throw new InvalidOperationException(
                $"The two stacks differ in size ({planet.Width}x{planet.Height} and {moons.Width}x{moons.Height}): the ROI changed between the recordings.");
        int w = planet.Width, h = planet.Height, n = w * h;
        int ch = Math.Max(planet.Channels, moons.Channels);

        var lp = Luminance(planet);
        var lm = Luminance(moons);
        var dp = ReadDisc(lp, w, h);
        var dm = ReadDisc(lm, w, h);
        if (!dp.Found) throw new InvalidOperationException("The planet stack does not show a disc.");
        if (!dm.Found) throw new InvalidOperationException("The moons stack does not show the planet.");

        // Register the moons stack on the planet stack by the disc centres.
        double sx = dp.CenterX - dm.CenterX, sy = dp.CenterY - dm.CenterY;

        // Per-channel backgrounds.
        double[] bgP = new double[ch], bgM = new double[ch];
        for (int c = 0; c < ch; c++) {
            bgP[c] = BackgroundAndNoise(ChannelPlane(planet, Math.Min(c, planet.Channels - 1))).Background;
            bgM[c] = BackgroundAndNoise(ChannelPlane(moons, Math.Min(c, moons.Channels - 1))).Background;
        }

        // Planet scale: the disc's 99.9th percentile to 0.95.
        var discVals = new List<float>();
        double thrP = dp.Background + 0.25 * (lp.Max() - dp.Background);
        for (int i = 0; i < n; i++) if (lp[i] >= thrP) discVals.Add(lp[i]);
        double planetTop = Math.Max(1, Percentile(discVals, 0.999) - dp.Background);
        double planetScale = 0.95 / planetTop;

        // Moons scale: the brightest moon to 0.85. Without a moon the long
        // stack is shown at the planet's scale times the exposure-free level
        // of its background, i.e. just its sky.
        var mr = ReadMoons(lm, w, h, dm);
        double moonsScale = mr.Found ? 0.85 / Math.Max(1, mr.Signal) : 0.85 / Math.Max(1, 50 * dm.Noise);

        // Mask: the planet's blob in the long stack above half of full scale
        // (the disc and its glow, but not a bright moon, which is a blob of
        // its own), plus the disc of the short stack, grown by a margin and
        // feathered.
        var lmShift = Shift(lm, w, h, sx, sy);
        double glow = dm.Background + 0.5 * (FullScale - dm.Background);
        var above = new bool[n];
        for (int i = 0; i < n; i++) above[i] = lmShift[i] >= glow || lp[i] >= thrP;
        var blob = LargestComponent(above, w, h);
        var mask = new float[n];
        for (int i = 0; i < n; i++) mask[i] = blob[i] ? 1f : 0f;
        int grow = (int)Math.Max(2, Math.Round(dp.Radius * 0.12));
        mask = Dilate(mask, w, h, grow);
        int feather = (int)Math.Max(2, Math.Round(dp.Radius * 0.10));
        mask = BoxBlur(BoxBlur(mask, w, h, feather), w, h, feather);

        var outp = new float[n * ch];
        for (int c = 0; c < ch; c++) {
            var pc = ChannelPlane(planet, Math.Min(c, planet.Channels - 1));
            var mc = Shift(ChannelPlane(moons, Math.Min(c, moons.Channels - 1)), w, h, sx, sy);
            for (int i = 0; i < n; i++) {
                double pv = Math.Clamp((pc[i] - bgP[c]) * planetScale, 0, 1);
                double mv = Math.Clamp((mc[i] - bgM[c]) * moonsScale, 0, 1);
                outp[c * n + i] = (float)(mask[i] * pv + (1 - mask[i]) * mv);
            }
        }
        return new CompositeResult(outp, w, h, ch, sx, sy, dp.Radius);
    }

    /// <summary>The largest 8-connected region of <paramref name="above"/>.</summary>
    public static bool[] LargestComponent(bool[] above, int w, int h) {
        var label = new int[above.Length];
        int next = 0, bestLabel = 0, bestSize = 0;
        var stack = new Stack<int>();
        for (int start = 0; start < above.Length; start++) {
            if (!above[start] || label[start] != 0) continue;
            next++;
            int size = 0;
            label[start] = next;
            stack.Push(start);
            while (stack.Count > 0) {
                int i = stack.Pop();
                size++;
                int x = i % w, y = i / w;
                for (int j = -1; j <= 1; j++)
                    for (int k = -1; k <= 1; k++) {
                        int xx = x + k, yy = y + j;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int q = yy * w + xx;
                        if (above[q] && label[q] == 0) { label[q] = next; stack.Push(q); }
                    }
            }
            if (size > bestSize) { bestSize = size; bestLabel = next; }
        }
        var o = new bool[above.Length];
        if (bestLabel != 0) for (int i = 0; i < o.Length; i++) o[i] = label[i] == bestLabel;
        return o;
    }

    private static float[] ChannelPlane(Plane16 p, int c) {
        int n = p.Width * p.Height;
        var o = new float[n];
        for (int i = 0; i < n; i++) o[i] = p.Data[c * n + i];
        return o;
    }

    private static float[] Dilate(float[] m, int w, int h, int r) {
        // Separable max: rows then columns.
        var t = new float[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                float v = 0;
                for (int i = Math.Max(0, x - r); i <= Math.Min(w - 1, x + r); i++) v = Math.Max(v, m[y * w + i]);
                t[y * w + x] = v;
            }
        var o = new float[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                float v = 0;
                for (int j = Math.Max(0, y - r); j <= Math.Min(h - 1, y + r); j++) v = Math.Max(v, t[j * w + x]);
                o[y * w + x] = v;
            }
        return o;
    }

    private static float[] BoxBlur(float[] m, int w, int h, int r) {
        var t = new float[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double s = 0; int k = 0;
                for (int i = Math.Max(0, x - r); i <= Math.Min(w - 1, x + r); i++) { s += m[y * w + i]; k++; }
                t[y * w + x] = (float)(s / k);
            }
        var o = new float[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double s = 0; int k = 0;
                for (int j = Math.Max(0, y - r); j <= Math.Min(h - 1, y + r); j++) { s += t[j * w + x]; k++; }
                o[y * w + x] = (float)(s / k);
            }
        return o;
    }
}
