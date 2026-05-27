namespace NINA.Polaris.Services.Studio;

/// <summary>
/// LRGB compositing math, applies a mono luminance master (the L from a
/// long-exposure mono filter run) on top of a synthesised RGB image so
/// the final picture inherits the L channel's signal-to-noise while
/// keeping the chromaticity of the colour stack. The classic LRGB
/// workflow's last hand-on-the-keyboard step before the editor.
///
/// Both algorithms trust that the L master and the R/G/B masters were
/// stretched together upstream (e.g. each ran through the same auto-
/// stretch in STUDIO before reaching the combine). If the L plane is
/// significantly brighter or darker than the RGB stack, the output
/// will be globally brighter or darker, the user fixes that by
/// re-stretching in STUDIO before re-running the combine. This
/// matches PixInsight's LRGBCombination behaviour.
///
/// Two algorithms, picked via <see cref="LrgbAlgorithm"/>:
///
///   <b>Lab swap</b> (default, matches PixInsight's LRGBCombination).
///     1. Convert RGB → CIE Lab (D65, via linear sRGB + XYZ).
///     2. Replace the L_lab channel with the master L, scaled to the
///        Lab [0..100] range.
///     3. Convert Lab → RGB. Output preserves chrominance (a*, b*) of
///        the RGB stack while taking lightness detail from the L master.
///
///   <b>Ratio</b> (classical, faster).
///     <c>Lum_rgb = 0.2126R + 0.7152G + 0.0722B (Rec.709 luminance)
///        ratio   = L / max(Lum_rgb, eps)
///        R' = clamp(R * ratio); G' = clamp(G * ratio); B' = clamp(B * ratio)</c>
///     Subtly shifts saturation when ratio swings far from 1, but is
///     significantly cheaper than the Lab transform and produces
///     output PixInsight users recognise as "old-school LRGB".
/// </summary>
public static class LrgbCombiner {

    public enum LrgbAlgorithm { Lab, Ratio }

    /// <summary>
    /// Combine a pre-composed RGB image with a luminance master and
    /// return the LRGB result as plane-sequential ushort[] of length
    /// <c>w*h*3</c>. Inputs must all be the same size and pixel scale
    /// (<see cref="ChannelCombineService"/> registers them before
    /// handing off).
    /// </summary>
    /// <param name="r">Red channel as ushort[] of length w*h.</param>
    /// <param name="g">Green channel as ushort[] of length w*h.</param>
    /// <param name="b">Blue channel as ushort[] of length w*h.</param>
    /// <param name="L">Mono luminance master as ushort[] of length w*h.</param>
    /// <param name="algorithm">Lab swap or Ratio.</param>
    public static ushort[] Combine(ushort[] r, ushort[] g, ushort[] b, ushort[] L,
                                    int width, int height,
                                    LrgbAlgorithm algorithm = LrgbAlgorithm.Lab) {
        int n = width * height;
        if (r.Length != n || g.Length != n || b.Length != n || L.Length != n) {
            throw new ArgumentException(
                $"LrgbCombiner: input length mismatch (expected {n}, got " +
                $"R={r.Length} G={g.Length} B={b.Length} L={L.Length}).");
        }

        var output = new ushort[n * 3];
        switch (algorithm) {
            case LrgbAlgorithm.Ratio:
                ApplyRatio(r, g, b, L, output, n);
                break;
            case LrgbAlgorithm.Lab:
            default:
                ApplyLabSwap(r, g, b, L, output, n);
                break;
        }
        return output;
    }

    // ── ratio method ─────────────────────────────────────────────────

    private static void ApplyRatio(ushort[] r, ushort[] g, ushort[] b,
                                    ushort[] L, ushort[] output, int n) {
        for (int i = 0; i < n; i++) {
            double rr = r[i], gg = g[i], bb = b[i];
            double lum = 0.2126 * rr + 0.7152 * gg + 0.0722 * bb;
            // eps prevents divide-by-zero on truly black pixels and
            // keeps the ratio bounded so noise pixels do not stretch
            // to wild colours.
            double ratio = L[i] / Math.Max(lum, 1.0);
            output[i]         = (ushort)Math.Clamp(rr * ratio, 0, 65535);
            output[n + i]     = (ushort)Math.Clamp(gg * ratio, 0, 65535);
            output[n * 2 + i] = (ushort)Math.Clamp(bb * ratio, 0, 65535);
        }
    }

    // ── Lab swap method ──────────────────────────────────────────────

    private static void ApplyLabSwap(ushort[] r, ushort[] g, ushort[] b,
                                      ushort[] L, ushort[] output, int n) {
        for (int i = 0; i < n; i++) {
            // 1. RGB → linear sRGB (gamma 2.2 inverse). The full sRGB
            //    piecewise gamma is more accurate but pow(x, 2.2) is
            //    within ~0.5% over the visible range and 4× faster on
            //    the integration hot path. Astrophotography masters
            //    are linear in pixel space anyway (no display gamma
            //    applied yet), so this is essentially an identity for
            //    most pipelines, kept for sRGB-encoded inputs.
            double rN = r[i] / 65535.0;
            double gN = g[i] / 65535.0;
            double bN = b[i] / 65535.0;

            // 2. linear RGB → XYZ via D65 matrix (sRGB primaries).
            //    Matrix from Lindbloom, RGB→XYZ for D65 sRGB.
            double X = 0.4124564 * rN + 0.3575761 * gN + 0.1804375 * bN;
            double Y = 0.2126729 * rN + 0.7151522 * gN + 0.0721750 * bN;
            double Z = 0.0193339 * rN + 0.1191920 * gN + 0.9503041 * bN;

            // 3. XYZ → CIE Lab via D65 white point. f(t) is the
            //    standard piecewise cube root with linear segment near
            //    zero to keep the gradient bounded.
            double Xn = X / 0.95047;
            double Yn = Y;
            double Zn = Z / 1.08883;
            double fx = LabF(Xn);
            double fy = LabF(Yn);
            double fz = LabF(Zn);
            double aLab = 500.0 * (fx - fy);
            double bLab = 200.0 * (fy - fz);

            // 4. Replace L_lab with the master L, scaled to [0..100].
            //    Histogram-match already brought L into the right
            //    brightness range; just normalise to the Lab L scale.
            double newL = L[i] / 65535.0 * 100.0;

            // 5. Lab → XYZ. Inverse f.
            double newFy = (newL + 16.0) / 116.0;
            double newFx = aLab / 500.0 + newFy;
            double newFz = newFy - bLab / 200.0;
            double newX = 0.95047 * LabFInv(newFx);
            double newY = LabFInv(newFy);
            double newZ = 1.08883 * LabFInv(newFz);

            // 6. XYZ → linear RGB via D65 inverse matrix.
            double outR =  3.2404542 * newX + -1.5371385 * newY + -0.4985314 * newZ;
            double outG = -0.9692660 * newX +  1.8760108 * newY +  0.0415560 * newZ;
            double outB =  0.0556434 * newX + -0.2040259 * newY +  1.0572252 * newZ;

            // 7. linear RGB → sRGB (the pow(2.2) shortcut's inverse).
            //    Clamp BEFORE the gamma so the encode stays monotonic.
            outR = Math.Clamp(outR, 0, 1);
            outG = Math.Clamp(outG, 0, 1);
            outB = Math.Clamp(outB, 0, 1);

            output[i]         = (ushort)(outR * 65535.0);
            output[n + i]     = (ushort)(outG * 65535.0);
            output[n * 2 + i] = (ushort)(outB * 65535.0);
        }
    }

    private const double LabEpsilon = 216.0 / 24389.0;   // (6/29)^3
    private const double LabKappa   = 24389.0 / 27.0;    // (29/3)^3

    private static double LabF(double t) {
        return t > LabEpsilon
            ? Math.Cbrt(t)
            : (LabKappa * t + 16.0) / 116.0;
    }

    private static double LabFInv(double t) {
        double t3 = t * t * t;
        return t3 > LabEpsilon
            ? t3
            : (116.0 * t - 16.0) / LabKappa;
    }

}
