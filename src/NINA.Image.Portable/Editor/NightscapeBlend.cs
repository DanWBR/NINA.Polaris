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

/// <summary>
/// The nightscape composite: two 16-bit masters of the same fixed-tripod scene,
/// one stacked with star alignment (the SKY) and one stacked without (the
/// FOREGROUND), joined through a per-pixel foreground-coverage map from
/// <see cref="HorizonMask"/>.
///
/// <para>Works in 16 bits so the blend happens before any 8-bit render, unlike
/// the editor's <c>EditMask.Blend</c> (8-bit) and <c>ImageBlend.Combine</c>
/// (scalar opacity, no per-pixel mask). Buffers are PLANAR: for 3 channels the
/// layout is [R plane][G plane][B plane], the same shape the stacking path
/// hands to the renderer.</para>
/// </summary>
public static class NightscapeBlend {

    /// <summary><c>out = sky*(1-coverage) + foreground*coverage</c>, per pixel,
    /// across every channel. <paramref name="coverage"/> is length w*h (one
    /// weight per pixel, applied to all channels); <paramref name="sky"/> and
    /// <paramref name="foreground"/> are length w*h*channels, planar.</summary>
    public static ushort[] Composite16(
            ushort[] sky, ushort[] foreground, float[] coverage,
            int width, int height, int channels) {
        if (channels < 1) throw new System.ArgumentOutOfRangeException(nameof(channels));
        int wh = width * height;
        if (sky.Length != wh * channels || foreground.Length != wh * channels)
            throw new System.ArgumentException("sky/foreground length must be width*height*channels");
        if (coverage.Length != wh)
            throw new System.ArgumentException("coverage length must be width*height");

        var outBuf = new ushort[wh * channels];
        for (int c = 0; c < channels; c++) {
            int planeOffset = c * wh;
            for (int i = 0; i < wh; i++) {
                float f = coverage[i];
                if (f <= 0f) { outBuf[planeOffset + i] = sky[planeOffset + i]; continue; }
                if (f >= 1f) { outBuf[planeOffset + i] = foreground[planeOffset + i]; continue; }
                double v = sky[planeOffset + i] * (1.0 - f) + foreground[planeOffset + i] * f;
                outBuf[planeOffset + i] = ClampToUShort(v);
            }
        }
        return outBuf;
    }

    private static ushort ClampToUShort(double v) {
        if (v <= 0) return 0;
        if (v >= ushort.MaxValue) return ushort.MaxValue;
        return (ushort)(v + 0.5);
    }
}
