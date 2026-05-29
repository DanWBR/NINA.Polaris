namespace NINA.Image.Editor;

/// <summary>
/// Heuristic "Auto" computer for the editor sliders, inspired by Lightroom
/// mobile's Auto button. Reads a session's 8-bit working buffer (post-stretch,
/// the same byte[] the EditPipeline consumes) and returns reasonable starting
/// values for the Light + Color sliders. Pure heuristic, no ML, no network.
///
/// Scope is intentionally conservative for astro work:
///   - Exposure is capped at ±1.5 stops (the source is already stretched by
///     the autostretch upstream, so a big boost would over-cook).
///   - Highlights/Shadows/Whites/Blacks read percentiles to decide whether
///     the histogram is clipped or has unused headroom, then nudge.
///   - Contrast gets a small positive bias (matches Lightroom Auto behaviour).
///   - Vibrance is gentle (+0.25 on RGB); Saturation stays at 0 because
///     vibrance already covers the case without over-saturating nebula cores.
///   - White Balance and ToneCurve are NOT touched (astro WB belongs to PCC,
///     and an auto S-curve would crush gradient detail in nebulae).
///
/// Returned <see cref="AutoSuggestion"/> can be applied with the existing
/// editor setters; sliders show the values, the user can refine, and the
/// sidecar treats them like any manual edit (saved on demand, undoable).
/// </summary>
public static class EditAutoTuner {

    /// <summary>Combined Light + Color suggestion. Color is null when the
    /// source is mono (no point boosting vibrance on a 1-channel buffer).</summary>
    public sealed record AutoSuggestion(LightParams Light, ColorParams? Color);

    // --- Tuning constants -------------------------------------------------
    //
    // All thresholds are in normalised 0..1 luminance (after div by 255).
    // The mapping ratios are picked so that "barely off the threshold"
    // produces ~0.1 worth of slider motion and "way off" saturates the
    // slider, matching the empirical feel of Lightroom Auto.

    /// <summary>Adams Zone V target tonal midpoint, drives exposure boost.</summary>
    private const double TargetMid = 0.18;
    /// <summary>Soft cap on auto exposure (stops). Source is pre-stretched
    /// upstream so the editor rarely needs more than this.</summary>
    private const double ExposureCap = 1.5;
    /// <summary>p0.5 above this counts as "shadows crushed into black".</summary>
    private const double BlackClipThreshold = 0.02;
    /// <summary>p99.5 below this counts as "whites have headroom".</summary>
    private const double WhiteHeadroomThreshold = 0.95;
    /// <summary>p99.5 above this counts as "highlights blowing out".</summary>
    private const double HighlightClipThreshold = 0.97;
    /// <summary>p5 below this counts as "deep shadows that lack detail".</summary>
    private const double ShadowLiftThreshold = 0.05;
    /// <summary>Default gentle contrast boost (Lightroom Auto signature).</summary>
    private const double ContrastBias = 0.10;
    /// <summary>Default vibrance bump for RGB sources. Vibrance protects
    /// already-saturated pixels, so this is safe even on star-rich frames.</summary>
    private const double VibranceBias = 0.25;

    /// <summary>
    /// Compute Light + Color values from an interleaved 8-bit buffer.
    /// <paramref name="channels"/> is 1 (mono) or 3 (BGR interleaved, the
    /// same layout EditPipeline consumes). For 3-channel data the algorithm
    /// works on Rec.709 luminance per pixel.
    /// </summary>
    public static AutoSuggestion Compute(byte[] data, int width, int height, int channels) {
        if (data == null || data.Length == 0 || width <= 0 || height <= 0) {
            return new AutoSuggestion(new LightParams(), channels == 3 ? new ColorParams() : null);
        }
        if (channels != 1 && channels != 3) {
            throw new System.ArgumentException("channels must be 1 or 3", nameof(channels));
        }

        // Build a 256-bin luminance histogram. For mono we read the byte
        // straight; for RGB we collapse via Rec.709 weights. We stream
        // through the buffer once, no allocations beyond the histogram.
        var hist = new int[256];
        long sampleCount = 0;
        int pixelCount = width * height;
        if (channels == 1) {
            int limit = System.Math.Min(data.Length, pixelCount);
            for (int i = 0; i < limit; i++) {
                hist[data[i]]++;
            }
            sampleCount = limit;
        } else {
            // BGR interleaved (Skia layout). Rec.709 coefficients.
            int triplets = System.Math.Min(data.Length / 3, pixelCount);
            for (int i = 0; i < triplets; i++) {
                int o = i * 3;
                // 0.2126*R + 0.7152*G + 0.0722*B
                double lum = 0.0722 * data[o] + 0.7152 * data[o + 1] + 0.2126 * data[o + 2];
                int bin = (int)lum;
                if (bin < 0) bin = 0;
                else if (bin > 255) bin = 255;
                hist[bin]++;
            }
            sampleCount = triplets;
        }

        if (sampleCount == 0) {
            return new AutoSuggestion(new LightParams(), channels == 3 ? new ColorParams() : null);
        }

        // Pull the percentiles we need from the cumulative histogram in
        // a single pass. Keeping them in 0..1 space makes the slider
        // mapping below readable without a /255 scattered through it.
        double p005 = Percentile(hist, sampleCount, 0.005);   // shadow clip
        double p05  = Percentile(hist, sampleCount, 0.05);    // deep shadows
        double p50  = Percentile(hist, sampleCount, 0.50);    // median
        double p995 = Percentile(hist, sampleCount, 0.995);   // highlight clip

        // ---- Light sliders ------------------------------------------------

        // Exposure: nudge toward target mid (zone V). Log2 keeps it in
        // perceptual stops. Cap because the source is pre-stretched.
        double exposure = 0;
        if (p50 > 1e-4) {
            exposure = System.Math.Log2(TargetMid / p50);
            exposure = System.Math.Clamp(exposure, -ExposureCap, ExposureCap);
        }

        // Blacks: drag negative when p0.5 is sitting well above 0 (means
        // shadows are pinned but not crushed yet). Slider value scales
        // linearly until p0.5 hits 0.05.
        double blacks = 0;
        if (p005 > BlackClipThreshold) {
            blacks = -System.Math.Min(1.0, (p005 - BlackClipThreshold) / 0.05) * 0.6;
        }

        // Whites: drag positive when p99.5 sits well below pure white.
        // Symmetric to Blacks.
        double whites = 0;
        if (p995 < WhiteHeadroomThreshold) {
            whites = System.Math.Min(1.0, (WhiteHeadroomThreshold - p995) / 0.05) * 0.6;
        }

        // Highlights: drag negative when the top is clipping. Keeps star
        // cores from blowing out further once we boosted exposure.
        double highlights = 0;
        if (p995 > HighlightClipThreshold) {
            highlights = -0.5;
        }

        // Shadows: drag positive when p5 is buried. Lifts faint nebular
        // detail without flattening contrast (that's what Blacks would do).
        double shadows = 0;
        if (p05 < ShadowLiftThreshold) {
            shadows = 0.3;
        }

        // Contrast: gentle bump matches Lightroom Auto's signature. Skip
        // when the histogram is already wide (max-min > 0.9 in 0..1) so
        // we don't double up on what's already a punchy image.
        double contrast = ContrastBias;
        if (p995 - p005 > 0.9) {
            contrast = 0;
        }

        var light = new LightParams(
            Exposure: exposure,
            Contrast: contrast,
            Highlights: highlights,
            Shadows: shadows,
            Whites: whites,
            Blacks: blacks);

        // ---- Color sliders (RGB only) -------------------------------------

        ColorParams? color = null;
        if (channels == 3) {
            color = new ColorParams(Vibrance: VibranceBias, Saturation: 0, Hue: 0);
        }

        return new AutoSuggestion(light, color);
    }

    /// <summary>
    /// Look up a fractional position in a 256-bin histogram using cumulative
    /// counts. Returns a value in 0..1 (normalised bin index). Cheaper than
    /// sorting samples and gives the same answer for 8-bit data.
    /// </summary>
    private static double Percentile(int[] hist, long sampleCount, double frac) {
        long target = (long)System.Math.Floor(sampleCount * frac);
        if (target < 0) target = 0;
        if (target >= sampleCount) target = sampleCount - 1;
        long cumulative = 0;
        for (int i = 0; i < hist.Length; i++) {
            cumulative += hist[i];
            if (cumulative > target) {
                return i / 255.0;
            }
        }
        return 1.0;
    }
}
