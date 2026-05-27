namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Aperture photometry on already-detected stars in an RGB image.
/// Pairs with <see cref="StarDetector"/> which finds star positions
/// + HFR but only reports total luminance flux: PCC needs flux
/// separately per R/G/B channel so it can fit per-channel gains
/// against a catalog star's expected colour.
///
/// Algorithm (standard aperture photometry):
///   1. Inner aperture: circle of radius r_in = 2 * HFR around the
///      star centroid. All pixels inside the circle contribute to
///      the star flux.
///   2. Background annulus: ring between r_in and r_out (= 4 * HFR
///      by default). Median of pixels in the annulus = per-pixel
///      sky background. Robust to faint stars contaminating the
///      annulus because we use median, not mean.
///   3. Per-channel net flux = sum_inside_aperture - (pixel_count *
///      background_median).
///   4. Skip stars whose aperture overlaps a saturated pixel
///      (>= 60000) on any channel, the photometry would be biased.
///   5. Skip stars whose inner aperture contains fewer than 5 pixels
///      (too close to the image edge after clipping).
///
/// Inputs are plane-sequential ushort[] (the layout
/// <see cref="NINA.Image.FileFormat.FITS.FITSReader"/> emits for
/// RGB FITS): R plane first, then G, then B.
/// </summary>
public static class StarPhotometer {

    /// <summary>
    /// Per-star photometric measurement. Position + HFR copied from
    /// the input DetectedStar; FluxR/G/B are background-subtracted
    /// aperture sums in ADU. ApertureRadius and BackgroundLevel are
    /// kept for diagnostics so a caller can audit why a particular
    /// star produced a given gain estimate.
    /// </summary>
    public record StarPhotometry(
        double X, double Y, double HFR,
        double FluxR, double FluxG, double FluxB,
        double BackgroundR, double BackgroundG, double BackgroundB,
        int PixelCount, double ApertureRadius, bool Saturated);

    /// <summary>
    /// Measure per-channel flux for a list of detected stars on a
    /// plane-sequential RGB buffer.
    /// </summary>
    /// <param name="planeSequentialRgb">ushort[w*h*3], R then G then B.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="stars">Detected stars from <see cref="StarDetector"/>.</param>
    /// <param name="apertureFactor">Aperture radius multiplier (default
    ///     2.0 * HFR, a common choice that captures &gt;95% of star
    ///     flux for a Gaussian PSF).</param>
    /// <param name="annulusOuterFactor">Background annulus outer
    ///     radius multiplier (default 4.0 * HFR).</param>
    /// <param name="saturationLevel">Pixels at or above this value
    ///     mark a star as saturated; saturated stars are returned
    ///     with Saturated=true and zero flux so the caller can
    ///     filter them out without losing position information.</param>
    public static List<StarPhotometry> MeasureRgb(
            ushort[] planeSequentialRgb, int width, int height,
            IEnumerable<DetectedStar> stars,
            double apertureFactor = 2.0,
            double annulusOuterFactor = 4.0,
            ushort saturationLevel = 60000) {
        int n = width * height;
        if (planeSequentialRgb.Length != n * 3) {
            throw new ArgumentException(
                $"MeasureRgb expects a plane-sequential RGB ushort[] of " +
                $"length {n * 3} (w*h*3), got {planeSequentialRgb.Length}.");
        }

        var results = new List<StarPhotometry>();
        foreach (var s in stars) {
            // Aperture radius. We clamp to a minimum of 2 px so very
            // tight stars (HFR < 1) still get a usable footprint;
            // anything smaller often catches just a hot pixel and
            // gives unreliable flux.
            double rIn = Math.Max(2.0, apertureFactor * Math.Max(1.0, s.HFR));
            double rOut = Math.Max(rIn + 2.0, annulusOuterFactor * Math.Max(1.0, s.HFR));

            int x0 = (int)Math.Floor(s.X - rOut);
            int y0 = (int)Math.Floor(s.Y - rOut);
            int x1 = (int)Math.Ceiling(s.X + rOut);
            int y1 = (int)Math.Ceiling(s.Y + rOut);
            // Clip to image bounds.
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 >= width) x1 = width - 1;
            if (y1 >= height) y1 = height - 1;
            if (x1 - x0 < 3 || y1 - y0 < 3) continue;   // too tight to be useful

            double rIn2 = rIn * rIn;
            double rOut2 = rOut * rOut;

            // Collect per-channel sums + annulus samples in one pass.
            double sumR = 0, sumG = 0, sumB = 0;
            int aperturePixels = 0;
            var annR = new List<int>();
            var annG = new List<int>();
            var annB = new List<int>();
            bool saturated = false;

            for (int y = y0; y <= y1 && !saturated; y++) {
                for (int x = x0; x <= x1; x++) {
                    double dx = x - s.X;
                    double dy = y - s.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 > rOut2) continue;
                    int idx = y * width + x;
                    ushort vR = planeSequentialRgb[idx];
                    ushort vG = planeSequentialRgb[n + idx];
                    ushort vB = planeSequentialRgb[2 * n + idx];
                    // Saturation check first; if anything in the
                    // aperture is saturated, the flux measurement is
                    // junk regardless of the rest of the pixels.
                    if (d2 <= rIn2 &&
                        (vR >= saturationLevel || vG >= saturationLevel || vB >= saturationLevel)) {
                        saturated = true;
                        break;
                    }
                    if (d2 <= rIn2) {
                        sumR += vR;
                        sumG += vG;
                        sumB += vB;
                        aperturePixels++;
                    } else {
                        // Pixel is in the background annulus (between
                        // rIn and rOut).
                        annR.Add(vR);
                        annG.Add(vG);
                        annB.Add(vB);
                    }
                }
            }

            if (saturated) {
                results.Add(new StarPhotometry(
                    X: s.X, Y: s.Y, HFR: s.HFR,
                    FluxR: 0, FluxG: 0, FluxB: 0,
                    BackgroundR: 0, BackgroundG: 0, BackgroundB: 0,
                    PixelCount: 0, ApertureRadius: rIn, Saturated: true));
                continue;
            }
            // Need enough pixels in the aperture for the measurement
            // to be meaningful. Very small stars near the edge can
            // end up with too few aperture pixels after clipping.
            if (aperturePixels < 5 || annR.Count < 5) continue;

            double bgR = Median(annR);
            double bgG = Median(annG);
            double bgB = Median(annB);
            // Net flux = aperture sum minus expected background under
            // the aperture. Background is per-pixel median * pixel
            // count (standard photometry textbook formula).
            double netR = sumR - bgR * aperturePixels;
            double netG = sumG - bgG * aperturePixels;
            double netB = sumB - bgB * aperturePixels;

            results.Add(new StarPhotometry(
                X: s.X, Y: s.Y, HFR: s.HFR,
                FluxR: netR, FluxG: netG, FluxB: netB,
                BackgroundR: bgR, BackgroundG: bgG, BackgroundB: bgB,
                PixelCount: aperturePixels, ApertureRadius: rIn,
                Saturated: false));
        }
        return results;
    }

    /// <summary>
    /// Quick-and-dirty median via an in-place sort. Fine for the
    /// annulus sample sizes we see in practice (a few hundred pixels
    /// per star, times a few hundred stars per master = manageable
    /// even on a Pi 2).
    /// </summary>
    private static double Median(List<int> values) {
        if (values.Count == 0) return 0;
        values.Sort();
        int mid = values.Count / 2;
        if ((values.Count & 1) == 1) return values[mid];
        return 0.5 * (values[mid - 1] + values[mid]);
    }
}
