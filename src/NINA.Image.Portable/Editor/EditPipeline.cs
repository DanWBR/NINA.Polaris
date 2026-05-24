using NINA.Image.ImageAnalysis;

namespace NINA.Image.Editor;

/// <summary>
/// Apply an EditParams to an 8-bit pixel buffer. Single-pass over the
/// canonical pipeline ordering (white-balance → exposure → contrast →
/// highlights/shadows → whites/blacks → curves → vibrance/saturation →
/// hue → clarity → dehaze → texture → sharpen → noise reduce → vignette
/// → crop+resize). Each step short-circuits when its input section is
/// at defaults so a slider that hasn't been touched costs zero work.
///
/// The pipeline operates on 8-bit byte buffers — by the time the user is
/// in the editor the image has already been auto-stretched into a viewable
/// 8-bit space (server does the FITS → byte[] preview render once per
/// session). That keeps the pipeline cheap to run on every slider drag
/// and identical in behaviour between WASM and server (no float-precision
/// differences). The trade-off is reduced headroom for extreme exposure
/// shifts; for that the user should re-render the stretch from the FITS.
///
/// Input/output buffers are <em>channels-interleaved</em>: BGR-BGR-BGR for
/// 3-channel, plain byte for 1-channel. This matches Skia's default RGB888
/// layout so server-side decode → pipeline → Skia encode is one allocation.
/// </summary>
public static class EditPipeline {

    /// <summary>
    /// Apply edits in-place. <paramref name="channels"/> is 1 (mono) or 3
    /// (RGB interleaved). Returns the same buffer for chaining. Crop and
    /// resize are NOT applied here — they change dimensions and are handled
    /// separately by the caller (so the preview keeps dimensions stable
    /// while sliders move). Use <see cref="ApplyCropResize"/> for those.
    /// </summary>
    public static byte[] Apply(byte[] buf, int width, int height, int channels, EditParams p) {
        if (p == null) return buf;

        // ── 1. White balance (RGB only — no-op for mono)
        if (channels == 3 && p.WhiteBalance != null && !p.WhiteBalance.IsDefault) {
            var (rG, gG, bG) = ColorSpace.TempTintToGain(p.WhiteBalance.TempK, p.WhiteBalance.Tint);
            for (int i = 0; i < buf.Length; i += 3) {
                buf[i]     = Clamp8(buf[i]     * rG);
                buf[i + 1] = Clamp8(buf[i + 1] * gG);
                buf[i + 2] = Clamp8(buf[i + 2] * bG);
            }
        }

        // ── 2-6. Light: build a single combined LUT for exposure, contrast,
        //         highlights, shadows, whites, blacks. Cheaper than 6 passes
        //         and the math composes cleanly in tone space.
        if (p.Light != null && !p.Light.IsDefault) {
            var lut = BuildLightLut(p.Light);
            ApplyLut(buf, lut);
        }

        // ── 7. Tone curves (per-channel first, then master)
        if (p.ToneCurve != null && !p.ToneCurve.IsDefault) {
            if (channels == 3) {
                if (p.ToneCurve.R != null)
                    ApplyLutChannel(buf, ToneCurveLut(p.ToneCurve.R), 0, 3);
                if (p.ToneCurve.G != null)
                    ApplyLutChannel(buf, ToneCurveLut(p.ToneCurve.G), 1, 3);
                if (p.ToneCurve.B != null)
                    ApplyLutChannel(buf, ToneCurveLut(p.ToneCurve.B), 2, 3);
            }
            if (p.ToneCurve.Rgb != null) {
                ApplyLut(buf, ToneCurveLut(p.ToneCurve.Rgb));
            }
        }

        // ── 8-9. Colour (vibrance / saturation / hue) — RGB only
        if (channels == 3 && p.Color != null && !p.Color.IsDefault) {
            ApplyColor(buf, p.Color);
        }

        // ── 10. Clarity (large-radius USM on luminance)
        if (p.Effects != null && Math.Abs(p.Effects.Clarity) > 1e-4) {
            ApplyLocalContrast(buf, width, height, channels,
                radius: (int)Math.Max(8, width / 80),
                amount: p.Effects.Clarity * 0.5);
        }

        // ── 11. Dehaze (global contrast + saturation boost weighted by haze)
        if (p.Effects != null && Math.Abs(p.Effects.Dehaze) > 1e-4) {
            ApplyDehaze(buf, channels, p.Effects.Dehaze);
        }

        // ── 12. Texture (mid-radius USM)
        if (p.Effects != null && Math.Abs(p.Effects.Texture) > 1e-4) {
            ApplyLocalContrast(buf, width, height, channels,
                radius: 3,
                amount: p.Effects.Texture * 0.6);
        }

        // ── 13. Sharpen (small-radius USM)
        if (p.Detail != null && Math.Abs(p.Detail.SharpenAmount) > 1e-4) {
            ApplyLocalContrast(buf, width, height, channels,
                radius: Math.Max(1, (int)Math.Round(p.Detail.SharpenRadius)),
                amount: p.Detail.SharpenAmount,
                thresholdAdu: p.Detail.SharpenThreshold);
        }

        // ── 14. Noise reduction (3x3 median on luminance)
        if (p.Detail != null && p.Detail.NoiseReduce > 1e-4) {
            ApplyMedian(buf, width, height, channels, p.Detail.NoiseReduce);
        }

        // ── 15. Vignette (radial multiply)
        if (p.Effects != null && Math.Abs(p.Effects.VignetteAmount) > 1e-4) {
            ApplyVignette(buf, width, height, channels,
                p.Effects.VignetteAmount, p.Effects.VignetteFeather);
        }

        // ── 8.5. Final hue rotation (applied after vibrance/sat so the
        //        rotation acts on the final colour palette)
        // (already done inside ApplyColor — kept here for ordering doc)
        return buf;
    }

    /// <summary>
    /// Crop + resize step. Returns a new buffer (dimensions change). Caller
    /// passes the channel count so it can interleave correctly. Resize uses
    /// bilinear (good enough for screen preview + matches ImageResampler
    /// already in the project).
    /// </summary>
    public static (byte[] data, int width, int height) ApplyCropResize(
        byte[] buf, int width, int height, int channels,
        CropParams? crop, int? targetWidth, int? targetHeight) {

        int x0 = 0, y0 = 0, w = width, h = height;
        if (crop != null) {
            x0 = Math.Clamp(crop.X, 0, width - 1);
            y0 = Math.Clamp(crop.Y, 0, height - 1);
            w = Math.Clamp(crop.Width, 1, width - x0);
            h = Math.Clamp(crop.Height, 1, height - y0);
        }

        // Tight crop pass (allocate cropped buffer).
        byte[] cropped;
        if (x0 == 0 && y0 == 0 && w == width && h == height) {
            cropped = buf;
        } else {
            cropped = new byte[w * h * channels];
            for (int row = 0; row < h; row++) {
                int srcOff = ((y0 + row) * width + x0) * channels;
                int dstOff = row * w * channels;
                Buffer.BlockCopy(buf, srcOff, cropped, dstOff, w * channels);
            }
        }

        // Resize pass (bilinear).
        int tw = targetWidth ?? w;
        int th = targetHeight ?? h;
        if (tw == w && th == h) return (cropped, w, h);

        var resized = new byte[tw * th * channels];
        double sx = (double)w / tw;
        double sy = (double)h / th;
        for (int ty = 0; ty < th; ty++) {
            double fy = (ty + 0.5) * sy - 0.5;
            int iy = (int)Math.Floor(fy);
            double dy = fy - iy;
            int iy0 = Math.Clamp(iy, 0, h - 1);
            int iy1 = Math.Clamp(iy + 1, 0, h - 1);

            for (int tx = 0; tx < tw; tx++) {
                double fx = (tx + 0.5) * sx - 0.5;
                int ix = (int)Math.Floor(fx);
                double dx = fx - ix;
                int ix0 = Math.Clamp(ix, 0, w - 1);
                int ix1 = Math.Clamp(ix + 1, 0, w - 1);

                for (int c = 0; c < channels; c++) {
                    double a = cropped[(iy0 * w + ix0) * channels + c];
                    double b = cropped[(iy0 * w + ix1) * channels + c];
                    double cc = cropped[(iy1 * w + ix0) * channels + c];
                    double d = cropped[(iy1 * w + ix1) * channels + c];
                    double top = a + (b - a) * dx;
                    double bot = cc + (d - cc) * dx;
                    resized[(ty * tw + tx) * channels + c] = Clamp8(top + (bot - top) * dy);
                }
            }
        }
        return (resized, tw, th);
    }

    // ───────────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────────

    private static byte[] BuildLightLut(LightParams light) {
        // Compose: exposure (multiply in linear-ish), then bend the curve
        // for contrast/highlights/shadows/whites/blacks.
        // For 8-bit input we treat values as gamma-ish (sRGB-encoded) —
        // not strictly linear, but matches Lightroom's behaviour where
        // sliders work on the *display* tone rather than scene-referred
        // radiance.
        double expGain = Math.Pow(2, light.Exposure);
        // Contrast slider -1..1. Positive lerps the value toward a
        // smoothstep S-curve (3v²-2v³) — pulls below-mid down, above-mid
        // up, preserving endpoints. Negative lerps toward a flatter
        // midline (compresses dynamic range without clipping).
        double contrastK = light.Contrast;
        // Highlights / Shadows / Whites / Blacks: each anchors a region
        // and lifts/cuts. Map -1..1 to gentle offsets that compose without
        // overshooting at extremes.
        double hi = light.Highlights;
        double sh = light.Shadows;
        double wh = light.Whites * 0.5;
        double bl = light.Blacks * 0.5;

        var lut = new byte[256];
        for (int i = 0; i < 256; i++) {
            double v = i / 255.0;
            // Exposure
            v *= expGain;
            // Contrast: lerp toward smoothstep S-curve (positive k) or
            // toward a flattened midline (negative k). Endpoint-preserving.
            if (Math.Abs(contrastK) > 1e-4) {
                double vClamped = Math.Clamp(v, 0, 1);
                if (contrastK > 0) {
                    double s = vClamped * vClamped * (3 - 2 * vClamped);
                    v = vClamped + (s - vClamped) * contrastK;
                } else {
                    double midline = 0.5 + (vClamped - 0.5) * 0.5;
                    v = vClamped + (midline - vClamped) * (-contrastK);
                }
            }
            // Highlights: pull values > 0.5 toward 0.5 when hi<0, away when hi>0
            if (Math.Abs(hi) > 1e-4 && v > 0.5) {
                double t = (v - 0.5) * 2;       // 0..1
                v = 0.5 + (v - 0.5) * (1 + hi * 0.5) - hi * 0.15 * t * t;
            }
            // Shadows: mirror
            if (Math.Abs(sh) > 1e-4 && v < 0.5) {
                double t = (0.5 - v) * 2;       // 0..1
                v = 0.5 - (0.5 - v) * (1 - sh * 0.5) + sh * 0.15 * t * t;
            }
            // Whites: lift the top end
            if (Math.Abs(wh) > 1e-4) v += wh * Math.Max(0, v - 0.7) * 1.5;
            // Blacks: push down the low end
            if (Math.Abs(bl) > 1e-4) v += bl * Math.Max(0, 0.3 - v) * 1.5;

            lut[i] = Clamp8(v * 255);
        }
        return lut;
    }

    private static byte[] ToneCurveLut(IReadOnlyList<CurvePoint> pts) {
        var tuples = pts.Select(p => (p.X, p.Y)).ToList();
        return ToneCurve.Build(tuples);
    }

    private static void ApplyLut(byte[] buf, byte[] lut) {
        for (int i = 0; i < buf.Length; i++) buf[i] = lut[buf[i]];
    }

    private static void ApplyLutChannel(byte[] buf, byte[] lut, int channelOffset, int stride) {
        for (int i = channelOffset; i < buf.Length; i += stride) {
            buf[i] = lut[buf[i]];
        }
    }

    private static void ApplyColor(byte[] buf, ColorParams c) {
        double satMul = 1 + c.Saturation;
        // Vibrance acts on the *gap* to full saturation — already-saturated
        // pixels move less than dull ones. Strength ~0.5 maps -1..1 well.
        double vibStrength = c.Vibrance;
        double hueDelta = c.Hue;

        for (int i = 0; i < buf.Length; i += 3) {
            double r = buf[i] / 255.0;
            double g = buf[i + 1] / 255.0;
            double b = buf[i + 2] / 255.0;

            var (h, s, l) = ColorSpace.RgbToHsl(r, g, b);

            // Vibrance: scale saturation by (1 - s) so highly-saturated
            // pixels are less affected.
            if (Math.Abs(vibStrength) > 1e-4) {
                s += vibStrength * (1 - s) * 0.5;
                s = Math.Clamp(s, 0, 1);
            }
            // Saturation: flat scale
            if (Math.Abs(c.Saturation) > 1e-4) {
                s = Math.Clamp(s * satMul, 0, 1);
            }
            // Hue rotation
            if (Math.Abs(hueDelta) > 1e-4) {
                h += hueDelta;
            }

            var (nr, ng, nb) = ColorSpace.HslToRgb(h, s, l);
            buf[i]     = Clamp8(nr * 255);
            buf[i + 1] = Clamp8(ng * 255);
            buf[i + 2] = Clamp8(nb * 255);
        }
    }

    private static void ApplyLocalContrast(byte[] buf, int width, int height, int channels,
                                            int radius, double amount, int thresholdAdu = 0) {
        // Build a luminance plane, blur it, compute the diff, add back.
        // For RGB we apply the diff per-channel scaled by the same factor
        // so colour isn't shifted.
        var lum = new byte[width * height];
        if (channels == 1) {
            Array.Copy(buf, lum, lum.Length);
        } else {
            for (int i = 0, j = 0; i < buf.Length; i += 3, j++) {
                lum[j] = Clamp8(0.2126 * buf[i] + 0.7152 * buf[i + 1] + 0.0722 * buf[i + 2]);
            }
        }
        var blurred = BoxBlur(lum, width, height, radius);

        for (int j = 0; j < lum.Length; j++) {
            int diff = lum[j] - blurred[j];
            if (thresholdAdu > 0 && Math.Abs(diff) < thresholdAdu) continue;
            double boost = amount * diff;
            if (channels == 1) {
                buf[j] = Clamp8(buf[j] + boost);
            } else {
                int o = j * 3;
                buf[o]     = Clamp8(buf[o]     + boost);
                buf[o + 1] = Clamp8(buf[o + 1] + boost);
                buf[o + 2] = Clamp8(buf[o + 2] + boost);
            }
        }
    }

    private static void ApplyDehaze(byte[] buf, int channels, double amount) {
        // Cheap: stretch each channel by pulling toward black using
        // a per-channel "haze floor" estimated as the channel min in
        // an 8x8 grid of samples. Then boost saturation slightly.
        // Real dehaze (He et al. 2009) needs a dark-channel pass per
        // pixel; this approximation is good enough for editor UX.
        double pullStrength = Math.Clamp(amount, -1, 1) * 0.4;
        for (int c = 0; c < (channels == 1 ? 1 : 3); c++) {
            int min = 255;
            int max = 0;
            int stride = channels;
            for (int i = c; i < buf.Length; i += stride) {
                if (buf[i] < min) min = buf[i];
                if (buf[i] > max) max = buf[i];
            }
            if (max <= min) continue;
            double black = min + (max - min) * 0.05 * pullStrength;
            double white = max - (max - min) * 0.02 * pullStrength;
            if (white <= black) continue;
            double scale = 255.0 / (white - black);
            for (int i = c; i < buf.Length; i += stride) {
                double v = (buf[i] - black) * scale;
                buf[i] = Clamp8(v);
            }
        }
    }

    private static void ApplyMedian(byte[] buf, int width, int height, int channels, double strength) {
        // 3x3 median on luminance plane; blend with original by strength.
        // Conservative on purpose (the user has GraXpert for the heavy lifting).
        if (channels == 1) {
            var src = (byte[])buf.Clone();
            var nbrs = new byte[9];
            for (int y = 1; y < height - 1; y++) {
                for (int x = 1; x < width - 1; x++) {
                    int k = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            nbrs[k++] = src[(y + dy) * width + (x + dx)];
                    Array.Sort(nbrs);
                    int o = y * width + x;
                    buf[o] = Clamp8(src[o] * (1 - strength) + nbrs[4] * strength);
                }
            }
        } else {
            // For RGB, median each channel separately (acceptable speed for v1).
            for (int c = 0; c < 3; c++) {
                var plane = new byte[width * height];
                for (int i = 0; i < plane.Length; i++) plane[i] = buf[i * 3 + c];
                var src = (byte[])plane.Clone();
                var nbrs = new byte[9];
                for (int y = 1; y < height - 1; y++) {
                    for (int x = 1; x < width - 1; x++) {
                        int k = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                nbrs[k++] = src[(y + dy) * width + (x + dx)];
                        Array.Sort(nbrs);
                        int o = y * width + x;
                        plane[o] = Clamp8(src[o] * (1 - strength) + nbrs[4] * strength);
                    }
                }
                for (int i = 0; i < plane.Length; i++) buf[i * 3 + c] = plane[i];
            }
        }
    }

    private static void ApplyVignette(byte[] buf, int width, int height, int channels,
                                      double amount, double feather) {
        double cx = width * 0.5;
        double cy = height * 0.5;
        double maxR = Math.Sqrt(cx * cx + cy * cy);
        double inner = Math.Clamp(feather, 0, 1) * maxR;
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                double dx = x - cx;
                double dy = y - cy;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double t = r <= inner ? 0 : (r - inner) / (maxR - inner);
                t = Math.Clamp(t, 0, 1);
                // amount: -1 darkens corners to ~0, +1 brightens to ~2x
                double m = 1 + amount * t * (amount < 0 ? 1 : 1);
                if (amount < 0) m = 1 + amount * t;  // 1 → 1+amount (e.g. -1 → 0)
                else            m = 1 + amount * t;  // 1 → 1+amount (e.g. +1 → 2)
                int idx = (y * width + x) * channels;
                for (int c = 0; c < channels; c++) {
                    buf[idx + c] = Clamp8(buf[idx + c] * m);
                }
            }
        }
    }

    /// <summary>
    /// Fast separable box blur, repeated 3 times = ~Gaussian. Used by
    /// clarity / texture / sharpen for the blur kernel. O(N) per pass
    /// regardless of radius.
    /// </summary>
    private static byte[] BoxBlur(byte[] src, int width, int height, int radius) {
        if (radius < 1) return (byte[])src.Clone();
        var buf1 = new byte[src.Length];
        var buf2 = new byte[src.Length];
        Array.Copy(src, buf1, src.Length);
        for (int pass = 0; pass < 3; pass++) {
            BoxBlurH(buf1, buf2, width, height, radius);
            BoxBlurV(buf2, buf1, width, height, radius);
        }
        return buf1;
    }

    private static void BoxBlurH(byte[] src, byte[] dst, int width, int height, int r) {
        double iarr = 1.0 / (r + r + 1);
        for (int y = 0; y < height; y++) {
            int ti = y * width;
            int li = ti;
            int ri = ti + r;
            int fv = src[ti];
            int lv = src[ti + width - 1];
            int val = (r + 1) * fv;
            for (int j = 0; j < r; j++) val += src[ti + j];
            for (int j = 0; j <= r; j++) {
                val += src[ri++] - fv;
                dst[ti++] = Clamp8(val * iarr);
            }
            for (int j = r + 1; j < width - r; j++) {
                val += src[ri++] - src[li++];
                dst[ti++] = Clamp8(val * iarr);
            }
            for (int j = width - r; j < width; j++) {
                val += lv - src[li++];
                dst[ti++] = Clamp8(val * iarr);
            }
        }
    }

    private static void BoxBlurV(byte[] src, byte[] dst, int width, int height, int r) {
        double iarr = 1.0 / (r + r + 1);
        for (int x = 0; x < width; x++) {
            int ti = x;
            int li = ti;
            int ri = ti + r * width;
            int fv = src[ti];
            int lv = src[ti + width * (height - 1)];
            int val = (r + 1) * fv;
            for (int j = 0; j < r; j++) val += src[ti + j * width];
            for (int j = 0; j <= r; j++) {
                val += src[ri] - fv; ri += width;
                dst[ti] = Clamp8(val * iarr); ti += width;
            }
            for (int j = r + 1; j < height - r; j++) {
                val += src[ri] - src[li]; ri += width; li += width;
                dst[ti] = Clamp8(val * iarr); ti += width;
            }
            for (int j = height - r; j < height; j++) {
                val += lv - src[li]; li += width;
                dst[ti] = Clamp8(val * iarr); ti += width;
            }
        }
    }

    private static byte Clamp8(double v) {
        if (v <= 0) return 0;
        if (v >= 255) return 255;
        return (byte)Math.Round(v);
    }
}
