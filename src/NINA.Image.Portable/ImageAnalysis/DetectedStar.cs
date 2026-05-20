namespace NINA.Image.ImageAnalysis;

public class DetectedStar {
    public double X { get; set; }
    public double Y { get; set; }
    public double HFR { get; set; }
    public double Peak { get; set; }
    public double Flux { get; set; }
    public int PixelCount { get; set; }

    public double DistanceTo(DetectedStar other) {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
