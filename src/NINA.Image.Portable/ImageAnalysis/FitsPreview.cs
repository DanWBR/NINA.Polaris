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
using NINA.Core.Enum;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// The FILES viewer's FITS preview up to the pixels: debayer, box-average
/// down towards the requested size, auto-stretch each channel, interleave
/// to RGBA. The server's FitsThumbnailer runs the same steps and then hands
/// the result to Skia for the final resize and the JPEG; the browser-wasm
/// module stops at the RGBA buffer and lets a canvas do the rest, so a file
/// looks the same whether the host or the phone rendered it.
/// </summary>
public static class FitsPreview {

    /// <summary>Box-average a plane-sequential ushort buffer down by an integer
    /// factor so that its longest side is just above <paramref name="targetMax"/>.
    /// The factor is floored, so the result never drops below the target and a
    /// caller that resizes afterwards still lands on the exact size. Returns the
    /// same array and dimensions when no reduction is needed (the source is
    /// already small, or targetMax is 0 or int.MaxValue for full resolution).</summary>
    public static (ushort[] pix, int w, int h) Downsample(
            ushort[] src, int width, int height, int planes, int targetMax) {
        int f = BinFactor(width, height, targetMax);
        if (f <= 1) return (src, width, height);
        int dw = (width + f - 1) / f, dh = (height + f - 1) / f;
        var dst = new ushort[dw * dh * planes];
        for (int pl = 0; pl < planes; pl++)
            BinInto(src, pl * width * height, width, height, f, dst, pl * dw * dh, dw, dh);
        return (dst, dw, dh);
    }

    /// <summary>The integer binning factor <see cref="Downsample"/> uses.</summary>
    static int BinFactor(int width, int height, int targetMax)
        => targetMax > 0 ? Math.Max(1, Math.Max(width, height) / targetMax) : 1;

    /// <summary>Box-average one plane of <paramref name="src"/>, starting at
    /// <paramref name="sOff"/>, into <paramref name="dst"/> at <paramref name="dOff"/>.</summary>
    static void BinInto(ushort[] src, int sOff, int width, int height, int f,
            ushort[] dst, int dOff, int dw, int dh) {
        for (int by = 0; by < dh; by++) {
            int y0 = by * f, y1 = Math.Min(y0 + f, height);
            int drow = dOff + by * dw;
            for (int bx = 0; bx < dw; bx++) {
                int x0 = bx * f, x1 = Math.Min(x0 + f, width);
                uint sum = 0; int n = 0;
                for (int y = y0; y < y1; y++) {
                    int srow = sOff + y * width;
                    for (int x = x0; x < x1; x++) { sum += src[srow + x]; n++; }
                }
                dst[drow + bx] = (ushort)(n > 0 ? sum / (uint)n : 0);
            }
        }
    }

    /// <summary>One plane of <paramref name="src"/> binned by <paramref name="f"/>.
    /// With f = 1 a plane that already fills the array is returned as it is,
    /// and any other plane is copied out.</summary>
    static ushort[] BinPlane(ushort[] src, int sOff, int width, int height, int f, out int dw, out int dh) {
        dw = (width + f - 1) / f;
        dh = (height + f - 1) / f;
        if (f <= 1 && sOff == 0 && src.Length == width * height) return src;
        var dst = new ushort[dw * dh];
        if (f <= 1) Array.Copy(src, sOff, dst, 0, dst.Length);
        else BinInto(src, sOff, width, height, f, dst, 0, dw, dh);
        return dst;
    }

    /// <summary>The effective Bayer pattern: the override when it names a real
    /// pattern, otherwise what the file declares.</summary>
    public static BayerPatternEnum EffectivePattern(BayerPatternEnum declared, BayerPatternEnum? overridePattern)
        => overridePattern ?? declared;

    /// <summary>
    /// Render the auto-stretched preview of a decoded frame as RGBA8888.
    /// <paramref name="channels"/> is 1 for a mono or CFA buffer and 3 for a
    /// plane-sequential RGB cube. A mono buffer with a Bayer pattern (the
    /// file's or <paramref name="bayerOverride"/>) is debayered first. When the
    /// preview is at most half the sensor size, the debayer is a 2x2 superpixel
    /// to save memory; otherwise it is bilinear, as in the server preview. The
    /// output is at least
    /// <paramref name="maxDim"/> on its long side when the source is larger
    /// (integer binning, see <see cref="Downsample"/>), so the caller scales the
    /// last step.
    /// </summary>
    public static (byte[] rgba, int width, int height) RenderRgba(
            ushort[] data, int width, int height, int channels, int bitDepth,
            BayerPatternEnum declaredPattern, BayerPatternEnum? bayerOverride, int maxDim) {
        int plane = width * height;
        var pattern = EffectivePattern(declaredPattern, bayerOverride);
        bool color = channels >= 3 && data.Length >= plane * 3;
        bool cfa = !color && pattern != BayerPatternEnum.None && pattern != BayerPatternEnum.Auto;

        if (color || cfa) {
            // Each plane is binned on its own straight from where it lives, so
            // that the full-resolution frame is never copied into a second
            // interleaved buffer. On a phone the WASM heap is the limit.
            ushort[] sr = data, sg = data, sb = data;
            int og = plane, ob = plane * 2, sw = width, sh = height;
            if (cfa) {
                BayerDebayer.Channels ch;
                if (BinFactor(width, height, maxDim) >= 2) {
                    // The preview is half the sensor size or less, so a 2x2
                    // superpixel loses nothing and needs a quarter of the memory.
                    (ch, sw, sh) = BayerDebayer.Superpixel(data, width, height, pattern);
                } else {
                    ch = BayerDebayer.Bilinear(data, width, height, pattern);
                }
                sr = ch.R; sg = ch.G; sb = ch.B;
                og = ob = 0;
            }
            int f = BinFactor(sw, sh, maxDim);
            var r = BinPlane(sr, 0, sw, sh, f, out int cw, out int chh);
            var g = BinPlane(sg, og, sw, sh, f, out _, out _);
            var b = BinPlane(sb, ob, sw, sh, f, out _, out _);
            return StretchRgb(r, g, b, cw, chh, bitDepth);
        }

        var (pix, w, h) = Downsample(data, width, height, 1, maxDim);
        var gray = AutoStretch.Apply(pix, w, h, bitDepth);
        var outMono = new byte[w * h * 4];
        for (int i = 0, o = 0; i < gray.Length; i++, o += 4) {
            byte v = gray[i];
            outMono[o] = v; outMono[o + 1] = v; outMono[o + 2] = v; outMono[o + 3] = 255;
        }
        return (outMono, w, h);
    }

    static (byte[] rgba, int width, int height) StretchRgb(
            ushort[] r, ushort[] g, ushort[] b, int cw, int chh, int bitDepth) {
        int n = cw * chh;
        // Each channel is stretched on its own, as the server preview does, so
        // that an OSC frame or an RGB master keeps a natural colour balance.
        var pr = AutoStretch.ComputeAutoStretchParams(r, cw, chh, bitDepth);
        var pg = AutoStretch.ComputeAutoStretchParams(g, cw, chh, bitDepth);
        var pb = AutoStretch.ComputeAutoStretchParams(b, cw, chh, bitDepth);
        var rs = AutoStretch.ApplyManual(r, cw, chh, pr.Black, pr.Mid, pr.White, bitDepth);
        var gs = AutoStretch.ApplyManual(g, cw, chh, pg.Black, pg.Mid, pg.White, bitDepth);
        var bs = AutoStretch.ApplyManual(b, cw, chh, pb.Black, pb.Mid, pb.White, bitDepth);
        var rgba = new byte[n * 4];
        for (int i = 0, o = 0; i < n; i++, o += 4) {
            rgba[o] = rs[i]; rgba[o + 1] = gs[i]; rgba[o + 2] = bs[i]; rgba[o + 3] = 255;
        }
        return (rgba, cw, chh);
    }

    /// <summary>Parse the viewer's Bayer dropdown value. Returns null for an
    /// empty or unknown value, which means "use the file's pattern".</summary>
    public static BayerPatternEnum? ParseBayer(string? value) => (value ?? "").Trim().ToUpperInvariant() switch {
        "RGGB" => BayerPatternEnum.RGGB,
        "BGGR" => BayerPatternEnum.BGGR,
        "GBRG" => BayerPatternEnum.GBRG,
        "GRBG" => BayerPatternEnum.GRBG,
        _ => null
    };
}
