namespace NINA.Guider.Portable;

/// <summary>A per-axis guide algorithm: maps the measured error (pixels) to a
/// correction (pixels) to apply this frame. Mirrors PHD2's GuideAlgorithm.</summary>
public interface IGuideAlgorithm {
    string Name { get; }
    /// <summary>Compute the correction for this frame's measured error.</summary>
    double Result(double input);
    /// <summary>Clear internal history (on guiding start / dither).</summary>
    void Reset();
}
