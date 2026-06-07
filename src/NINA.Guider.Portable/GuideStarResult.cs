namespace NINA.Guider.Portable;

/// <summary>Outcome of a single-star centroid search (mirrors PHD2 FindResult).</summary>
public enum GuideStarStatus {
    Ok,
    LowMass,
    LowSnr,
    LowHfd,
    HighHfd,
    Saturated,
    Error,
}

/// <summary>Result of <see cref="GuideStar.Find"/>: sub-pixel centroid + quality metrics.</summary>
public readonly record struct GuideStarResult(
    double X,
    double Y,
    double Mass,
    double Snr,
    double Hfd,
    ushort PeakValue,
    GuideStarStatus Status) {

    /// <summary>True when the star was located with acceptable mass + SNR.
    /// Saturated still counts as found (centroid usable), matching PHD2.</summary>
    public bool Found => Status is GuideStarStatus.Ok or GuideStarStatus.Saturated
                                or GuideStarStatus.LowHfd or GuideStarStatus.HighHfd;

    public static GuideStarResult Failed(GuideStarStatus status) =>
        new(0, 0, 0, 0, 0, 0, status);
}
