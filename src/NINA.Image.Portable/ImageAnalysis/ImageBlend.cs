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
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Two-image blend used by the "Image Blend" tool (the in-app equivalent of
/// PixInsight's ImageBlend script): independently MTF-stretch a base image and
/// a blend image, then combine them per-pixel with a blend mode + opacity.
///
/// The canonical use is the starless workflow — base = stretched starless,
/// blend = stretched stars-only — recombined with a Screen blend so the stars
/// are added back on top of the processed nebulosity.
///
/// Pure, allocation-only, no I/O, so it is unit-testable. Works identically for
/// a mono buffer or a plane-sequential RGB buffer: the per-image stretch uses
/// one black/mid/white triplet across all planes (matching the tool's single
/// set of sliders per image), and the combine is a per-element map.
/// </summary>
public static class ImageBlend {
    public enum Mode {
        /// <summary>1 − (1−a)(1−b). The default; brightens, never clips hard,
        /// the natural choice for adding stars back onto nebulosity.</summary>
        Screen,
        /// <summary>min(1, a+b). Linear add, clipped to white.</summary>
        Add,
        /// <summary>max(a, b). Keeps the brighter of the two.</summary>
        Lighten
    }

    /// <summary>Per-image MTF stretch: black/mid/white all normalised 0..1
    /// (mid is the midtones balance, &lt;0.5 lifts shadows).</summary>
    public readonly record struct StretchSpec(double Black, double Mid, double White) {
        public static StretchSpec Identity => new(0.0, 0.5, 1.0);
    }

    /// <summary>
    /// MTF-stretch <paramref name="baseData"/> and <paramref name="blendData"/>
    /// with their own specs, combine per pixel via <paramref name="mode"/>, and
    /// mix the result against the base by <paramref name="opacity"/>
    /// (out = base*(1−op) + blended*op). Returns a 16-bit buffer the same
    /// length/layout as the inputs.
    /// </summary>
    public static ushort[] Combine(
            ushort[] baseData, ushort[] blendData,
            StretchSpec baseStretch, StretchSpec blendStretch,
            Mode mode, double opacity,
            int baseBitDepth = 16, int blendBitDepth = 16) {
        if (baseData == null) throw new ArgumentNullException(nameof(baseData));
        if (blendData == null) throw new ArgumentNullException(nameof(blendData));
        if (baseData.Length != blendData.Length)
            throw new ArgumentException(
                $"base ({baseData.Length}) and blend ({blendData.Length}) buffers must match in length");

        opacity = Math.Clamp(opacity, 0.0, 1.0);

        // Independent stretch of each image, in float to keep precision.
        var a = AutoStretch.ApplyManualFloat(
            baseData, baseStretch.Black, baseStretch.Mid, baseStretch.White, baseBitDepth);
        var b = AutoStretch.ApplyManualFloat(
            blendData, blendStretch.Black, blendStretch.Mid, blendStretch.White, blendBitDepth);

        var outp = new ushort[baseData.Length];
        Parallel.ForEach(Partitioner.Create(0, baseData.Length), range => {
            for (int i = range.Item1; i < range.Item2; i++) {
                float av = a[i], bv = b[i];
                float blended = mode switch {
                    Mode.Add     => MathF.Min(1f, av + bv),
                    Mode.Lighten => MathF.Max(av, bv),
                    _            => 1f - (1f - av) * (1f - bv),   // Screen
                };
                float v = (float)(av * (1.0 - opacity) + blended * opacity);
                v = v < 0f ? 0f : (v > 1f ? 1f : v);
                outp[i] = (ushort)(v * 65535f + 0.5f);
            }
        });
        return outp;
    }

    /// <summary>
    /// Derive a stars-only buffer from an original and its starless version:
    /// stars = clamp(original − starless, 0). Both must match in length/layout.
    /// Used after star removal so the recombine tool gets a stars image without
    /// a second model pass.
    /// </summary>
    public static ushort[] DeriveStars(ushort[] original, ushort[] starless) {
        if (original == null) throw new ArgumentNullException(nameof(original));
        if (starless == null) throw new ArgumentNullException(nameof(starless));
        if (original.Length != starless.Length)
            throw new ArgumentException("original and starless buffers must match in length");

        var stars = new ushort[original.Length];
        Parallel.ForEach(Partitioner.Create(0, original.Length), range => {
            for (int i = range.Item1; i < range.Item2; i++) {
                int d = original[i] - starless[i];
                stars[i] = d > 0 ? (ushort)d : (ushort)0;
            }
        });
        return stars;
    }

    public static Mode ParseMode(string? s) => (s ?? "screen").Trim().ToLowerInvariant() switch {
        "add"     => Mode.Add,
        "lighten" => Mode.Lighten,
        _         => Mode.Screen
    };
}
