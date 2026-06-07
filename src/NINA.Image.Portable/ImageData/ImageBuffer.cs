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

    /// <summary>FIELD-2: same as <see cref="FromImageData(IImageData)"/>
    /// but lets the caller force a Bayer mosaic that overrides what the
    /// camera / FITS header reports. Used by ImageRelayService when the
    /// active rig has a BayerPatternOverride set, so the client-side
    /// debayer receives the right pattern even when the driver lies.
    /// Pass null to honour the source pattern (auto-detect).</summary>
    public static ImageBuffer FromImageData(IImageData imageData,
                                             BayerPatternEnum? bayerOverride) {
        return new ImageBuffer(
            imageData.Data,
            imageData.Properties.Width,
            imageData.Properties.Height,
            imageData.Properties.BitDepth,
            bayerOverride ?? imageData.Properties.BayerPattern);
    }

    public byte[] ToLz4Compressed() {
        // PERF #365: rent the two scratch buffers (the ushort->byte copy
        // and the LZ4 max-size target) from the shared ArrayPool instead
        // of allocating them per frame. On the streaming path this used to
        // churn ~3 large arrays/frame (tens of MB on a full-frame OSC),
        // pressuring the Pi GC. Only the trimmed `result` is a real
        // allocation now (it outlives this call — it's sent over the WS).
        int srcLen = _pixels.Length * 2;
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var sourceBytes = pool.Rent(srcLen);
        try {
            Buffer.BlockCopy(_pixels, 0, sourceBytes, 0, srcLen);
            int maxLen = LZ4Codec.MaximumOutputSize(srcLen);
            var compressed = pool.Rent(maxLen);
            try {
                // Rented arrays are oversized; slice to exact lengths so
                // the codec compresses the right source span.
                int compressedLen = LZ4Codec.Encode(
                    sourceBytes.AsSpan(0, srcLen),
                    compressed.AsSpan(0, maxLen),
                    LZ4Level.L00_FAST);
                var result = new byte[compressedLen];
                Array.Copy(compressed, result, compressedLen);
                return result;
            } finally {
                pool.Return(compressed);
            }
        } finally {
            pool.Return(sourceBytes);
        }
    }

    /// <summary>
    /// Build the binary header that precedes the LZ4 payload on the
    /// /ws/image-stream raw channel. Layout (little-endian):
    ///   off 0   int Width
    ///   off 4   int Height
    ///   off 8   int BitDepth
    ///   off 12  int BayerPattern (enum int)
    ///   off 16  int Uncompressed pixel bytes
    ///   off 20  int FrameKind (0 = stackable LIVE frame, 1 = PREVIEW
    ///                          / one-off snap — client must skip the
    ///                          WASM stacker for these)
    /// The header length is sent as a uint32 BEFORE this blob (in the
    /// relay envelope), so the client can extend / shrink the layout
    /// in future without breaking older builds — old clients that read
    /// fixed offsets 0..16 keep working as long as the prefix layout
    /// is preserved.
    /// </summary>
    public byte[] GetStreamHeader(int kind = 0) {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Width);
        bw.Write(Height);
        bw.Write(BitDepth);
        bw.Write((int)BayerPattern);
        bw.Write(_pixels.Length * 2); // uncompressed size in bytes
        bw.Write(kind);
        return ms.ToArray();
    }

    public byte[] ToJpeg(int quality = 85) {
        var stretched = AutoStretch.Apply(_pixels, Width, Height, BitDepth);
        return JpegHelper.EncodeGrayscale(stretched, Width, Height, quality);
    }
}
