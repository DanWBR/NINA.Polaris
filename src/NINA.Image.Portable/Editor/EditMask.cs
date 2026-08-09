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

namespace NINA.Image.Editor;

/// <summary>Which rule decides a pixel's mask coverage.</summary>
public enum MaskKind {
    /// <summary>No mask: edits apply everywhere.</summary>
    None = 0,
    /// <summary>A ramp on luminance. Everything at or above <c>High</c> is
    /// fully covered, everything at or below <c>Low</c> is untouched, and the
    /// span between them ramps. Invert it and you have a shadows mask.</summary>
    Luminance = 1,
    /// <summary>A band on luminance: full coverage between <c>Low</c> and
    /// <c>High</c>, falling off over <c>Feather</c> on both sides. The one to
    /// reach for when the target is midtones rather than an end of the
    /// histogram.</summary>
    Range = 2,
    /// <summary>Coverage the operator painted by hand, carried as a small
    /// run-length-encoded bitmap and scaled to the image.</summary>
    Painted = 3,
}

/// <summary>
/// Where an edit applies.
///
/// <para>The pipeline has no per-operation masking: several of its stages are
/// spatial (unsharp mask, median) and a kernel cannot be meaningfully clipped
/// per pixel without changing what it computes near the mask edge. So a masked
/// edit runs the ordinary pipeline over a copy and blends the two by coverage,
/// which is exact for every stage including the spatial ones and needs no
/// special case anywhere in <see cref="EditPipeline"/>.</para>
/// </summary>
/// <param name="Kind">Which rule builds the coverage.</param>
/// <param name="Low">Ramp start / band lower edge, in 0..1 display luminance.</param>
/// <param name="High">Ramp end / band upper edge, in 0..1 display luminance.</param>
/// <param name="Feather">Softness of the band edges, 0..1 of the luminance
/// range. Ignored by <see cref="MaskKind.Luminance"/>, whose softness is the
/// Low..High span itself.</param>
/// <param name="Invert">Swap covered and uncovered.</param>
/// <param name="Opacity">Ceiling on coverage, 0..1. Half opacity means the
/// masked edit is applied at half strength where the mask is full.</param>
/// <param name="Painted">Run-length-encoded 8-bit coverage, base64. See
/// <see cref="EncodeRle"/> for the format.</param>
/// <param name="PaintedWidth">Width of the painted bitmap before scaling.</param>
/// <param name="PaintedHeight">Height of the painted bitmap before scaling.</param>
public sealed record MaskParams(
    MaskKind Kind = MaskKind.None,
    double Low = 0.0,
    double High = 1.0,
    double Feather = 0.15,
    bool Invert = false,
    double Opacity = 1.0,
    string? Painted = null,
    int PaintedWidth = 0,
    int PaintedHeight = 0
) {
    /// <summary>True when this mask would leave every pixel fully covered, so
    /// the pipeline can skip the copy and the blend entirely.</summary>
    public bool IsNoOp =>
        Kind == MaskKind.None
        || (Kind == MaskKind.Painted
            && (string.IsNullOrEmpty(Painted) || PaintedWidth <= 0 || PaintedHeight <= 0));
}

/// <summary>Builds and applies <see cref="MaskParams"/> coverage.</summary>
public static class EditMask {

    /// <summary>Rec. 709 luma, the same weighting the pipeline's own luminance
    /// paths use, so a luminance mask agrees with what Clarity and Sharpen
    /// consider bright.</summary>
    private static double Luma(byte r, byte g, byte b) =>
        (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;

    /// <summary>Coverage per pixel, 0 = untouched, 255 = fully edited.
    /// <paramref name="buf"/> is the 8-bit interleaved source the mask is
    /// measured on (the image BEFORE the masked edits).</summary>
    public static byte[] Build(byte[] buf, int width, int height, int channels, MaskParams m) {
        var n = width * height;
        var mask = new byte[n];
        if (m == null || m.IsNoOp) {
            Array.Fill(mask, (byte)255);
            return mask;
        }

        var opacity = Math.Clamp(m.Opacity, 0.0, 1.0);

        if (m.Kind == MaskKind.Painted) {
            var painted = DecodeRle(m.Painted!, m.PaintedWidth * m.PaintedHeight);
            Resample(painted, m.PaintedWidth, m.PaintedHeight, mask, width, height);
        } else {
            // Low above High would invert the ramp silently; ordering them
            // means a slider dragged past its partner degrades to a hard edge
            // instead of turning the mask inside out.
            var lo = Math.Clamp(Math.Min(m.Low, m.High), 0.0, 1.0);
            var hi = Math.Clamp(Math.Max(m.Low, m.High), 0.0, 1.0);
            var feather = Math.Clamp(m.Feather, 0.0, 1.0);

            for (int i = 0, p = 0; i < n; i++, p += channels) {
                double y = channels == 3
                    ? Luma(buf[p], buf[p + 1], buf[p + 2])
                    : buf[p] / 255.0;
                double cov = m.Kind == MaskKind.Luminance
                    ? Ramp(y, lo, hi)
                    : Band(y, lo, hi, feather);
                mask[i] = (byte)Math.Clamp(Math.Round(cov * 255.0), 0, 255);
            }
        }

        if (m.Invert) {
            for (int i = 0; i < n; i++) mask[i] = (byte)(255 - mask[i]);
        }
        if (opacity < 1.0) {
            for (int i = 0; i < n; i++) mask[i] = (byte)Math.Round(mask[i] * opacity);
        }
        return mask;
    }

    /// <summary>Smoothstep from lo to hi. A linear ramp leaves a visible crease
    /// where it meets the flat ends; the cubic does not, and this is a mask
    /// whose whole job is to be invisible.</summary>
    private static double Ramp(double y, double lo, double hi) {
        if (hi - lo < 1e-9) return y >= hi ? 1.0 : 0.0;
        var t = Math.Clamp((y - lo) / (hi - lo), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Full coverage inside [lo, hi], falling off over
    /// <paramref name="feather"/> on each side.</summary>
    private static double Band(double y, double lo, double hi, double feather) {
        if (y >= lo && y <= hi) return 1.0;
        if (feather < 1e-9) return 0.0;
        var d = y < lo ? lo - y : y - hi;
        if (d >= feather) return 0.0;
        var t = 1.0 - d / feather;
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Bilinear scale of a coverage bitmap onto the image grid. The
    /// painted mask is deliberately stored small (brush strokes are smooth, and
    /// a full-resolution bitmap would dwarf the rest of the sidecar), so this
    /// runs on every apply.</summary>
    private static void Resample(byte[] src, int sw, int sh, byte[] dst, int dw, int dh) {
        if (sw <= 0 || sh <= 0) return;
        // Map destination pixel centres into source space, which is what keeps
        // the mask from drifting half a pixel toward the top-left at large
        // scale factors.
        double fx = (double)sw / dw, fy = (double)sh / dh;
        for (int y = 0; y < dh; y++) {
            double sy = (y + 0.5) * fy - 0.5;
            int y0 = (int)Math.Floor(sy);
            double wy = sy - y0;
            int y0c = Math.Clamp(y0, 0, sh - 1), y1c = Math.Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dw; x++) {
                double sx = (x + 0.5) * fx - 0.5;
                int x0 = (int)Math.Floor(sx);
                double wx = sx - x0;
                int x0c = Math.Clamp(x0, 0, sw - 1), x1c = Math.Clamp(x0 + 1, 0, sw - 1);
                double top = src[y0c * sw + x0c] * (1 - wx) + src[y0c * sw + x1c] * wx;
                double bot = src[y1c * sw + x0c] * (1 - wx) + src[y1c * sw + x1c] * wx;
                dst[y * dw + x] = (byte)Math.Clamp(Math.Round(top * (1 - wy) + bot * wy), 0, 255);
            }
        }
    }

    /// <summary>Blend the edited buffer back over the original by coverage:
    /// <c>out = original * (1 - m) + edited * m</c>. Writes into
    /// <paramref name="edited"/>.</summary>
    public static void Blend(byte[] edited, byte[] original, byte[] mask, int channels) {
        for (int i = 0, p = 0; i < mask.Length; i++, p += channels) {
            int m = mask[i];
            if (m == 255) continue;              // fully edited, nothing to do
            if (m == 0) {                         // untouched, take the original
                for (int c = 0; c < channels; c++) edited[p + c] = original[p + c];
                continue;
            }
            int inv = 255 - m;
            for (int c = 0; c < channels; c++) {
                // +127 rounds to nearest rather than truncating, which
                // otherwise darkens every partially covered pixel by up to one
                // level and shows up as a band along a soft mask edge.
                edited[p + c] = (byte)((original[p + c] * inv + edited[p + c] * m + 127) / 255);
            }
        }
    }

    /// <summary>Tint the covered area red, in place, so the operator can see
    /// where the mask is instead of inferring it from the result.
    ///
    /// <para>Rendered from the SAME coverage the blend uses rather than
    /// recomputed in the browser: a preview of a mask that disagrees with the
    /// mask is worse than no preview.</para></summary>
    public static void Overlay(byte[] buf, int channels, byte[] mask) {
        for (int i = 0, p = 0; i < mask.Length; i++, p += channels) {
            int m = mask[i];
            if (m == 0) continue;
            // Half-strength so structure stays readable underneath: the point
            // is to judge the mask against the image, not to hide the image.
            int a = m / 2;
            int inv = 255 - a;
            if (channels == 3) {
                buf[p]     = (byte)((buf[p] * inv + 255 * a + 127) / 255);
                buf[p + 1] = (byte)((buf[p + 1] * inv + 127) / 255);
                buf[p + 2] = (byte)((buf[p + 2] * inv + 127) / 255);
            } else {
                // Mono has no red to tint with, so brighten instead. Still
                // unambiguous, and it keeps the overlay working on a mono
                // frame rather than silently doing nothing.
                buf[p] = (byte)((buf[p] * inv + 255 * a + 127) / 255);
            }
        }
    }

    // ── run-length coding ────────────────────────────────────────────────
    //
    // A painted mask is mostly flat: long runs of 0 outside the strokes and of
    // 255 inside them, with soft edges in between. Pairs of (value, count) with
    // a 16-bit count take it from hundreds of kilobytes to a few, which is what
    // makes it reasonable to carry in the JSON sidecar next to the sliders.
    //
    // Wire format, base64 of: repeated [value:u8][count:u16 little-endian].
    // Runs longer than 65535 are split across pairs.

    public static string EncodeRle(byte[] data) {
        var outBytes = new List<byte>(Math.Max(16, data.Length / 8));
        int i = 0;
        while (i < data.Length) {
            byte v = data[i];
            int run = 1;
            while (i + run < data.Length && data[i + run] == v && run < 65535) run++;
            outBytes.Add(v);
            outBytes.Add((byte)(run & 0xFF));
            outBytes.Add((byte)((run >> 8) & 0xFF));
            i += run;
        }
        return Convert.ToBase64String(outBytes.ToArray());
    }

    /// <summary>Decode to exactly <paramref name="expected"/> bytes. A short
    /// stream leaves the tail at 0 and a long one is truncated: a corrupt or
    /// mismatched mask then costs coverage, never an exception in the middle of
    /// a render.</summary>
    public static byte[] DecodeRle(string base64, int expected) {
        var outBytes = new byte[Math.Max(0, expected)];
        if (string.IsNullOrEmpty(base64) || expected <= 0) return outBytes;
        byte[] raw;
        try { raw = Convert.FromBase64String(base64); } catch (FormatException) { return outBytes; }
        int w = 0;
        for (int i = 0; i + 2 < raw.Length; i += 3) {
            byte v = raw[i];
            int run = raw[i + 1] | (raw[i + 2] << 8);
            if (run <= 0) continue;
            int end = Math.Min(w + run, outBytes.Length);
            for (; w < end; w++) outBytes[w] = v;
            if (w >= outBytes.Length) break;
        }
        return outBytes;
    }
}
