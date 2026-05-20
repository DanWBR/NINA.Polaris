using K4os.Compression.LZ4;
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;

namespace NINA.Image.ImageData;

public class ImageBuffer : IImageBuffer {
    private readonly ushort[] _pixels;

    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
    public BayerPatternEnum BayerPattern { get; }
    public ReadOnlyMemory<ushort> PixelData => _pixels;

    public ImageBuffer(ushort[] pixels, int width, int height, int bitDepth = 16,
        BayerPatternEnum bayerPattern = BayerPatternEnum.None) {
        _pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        BayerPattern = bayerPattern;
    }

    public static ImageBuffer FromImageData(IImageData imageData) {
        return new ImageBuffer(
            imageData.Data,
            imageData.Properties.Width,
            imageData.Properties.Height,
            imageData.Properties.BitDepth,
            imageData.Properties.BayerPattern);
    }

    public byte[] ToLz4Compressed() {
        var sourceBytes = new byte[_pixels.Length * 2];
        Buffer.BlockCopy(_pixels, 0, sourceBytes, 0, sourceBytes.Length);

        int maxLen = LZ4Codec.MaximumOutputSize(sourceBytes.Length);
        var compressed = new byte[maxLen];
        int compressedLen = LZ4Codec.Encode(sourceBytes, compressed, LZ4Level.L00_FAST);

        var result = new byte[compressedLen];
        Array.Copy(compressed, result, compressedLen);
        return result;
    }

    public byte[] GetStreamHeader() {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Width);
        bw.Write(Height);
        bw.Write(BitDepth);
        bw.Write((int)BayerPattern);
        bw.Write(_pixels.Length * 2); // uncompressed size in bytes
        return ms.ToArray();
    }

    public byte[] ToJpeg(int quality = 85) {
        var stretched = AutoStretch.Apply(_pixels, Width, Height, BitDepth);
        return JpegHelper.EncodeGrayscale(stretched, Width, Height, quality);
    }
}
