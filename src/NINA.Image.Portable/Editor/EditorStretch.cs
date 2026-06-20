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
/// Auto mode reuses the GraXpert auto-stretch (15% bg, 3σ) PER CHANNEL, which
/// also acts as a rough white balance on OSC data (the historical load-time
/// behaviour). Manual mode applies the user's linked black/mid/white but keeps
/// each channel anchored to its own auto black point (relative to green), so
/// the per-channel white balance is preserved and dragging the handles doesn't
/// introduce a colour cast.
/// </summary>
public static class EditorStretch {
    /// <summary>
    /// Stretch a linear ushort source (mono, or plane-sequential R,G,B for
    /// 3 channels) to an 8-bit display buffer (mono, or RGB-interleaved).
    /// </summary>
    public static byte[] Apply(ushort[] linear, int width, int height, int channels,
                               int bitDepth, StretchParams? sp) {
        bool auto = sp == null || sp.Auto;
        if (channels == 3) {
            int planeSize = width * height;
            var r = Slice(linear, 0, planeSize);
            var g = Slice(linear, planeSize, planeSize);
            var b = Slice(linear, planeSize * 2, planeSize);
            byte[] rs, gs, bs;
            if (auto) {
                rs = AutoStretch.Apply(r, width, height, bitDepth);
                gs = AutoStretch.Apply(g, width, height, bitDepth);
                bs = AutoStretch.Apply(b, width, height, bitDepth);
            } else {
                // Linked manual stretch that PRESERVES the per-channel white
                // balance. A single black/mid/white applied identically to R,
                // G, B reveals the raw OSC colour cast (the R/G/B backgrounds
                // sit at different levels), so adjusting the handles visibly
                // shifts colour. Instead, anchor each channel's black to its
                // own auto black point relative to green: at the seed point
                // (handles come from green) each channel uses its own auto
                // black — identical to the Auto/neutral view, so there's no
                // colour jump — and as the user drags the linked handles all
                // three move together, keeping the background neutral.
                double gB = AutoStretch.ComputeAutoStretchParams(g, width, height, bitDepth).Black;
                double rOff = AutoStretch.ComputeAutoStretchParams(r, width, height, bitDepth).Black - gB;
                double bOff = AutoStretch.ComputeAutoStretchParams(b, width, height, bitDepth).Black - gB;
                rs = AutoStretch.ApplyManual(r, width, height, sp!.Black + rOff, sp.Mid, sp.White, bitDepth);
                gs = AutoStretch.ApplyManual(g, width, height, sp.Black, sp.Mid, sp.White, bitDepth);
                bs = AutoStretch.ApplyManual(b, width, height, sp.Black + bOff, sp.Mid, sp.White, bitDepth);
            }
            var outp = new byte[planeSize * 3];
            for (int i = 0, j = 0; i < planeSize; i++, j += 3) {
                outp[j] = rs[i]; outp[j + 1] = gs[i]; outp[j + 2] = bs[i];
            }
            return outp;
        }
        // Mono
        return auto
            ? AutoStretch.Apply(linear, width, height, bitDepth)
            : AutoStretch.ApplyManual(linear, width, height, sp!.Black, sp.Mid, sp.White, bitDepth);
    }

    /// <summary>
    /// Compute the auto black/mid/white for seeding the handles when the user
    /// switches from Auto to manual, so the image doesn't jump. For RGB we use
    /// the green plane as the luminance proxy (matches how STF tools seed a
    /// linked stretch from the dominant channel).
    /// </summary>
    public static StretchParams ComputeAuto(ushort[] linear, int width, int height,
                                            int channels, int bitDepth) {
        var src = channels == 3 ? Slice(linear, width * height, width * height) : linear;
        var p = AutoStretch.ComputeAutoStretchParams(src, width, height, bitDepth);
        return new StretchParams(Auto: false, Black: p.Black, Mid: p.Mid, White: p.White);
    }

    private static ushort[] Slice(ushort[] src, int start, int count) {
        var dst = new ushort[count];
        Array.Copy(src, start, dst, 0, count);
        return dst;
    }
}
