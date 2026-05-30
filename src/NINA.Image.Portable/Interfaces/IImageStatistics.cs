namespace NINA.Image.Interfaces;

public interface IImageStatistics {
    int Width { get; }
    int Height { get; }
    double Mean { get; }
    double Median { get; }
    double StDev { get; }
    double MAD { get; }
    int Min { get; }
    int Max { get; }
    long StarCount { get; }
    double HFR { get; }
    /// <summary>Background signal-to-noise ratio, computed in the
    /// same pixel pass as Mean/Median/MAD. See
    /// <see cref="NINA.Image.ImageData.ImageStatistics.ComputeBackgroundSnr"/>
    /// for the formula.</summary>
    double SNR { get; }
}
