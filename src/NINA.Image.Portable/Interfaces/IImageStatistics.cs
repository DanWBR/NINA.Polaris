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
}
