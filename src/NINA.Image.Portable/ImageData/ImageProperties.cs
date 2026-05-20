using NINA.Core.Enum;

namespace NINA.Image.ImageData;

public record ImageProperties {
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitDepth { get; init; }
    public bool IsBayered { get; init; }
    public BayerPatternEnum BayerPattern { get; init; } = BayerPatternEnum.None;

    public long PixelCount => (long)Width * Height;
}
