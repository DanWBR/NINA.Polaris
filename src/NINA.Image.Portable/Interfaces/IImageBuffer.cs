using NINA.Core.Enum;

namespace NINA.Image.Interfaces;

public interface IImageBuffer {
    int Width { get; }
    int Height { get; }
    int BitDepth { get; }
    BayerPatternEnum BayerPattern { get; }
    ReadOnlyMemory<ushort> PixelData { get; }
    byte[] ToJpeg(int quality = 85);
    byte[] ToLz4Compressed();
}
