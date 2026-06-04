namespace NINA.Image.ImageAnalysis;

public class DetectedStar {
    public double X { get; set; }
    public double Y { get; set; }
    public double HFR { get; set; }
    public double Peak { get; set; }
    public double Flux { get; set; }
    public int PixelCount { get; set; }

    /// <summary>Shape elongation from the flux-weighted second moments:
    /// 0 = perfectly round, →1 = highly elongated
    /// (sqrt(1 - minorAxis²/majorAxis²)). Used by the STUDIO aberration
    /// analyzer to tell coma / astigmatism from a round-but-soft field.</summary>
    public double Eccentricity { get; set; }

    /// <summary>Major-axis angle of the star ellipse in radians
    /// (0.5·atan2(2·Mxy, Mxx-Myy)), image coordinates. Only meaningful
    /// when <see cref="Eccentricity"/> is non-trivial.</summary>
    public double OrientationRad { get; set; }

    public double DistanceTo(DetectedStar other) {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
