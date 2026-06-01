using K4os.Compression.LZ4;
using Moq;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

[TestFixture]
public class ImageBufferTests {
    private const int TestWidth = 32;
    private const int TestHeight = 32;
    private const int TestBitDepth = 16;

    private static ushort[] CreateGradientPixels(int width, int height) {
        var pixels = new ushort[width * height];
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                pixels[y * width + x] = (ushort)((x + y) * 256);
            }
        }
        return pixels;
    }

    private static Mock<IImageData> CreateMockImageData(int width, int height, int bitDepth,
        BayerPatternEnum bayer = BayerPatternEnum.None) {
        var pixels = CreateGradientPixels(width, height);

        var properties = new ImageProperties {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            BayerPattern = bayer
        };

        var mockStats = new Mock<IImageStatistics>();
        var mockImageData = new Mock<IImageData>();
        mockImageData.Setup(d => d.Data).Returns(pixels);
        mockImageData.Setup(d => d.Properties).Returns(properties);
        mockImageData.Setup(d => d.MetaData).Returns(new ImageMetaData());
        mockImageData.Setup(d => d.Statistics).Returns(mockStats.Object);

        return mockImageData;
    }

    // --- FromImageData ---

    [Test]
    public void FromImageData_SetsProperties() {
        var mockData = CreateMockImageData(TestWidth, TestHeight, TestBitDepth, BayerPatternEnum.RGGB);

        var buffer = ImageBuffer.FromImageData(mockData.Object);

        Assert.That(buffer.Width, Is.EqualTo(TestWidth));
        Assert.That(buffer.Height, Is.EqualTo(TestHeight));
        Assert.That(buffer.BitDepth, Is.EqualTo(TestBitDepth));
        Assert.That(buffer.BayerPattern, Is.EqualTo(BayerPatternEnum.RGGB));
        Assert.That(buffer.PixelData.Length, Is.EqualTo(TestWidth * TestHeight));
    }

    [Test]
    public void FromImageData_PixelDataMatchesSource() {
        var mockData = CreateMockImageData(TestWidth, TestHeight, TestBitDepth);
        var expectedPixels = mockData.Object.Data;

        var buffer = ImageBuffer.FromImageData(mockData.Object);

        var actualPixels = buffer.PixelData.ToArray();
        Assert.That(actualPixels, Is.EqualTo(expectedPixels));
    }

    // --- GetStreamHeader ---

    [Test]
    public void GetStreamHeader_HasCorrectFormat() {
        var pixels = CreateGradientPixels(TestWidth, TestHeight);
        var buffer = new ImageBuffer(pixels, TestWidth, TestHeight, TestBitDepth, BayerPatternEnum.None);

        var header = buffer.GetStreamHeader();

        // Header layout: 6 int32s = 24 bytes
        // (width, height, bit depth, bayer, uncompressed size, kind tag).
        // The trailing `kind` byte was added when the relay envelope grew
        // to distinguish live-stack vs preview vs sequence frames.
        Assert.That(header.Length, Is.EqualTo(24));

        using var ms = new MemoryStream(header);
        using var br = new BinaryReader(ms);
        Assert.That(br.ReadInt32(), Is.EqualTo(TestWidth), "Width");
        Assert.That(br.ReadInt32(), Is.EqualTo(TestHeight), "Height");
        Assert.That(br.ReadInt32(), Is.EqualTo(TestBitDepth), "BitDepth");
        Assert.That(br.ReadInt32(), Is.EqualTo((int)BayerPatternEnum.None), "BayerPattern");
        Assert.That(br.ReadInt32(), Is.EqualTo(pixels.Length * 2), "Uncompressed size");
        Assert.That(br.ReadInt32(), Is.EqualTo(0), "Kind tag default");
    }

    [Test]
    public void GetStreamHeader_WithBayer_EncodesBayerPattern() {
        var pixels = CreateGradientPixels(8, 8);
        var buffer = new ImageBuffer(pixels, 8, 8, 12, BayerPatternEnum.GBRG);

        var header = buffer.GetStreamHeader();

        using var ms = new MemoryStream(header);
        using var br = new BinaryReader(ms);
        Assert.That(br.ReadInt32(), Is.EqualTo(8), "Width");
        Assert.That(br.ReadInt32(), Is.EqualTo(8), "Height");
        Assert.That(br.ReadInt32(), Is.EqualTo(12), "BitDepth");
        Assert.That(br.ReadInt32(), Is.EqualTo((int)BayerPatternEnum.GBRG), "BayerPattern");
        Assert.That(br.ReadInt32(), Is.EqualTo(8 * 8 * 2), "Uncompressed size");
    }

    // --- LZ4 compression ---

    [Test]
    public void ToLz4Compressed_ProducesValidData() {
        var pixels = CreateGradientPixels(TestWidth, TestHeight);
        var buffer = new ImageBuffer(pixels, TestWidth, TestHeight, TestBitDepth);

        var compressed = buffer.ToLz4Compressed();

        // Compressed data should exist and be non-empty
        Assert.That(compressed, Is.Not.Null);
        Assert.That(compressed.Length, Is.GreaterThan(0));

        // Verify round-trip: decompress and compare
        int uncompressedSize = pixels.Length * 2;
        var decompressed = new byte[uncompressedSize];
        int decodedLen = LZ4Codec.Decode(compressed, decompressed);
        Assert.That(decodedLen, Is.EqualTo(uncompressedSize));

        // Convert back to ushort array and compare
        var roundTripped = new ushort[pixels.Length];
        Buffer.BlockCopy(decompressed, 0, roundTripped, 0, uncompressedSize);
        Assert.That(roundTripped, Is.EqualTo(pixels));
    }

    [Test]
    public void ToLz4Compressed_SmallerThanRaw_ForRedundantData() {
        // Create highly redundant data (all same value) which should compress well
        var pixels = new ushort[256 * 256];
        Array.Fill(pixels, (ushort)1000);
        var buffer = new ImageBuffer(pixels, 256, 256, 16);

        var compressed = buffer.ToLz4Compressed();
        int rawSize = pixels.Length * 2;

        Assert.That(compressed.Length, Is.LessThan(rawSize),
            "LZ4 compressed data should be smaller than raw data for redundant input");
    }

    // --- JPEG encoding ---

    [Test]
    public void ToJpeg_ProducesValidJpeg() {
        var pixels = CreateGradientPixels(TestWidth, TestHeight);
        var buffer = new ImageBuffer(pixels, TestWidth, TestHeight, TestBitDepth);

        var jpeg = buffer.ToJpeg();

        Assert.That(jpeg, Is.Not.Null);
        Assert.That(jpeg.Length, Is.GreaterThan(2));

        // JPEG magic bytes: starts with FF D8
        Assert.That(jpeg[0], Is.EqualTo(0xFF), "JPEG should start with 0xFF");
        Assert.That(jpeg[1], Is.EqualTo(0xD8), "JPEG second byte should be 0xD8");

        // JPEG end marker: FF D9
        Assert.That(jpeg[^2], Is.EqualTo(0xFF), "JPEG should end with 0xFF D9");
        Assert.That(jpeg[^1], Is.EqualTo(0xD9), "JPEG last byte should be 0xD9");
    }

    [Test]
    public void ToJpeg_ProducesNonTrivialOutput() {
        var pixels = CreateGradientPixels(64, 64);
        var buffer = new ImageBuffer(pixels, 64, 64, TestBitDepth);

        var jpeg = buffer.ToJpeg(quality: 50);

        // Even a small image should produce a reasonable JPEG
        Assert.That(jpeg.Length, Is.GreaterThan(100),
            "JPEG output should be more than 100 bytes for a 64x64 image");
    }

    // --- Constructor validation ---

    [Test]
    public void Constructor_NullPixels_Throws() {
        Assert.Throws<ArgumentNullException>(() =>
            new ImageBuffer(null!, 10, 10));
    }

    [Test]
    public void Constructor_SetsAllProperties() {
        var pixels = new ushort[100];
        var buffer = new ImageBuffer(pixels, 10, 10, 12, BayerPatternEnum.BGGR);

        Assert.That(buffer.Width, Is.EqualTo(10));
        Assert.That(buffer.Height, Is.EqualTo(10));
        Assert.That(buffer.BitDepth, Is.EqualTo(12));
        Assert.That(buffer.BayerPattern, Is.EqualTo(BayerPatternEnum.BGGR));
    }
}
