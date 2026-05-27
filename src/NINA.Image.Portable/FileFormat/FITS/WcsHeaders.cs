using System.Globalization;

namespace NINA.Image.FileFormat.FITS;

/// <summary>
/// Read + write the World Coordinate System (WCS) headers that a
/// plate-solver produces, plus pixel↔(RA, Dec) conversion helpers
/// built on top.
///
/// Polaris solves with ASTAP (and PlateSolve3, astrometry.net) and
/// today the WCS data lives only in <c>PlateSolveResult</c>: it is
/// not written back to the FITS file. The Photometric Color
/// Calibration (PCC, CCALB-3) workflow needs that WCS in the FITS so
/// it can map catalog star (RA, Dec) coordinates back to image
/// pixels without re-solving. <see cref="Add"/> emits the standard
/// CRVAL/CRPIX/CD/CTYPE keywords for a TAN-gnomonic projection;
/// <see cref="Read"/> extracts them and builds a <see cref="WcsInfo"/>
/// with <see cref="WcsInfo.PixelToRaDec"/> + <see cref="WcsInfo.RaDecToPixel"/>
/// methods.
///
/// Projection: TAN (gnomonic), the de facto standard for
/// astrophotography frames at modest fields of view (≤ 5°). The full
/// WCS spec supports SIN / ZEA / AIT / CAR projections too; we
/// implement only TAN for now because it covers every camera +
/// telescope combination Polaris targets. Anything that bends much
/// beyond TAN's accuracy (a fisheye all-sky camera, say) is out of
/// scope and would need its projection added here.
/// </summary>
public static class WcsHeaders {

    /// <summary>
    /// Append WCS keyword cards to a custom-keywords list that will
    /// then be passed to <see cref="FITSWriter.Write"/>. Cards
    /// follow the WCS-FITS Paper II convention (Calabretta &amp;
    /// Greisen 2002): CTYPE1=RA---TAN / CTYPE2=DEC--TAN, CRVAL1/2
    /// are the reference RA/Dec in degrees, CRPIX1/2 are the
    /// reference pixel (1-based per the FITS spec), CD1_1..CD2_2 are
    /// the rotation+scale matrix in degrees/pixel.
    ///
    /// The reference pixel defaults to the image centre, which is
    /// what every common solver produces; pass a custom one only if
    /// the upstream solver uses something different.
    /// </summary>
    public static void Add(List<KeyValuePair<string, string>> customKeywords,
            WcsInfo wcs) {
        if (customKeywords == null) throw new ArgumentNullException(nameof(customKeywords));
        if (wcs == null) throw new ArgumentNullException(nameof(wcs));
        customKeywords.Add(new("CTYPE1", "RA---TAN"));
        customKeywords.Add(new("CTYPE2", "DEC--TAN"));
        customKeywords.Add(new("CRVAL1", Fmt(wcs.RaDeg)));
        customKeywords.Add(new("CRVAL2", Fmt(wcs.DecDeg)));
        customKeywords.Add(new("CRPIX1", Fmt(wcs.RefPixelX)));
        customKeywords.Add(new("CRPIX2", Fmt(wcs.RefPixelY)));
        customKeywords.Add(new("CD1_1",  Fmt(wcs.CD11)));
        customKeywords.Add(new("CD1_2",  Fmt(wcs.CD12)));
        customKeywords.Add(new("CD2_1",  Fmt(wcs.CD21)));
        customKeywords.Add(new("CD2_2",  Fmt(wcs.CD22)));
    }

    /// <summary>
    /// Build a <see cref="WcsInfo"/> from a plate-solve result
    /// expressed as the simpler (RA, Dec, scale, rotation) tuple
    /// our solvers return. The CD matrix is constructed as:
    ///
    ///   CD11 = -scaleDeg * cos(rot)     CD12 =  scaleDeg * sin(rot)
    ///   CD21 =  scaleDeg * sin(rot)     CD22 =  scaleDeg * cos(rot)
    ///
    /// The minus on CD11 reflects that RA grows to the east while
    /// pixel X grows to the right; the standard FITS convention
    /// matches an image displayed north-up with east on the left.
    /// </summary>
    public static WcsInfo FromSolveResult(double raDeg, double decDeg,
            double scaleArcsecPerPixel, double rotationDeg,
            int imageWidth, int imageHeight) {
        double scaleDeg = scaleArcsecPerPixel / 3600.0;
        double rotRad = rotationDeg * Math.PI / 180.0;
        double c = Math.Cos(rotRad);
        double s = Math.Sin(rotRad);
        return new WcsInfo {
            RaDeg = raDeg,
            DecDeg = decDeg,
            // FITS CRPIX is 1-based, and the reference pixel is
            // by convention the image centre for solver outputs.
            RefPixelX = (imageWidth + 1) / 2.0,
            RefPixelY = (imageHeight + 1) / 2.0,
            CD11 = -scaleDeg * c,
            CD12 =  scaleDeg * s,
            CD21 =  scaleDeg * s,
            CD22 =  scaleDeg * c,
        };
    }

    /// <summary>
    /// Pull a <see cref="WcsInfo"/> out of a FITS header dictionary
    /// if it has the WCS cards; returns null otherwise (no WCS
    /// present, common for un-solved frames).
    /// </summary>
    public static WcsInfo? Read(Dictionary<string, FITSHeaderCard> headers) {
        if (headers == null) return null;
        // We require at minimum CTYPE1 / CTYPE2 + CRVAL1 / CRVAL2 +
        // CRPIX1 / CRPIX2. The CD matrix is required for
        // RaDecToPixel; if only CDELT is present (older convention)
        // we synthesise the CD matrix from CDELT + CROTA2.
        if (!HasKey(headers, "CRVAL1") || !HasKey(headers, "CRVAL2")) return null;
        if (!HasKey(headers, "CRPIX1") || !HasKey(headers, "CRPIX2")) return null;

        var wcs = new WcsInfo {
            RaDeg     = ReadDouble(headers, "CRVAL1"),
            DecDeg    = ReadDouble(headers, "CRVAL2"),
            RefPixelX = ReadDouble(headers, "CRPIX1"),
            RefPixelY = ReadDouble(headers, "CRPIX2"),
        };

        if (HasKey(headers, "CD1_1")) {
            wcs.CD11 = ReadDouble(headers, "CD1_1");
            wcs.CD12 = ReadDouble(headers, "CD1_2");
            wcs.CD21 = ReadDouble(headers, "CD2_1");
            wcs.CD22 = ReadDouble(headers, "CD2_2");
        } else if (HasKey(headers, "CDELT1") && HasKey(headers, "CDELT2")) {
            // Older FITS convention: CDELT + CROTA. Synthesize the
            // CD matrix so downstream code only has to deal with one
            // representation.
            double cdelt1 = ReadDouble(headers, "CDELT1");
            double cdelt2 = ReadDouble(headers, "CDELT2");
            double crota = HasKey(headers, "CROTA2") ? ReadDouble(headers, "CROTA2")
                         : HasKey(headers, "CROTA1") ? ReadDouble(headers, "CROTA1")
                         : 0;
            double rotRad = crota * Math.PI / 180.0;
            double c = Math.Cos(rotRad);
            double s = Math.Sin(rotRad);
            wcs.CD11 = cdelt1 * c;
            wcs.CD12 = -cdelt2 * s;
            wcs.CD21 = cdelt1 * s;
            wcs.CD22 = cdelt2 * c;
        } else {
            // No scale/rotation info; can't do pixel↔sky math.
            return null;
        }

        return wcs;
    }

    private static bool HasKey(Dictionary<string, FITSHeaderCard> headers, string key)
        => headers.ContainsKey(key);

    private static double ReadDouble(Dictionary<string, FITSHeaderCard> headers, string key) {
        if (!headers.TryGetValue(key, out var card)) return 0;
        return double.TryParse(card.Value, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var v) ? v : 0;
    }

    private static string Fmt(double d)
        => d.ToString("0.##########", CultureInfo.InvariantCulture);
}

/// <summary>
/// In-memory representation of a plate-solved FITS frame's WCS.
/// Carries CRVAL (reference RA/Dec), CRPIX (reference pixel, 1-based),
/// and the CD matrix. Provides round-trip pixel↔(RA, Dec) helpers
/// that <see cref="NINA.Polaris.Services.Studio.ColorCalibrationService"/>
/// uses to project catalog stars into image space for PCC matching.
/// </summary>
public class WcsInfo {
    public double RaDeg { get; set; }
    public double DecDeg { get; set; }
    public double RefPixelX { get; set; }
    public double RefPixelY { get; set; }
    public double CD11 { get; set; }
    public double CD12 { get; set; }
    public double CD21 { get; set; }
    public double CD22 { get; set; }

    /// <summary>
    /// Convert a 1-based image pixel (x, y) to (RA, Dec) in degrees
    /// via the inverse TAN projection. Returns NaN/NaN if the pixel
    /// projects beyond the celestial sphere (impossible for sane
    /// fields of view but the math is defensive).
    /// </summary>
    public (double raDeg, double decDeg) PixelToRaDec(double pixelX, double pixelY) {
        // Intermediate world coords (degrees from reference pixel).
        double dx = pixelX - RefPixelX;
        double dy = pixelY - RefPixelY;
        // Apply the CD matrix to get intermediate world coordinates
        // (deg) on the projection plane.
        double xi  = CD11 * dx + CD12 * dy;
        double eta = CD21 * dx + CD22 * dy;
        // Convert from degrees to radians for trig.
        double xiR = xi * Math.PI / 180.0;
        double etaR = eta * Math.PI / 180.0;
        double ra0 = RaDeg * Math.PI / 180.0;
        double dec0 = DecDeg * Math.PI / 180.0;
        // Standard inverse TAN (gnomonic) formulae.
        double rho = Math.Sqrt(xiR * xiR + etaR * etaR);
        if (rho < 1e-15) return (RaDeg, DecDeg);
        double c = Math.Atan(rho);
        double sinC = Math.Sin(c);
        double cosC = Math.Cos(c);
        double sinDec0 = Math.Sin(dec0);
        double cosDec0 = Math.Cos(dec0);
        double dec = Math.Asin(cosC * sinDec0 + (etaR * sinC * cosDec0) / rho);
        double ra = ra0 + Math.Atan2(xiR * sinC,
            rho * cosDec0 * cosC - etaR * sinDec0 * sinC);
        // Normalise RA to [0, 360).
        double raDeg = ra * 180.0 / Math.PI;
        while (raDeg < 0) raDeg += 360.0;
        while (raDeg >= 360.0) raDeg -= 360.0;
        return (raDeg, dec * 180.0 / Math.PI);
    }

    /// <summary>
    /// Convert (RA, Dec) in degrees back to a 1-based image pixel.
    /// Returns the pixel coords; caller is responsible for checking
    /// the result lies inside [1, width] / [1, height].
    /// </summary>
    public (double pixelX, double pixelY) RaDecToPixel(double raDeg, double decDeg) {
        double raR = raDeg * Math.PI / 180.0;
        double decR = decDeg * Math.PI / 180.0;
        double ra0 = RaDeg * Math.PI / 180.0;
        double dec0 = DecDeg * Math.PI / 180.0;
        // Forward TAN projection. cosTheta is the cosine of the
        // angular distance between the target and the reference
        // direction; when it's ≤ 0 the target is more than 90° from
        // the reference, so the projection isn't meaningful.
        double sinDec = Math.Sin(decR), cosDec = Math.Cos(decR);
        double sinDec0 = Math.Sin(dec0), cosDec0 = Math.Cos(dec0);
        double dRa = raR - ra0;
        double cosTheta = sinDec0 * sinDec + cosDec0 * cosDec * Math.Cos(dRa);
        if (cosTheta < 1e-15) return (double.NaN, double.NaN);
        double xi = (cosDec * Math.Sin(dRa)) / cosTheta;
        double eta = (cosDec0 * sinDec - sinDec0 * cosDec * Math.Cos(dRa)) / cosTheta;
        // Back to degrees on the projection plane.
        double xiDeg = xi * 180.0 / Math.PI;
        double etaDeg = eta * 180.0 / Math.PI;
        // Invert the CD matrix.
        double det = CD11 * CD22 - CD12 * CD21;
        if (Math.Abs(det) < 1e-30) return (double.NaN, double.NaN);
        double dx = (CD22 * xiDeg - CD12 * etaDeg) / det;
        double dy = (-CD21 * xiDeg + CD11 * etaDeg) / det;
        return (RefPixelX + dx, RefPixelY + dy);
    }
}
