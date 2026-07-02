// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using NINA.Image.ImageAnalysis;

namespace NINA.Image.Editor;

/// <summary>
/// First stage of the editor pipeline: turn the LINEAR image that arrives
/// from the camera / stacking (very dark, data crammed near black) into the
/// 8-bit display-referred buffer the rest of <see cref="EditPipeline"/>
/// operates on. This is the non-destructive "stretch" the operator adjusts
/// with the editor histogram handles.
///
/// Shared by the server (<c>ImageEditService</c>) and the WASM build so a
/// given <see cref="StretchParams"/> + linear source produce byte-for-byte
/// identical output in both compute modes.
///
/// Two-stage, color-preserving:
///   • Stage A (always) — the GraXpert auto-stretch (15% bg, 3σ) PER CHANNEL.
///     This is the neutral, white-balanced display base (each channel's
///     background lands at ~15% with its own midtone, so OSC data renders
///     neutral). Auto mode returns this base directly.
///   • Stage B (manual only) — the user's black/mid/white applied as ONE
///     linked MTF over the already-neutral base (same LUT for R/G/B). Because
///     the base is balanced, a linked adjustment can't introduce a colour
///     cast: it just brightens/clips uniformly. Identity at (0, 0.5, 1).
///
/// So the manual handles operate in DISPLAY space on the neutral base (like a
/// Lightroom tone curve), not on the raw linear channels — that's what keeps
/// the colour stable while you adjust. Shared by the server + WASM editor.
/// </summary>
public static class EditorStretch {
    /// <summary>
    /// Stretch a linear ushort source (mono, or plane-sequential R,G,B for
    /// 3 channels) to an 8-bit display buffer (mono, or RGB-interleaved).
    /// </summary>
    public static byte[] Apply(ushort[] linear, int width, int height, int channels,
                               int bitDepth, StretchParams? sp) {
        // Stage A: per-channel auto → neutral display base.
        byte[] outp = AutoBase(linear, width, height, channels, bitDepth);
        // Stage B: linked manual adjustment on top (identity when Auto / unset).
        // The curve is chosen by Mode: the classic black/mid/white MTF, or a
        // generalized-hyperbolic / asinh curve (both linked, colour-preserving).
        if (sp != null && !sp.Auto) {
            var lut = StretchLut(sp);
            for (int i = 0; i < outp.Length; i++) outp[i] = lut[outp[i]];
        }
        return outp;
    }

    /// <summary>Stage A: per-channel GraXpert auto-stretch to the neutral
    /// 8-bit base (RGB-interleaved, or mono).</summary>
    private static byte[] AutoBase(ushort[] linear, int width, int height, int channels, int bitDepth) {
        if (channels == 3) {
            int planeSize = width * height;
            var r = Slice(linear, 0, planeSize);
            var g = Slice(linear, planeSize, planeSize);
            var b = Slice(linear, planeSize * 2, planeSize);
            var rs = AutoStretch.Apply(r, width, height, bitDepth);
            var gs = AutoStretch.Apply(g, width, height, bitDepth);
            var bs = AutoStretch.Apply(b, width, height, bitDepth);
            var outp = new byte[planeSize * 3];
            for (int i = 0, j = 0; i < planeSize; i++, j += 3) {
                outp[j] = rs[i]; outp[j + 1] = gs[i]; outp[j + 2] = bs[i];
            }
            return outp;
        }
        return AutoStretch.Apply(linear, width, height, bitDepth);
    }

    /// <summary>
    /// Stage B LUT dispatcher: pick the display-space curve from the stretch
    /// Mode. "ghs"/"asinh" build a generalized-hyperbolic / arc-sinh LUT
    /// (identity when D = 0); anything else is the classic black/mid/white MTF.
    /// </summary>
    private static byte[] StretchLut(StretchParams sp) {
        var mode = (sp.Mode ?? "mtf").Trim().ToLowerInvariant();
        if (mode == "ghs" || mode == "asinh") {
            var type = HyperbolicStretch.ParseType(mode);
            // 256-entry curve over the already-stretched [0,1] display base.
            var curve = HyperbolicStretch.BuildLut(256, type, sp.B, sp.D, sp.LP, sp.SP, sp.HP, 0.0);
            var lut = new byte[256];
            for (int v = 0; v < 256; v++)
                lut[v] = (byte)Math.Clamp(Math.Round(curve[v] * 255.0), 0, 255);
            return lut;
        }
        return LinkedLut(sp.Black, sp.Mid, sp.White);
    }

    /// <summary>Stage B: a 256→256 display-space LUT for the linked
    /// black/mid/white MTF. (0, 0.5, 1) is the identity (= neutral base).</summary>
    private static byte[] LinkedLut(double black, double mid, double white) {
        black = Math.Clamp(black, 0.0, 1.0) * 255.0;
        white = Math.Clamp(white, 0.0, 1.0) * 255.0;
        if (white <= black) white = black + 1.0;
        mid = Math.Clamp(mid, 0.001, 0.999);
        double inv = 1.0 / (white - black);
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) {
            double x = (v - black) * inv;
            x = x < 0 ? 0 : x > 1 ? 1 : x;
            lut[v] = (byte)(Mtf(x, mid) * 255.0 + 0.5);
        }
        return lut;
    }

    // PixInsight midtones transfer function. f(0)=0, f(1)=1, f(m)=0.5.
    private static double Mtf(double x, double m) {
        if (x <= 0) return 0; if (x >= 1) return 1;
        if (m <= 0) return 1; if (m >= 1) return 0;
        return ((m - 1.0) * x) / ((2.0 * m - 1.0) * x - m);
    }

    /// <summary>
    /// Seed the handles when switching Auto → manual. With the two-stage model
    /// the neutral base IS the auto result, so the no-jump manual seed is the
    /// identity (0, 0.5, 1): manual-at-seed renders exactly like Auto.
    /// </summary>
    public static StretchParams ComputeAuto(ushort[] linear, int width, int height,
                                            int channels, int bitDepth) {
        _ = linear; _ = width; _ = height; _ = channels; _ = bitDepth;
        return new StretchParams(Auto: false, Black: 0.0, Mid: 0.5, White: 1.0);
    }

    private static ushort[] Slice(ushort[] src, int start, int count) {
        var dst = new ushort[count];
        Array.Copy(src, start, dst, 0, count);
        return dst;
    }
}
