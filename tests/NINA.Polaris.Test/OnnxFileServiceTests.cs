using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services.Onnx;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the OnnxFileService load → save round-trip. The service is
/// the only bridge between FITS on disk + the browser pipeline; a
/// regression here either silently truncates pixels (LE bytes vs
/// system-endian confusion) or writes an unreadable sibling, both
/// of which would surface to the user as a corrupt result file.
/// </summary>
[TestFixture]
public class OnnxFileServiceTests {
    private string _tempDir = "";

    [SetUp]
    public void SetUp() {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "polaris-onnx-file-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tempDir)) {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task LoadRawAsync_RoundTripsFitsToBytes() {
        // Synthesise a tiny FITS with a known gradient. The bytes we
        // get back should equal the original ushort[] data interpreted
        // as little-endian uint16.
        var path = WriteFakeFits(width: 8, height: 4, fill: i => (ushort)(i * 257));
        var svc = new OnnxFileService(NullLogger<OnnxFileService>.Instance);
        var raw = await svc.LoadRawAsync(path);

        Assert.That(raw, Is.Not.Null);
        Assert.That(raw!.Width, Is.EqualTo(8));
        Assert.That(raw.Height, Is.EqualTo(4));
        Assert.That(raw.Channels, Is.EqualTo(1));
        Assert.That(raw.PixelsLE16.Length, Is.EqualTo(8 * 4 * 2));

        // First pixel of our gradient was 0 → bytes [0, 0]; pixel 5
        // was 5*257=1285 → LE bytes [5, 5]; pixel 31 = 31*257=7967
        // = 0x1F1F → LE bytes [0x1F, 0x1F].
        Assert.That(raw.PixelsLE16[0], Is.EqualTo(0));
        Assert.That(raw.PixelsLE16[1], Is.EqualTo(0));
        Assert.That(raw.PixelsLE16[10], Is.EqualTo(5));
        Assert.That(raw.PixelsLE16[11], Is.EqualTo(5));
        Assert.That(raw.PixelsLE16[62], Is.EqualTo(0x1F));
        Assert.That(raw.PixelsLE16[63], Is.EqualTo(0x1F));
    }

    [Test]
    public async Task LoadRawAsync_MissingFile_ReturnsNull() {
        var svc = new OnnxFileService(NullLogger<OnnxFileService>.Instance);
        Assert.That(await svc.LoadRawAsync(Path.Combine(_tempDir, "ghost.fits")),
                    Is.Null);
    }

    [Test]
    public async Task SaveSiblingAsync_WritesUniqueSibling() {
        var src = WriteFakeFits(width: 4, height: 4, fill: _ => (ushort)42);
        var svc = new OnnxFileService(NullLogger<OnnxFileService>.Instance);

        // Hand back the same bytes — for a real run these would be
        // post-inference outputs; the writer doesn't care.
        var bytes = new byte[4 * 4 * 2];
        for (int i = 0; i < 16; i++) {
            bytes[i * 2]     = (byte)i;
            bytes[i * 2 + 1] = 0;
        }
        var firstOut = await svc.SaveSiblingAsync(src, "_bge", bytes, 4, 4, 1);
        Assert.That(firstOut, Is.Not.Null);
        Assert.That(File.Exists(firstOut!));
        Assert.That(Path.GetFileName(firstOut), Does.EndWith("_bge.fits"));

        // Second save should auto-number to _bge_2.fits so previous
        // runs aren't overwritten silently.
        var secondOut = await svc.SaveSiblingAsync(src, "_bge", bytes, 4, 4, 1);
        Assert.That(secondOut, Is.Not.Null);
        Assert.That(secondOut, Is.Not.EqualTo(firstOut));
        Assert.That(Path.GetFileName(secondOut!), Does.Contain("_bge_2"));
    }

    [Test]
    public async Task SaveSiblingAsync_DimensionMismatch_ReturnsNull() {
        var src = WriteFakeFits(4, 4, _ => (ushort)0);
        var svc = new OnnxFileService(NullLogger<OnnxFileService>.Instance);
        var bytes = new byte[10];  // way too small
        var path = await svc.SaveSiblingAsync(src, "_bge", bytes, 4, 4, 1);
        Assert.That(path, Is.Null, "Mismatched buffer length must be rejected");
    }

    [Test]
    public async Task LoadRawAsync_RgbFits_ReturnsThreeChannels() {
        // GX-9: a 3-channel FITS should round-trip as channels=3 with
        // pixels in plane-sequential order (R, then G, then B).
        var path = WriteFakeRgbFits(width: 4, height: 4,
            fill: (i, c) => (ushort)((c + 1) * 1000 + i));
        var svc = new OnnxFileService(NullLogger<OnnxFileService>.Instance);
        var raw = await svc.LoadRawAsync(path);

        Assert.That(raw, Is.Not.Null);
        Assert.That(raw!.Channels, Is.EqualTo(3));
        Assert.That(raw.Width, Is.EqualTo(4));
        Assert.That(raw.Height, Is.EqualTo(4));
        Assert.That(raw.PixelsLE16.Length, Is.EqualTo(4 * 4 * 3 * 2));

        // R plane first pixel: c=0, i=0 → 1000 → LE 0xE8, 0x03
        Assert.That(raw.PixelsLE16[0], Is.EqualTo(0xE8));
        Assert.That(raw.PixelsLE16[1], Is.EqualTo(0x03));
        // G plane first pixel: offset = 4*4*2 = 32; c=1, i=0 → 2000 → LE 0xD0, 0x07
        Assert.That(raw.PixelsLE16[32], Is.EqualTo(0xD0));
        Assert.That(raw.PixelsLE16[33], Is.EqualTo(0x07));
        // B plane first pixel: offset = 64; c=2, i=0 → 3000 → LE 0xB8, 0x0B
        Assert.That(raw.PixelsLE16[64], Is.EqualTo(0xB8));
        Assert.That(raw.PixelsLE16[65], Is.EqualTo(0x0B));
    }

    [Test]
    public async Task SaveSiblingAsync_ThreeChannel_WritesRgbFits() {
        var src = WriteFakeFits(4, 4, _ => (ushort)0);
        var svc = new OnnxFileService(NullLogger<OnnxFileService>.Instance);
        var bytes = new byte[4 * 4 * 3 * 2];
        // distinct value per channel so the reader confirms ordering
        for (int c = 0; c < 3; c++) {
            for (int i = 0; i < 16; i++) {
                int off = (c * 16 + i) * 2;
                ushort v = (ushort)((c + 1) * 100 + i);
                bytes[off]     = (byte)(v & 0xFF);
                bytes[off + 1] = (byte)(v >> 8);
            }
        }
        var outPath = await svc.SaveSiblingAsync(src, "_bge", bytes, 4, 4, 3);
        Assert.That(outPath, Is.Not.Null);
        BaseImageData reRead;
        using (var fs = File.OpenRead(outPath!)) reRead = FITSReader.Read(fs);
        Assert.That(reRead.Properties.Channels, Is.EqualTo(3));
        Assert.That(reRead.Properties.Width,  Is.EqualTo(4));
        Assert.That(reRead.Properties.Height, Is.EqualTo(4));
        Assert.That(reRead.Data.Length, Is.EqualTo(4 * 4 * 3));
        // Spot-check per-channel ordering survives writer→reader round-trip.
        Assert.That(reRead.Data[0],  Is.EqualTo((ushort)100));  // R plane[0]
        Assert.That(reRead.Data[16], Is.EqualTo((ushort)200));  // G plane[0]
        Assert.That(reRead.Data[32], Is.EqualTo((ushort)300));  // B plane[0]
    }

    [Test]
    public async Task SaveSiblingAsync_RoundTripsPixelsThroughFitsReader() {
        // End-to-end: write a sibling, then re-read it via FITSReader
        // and assert the pixel data we shipped matches what we get
        // back. Catches BITPIX / BZERO / byte-order mistakes that
        // would visibly corrupt the output to the user.
        var src = WriteFakeFits(8, 4, _ => (ushort)0);
        var svc = new OnnxFileService(NullLogger<OnnxFileService>.Instance);

        var inBytes = new byte[8 * 4 * 2];
        for (int i = 0; i < 32; i++) {
            // value = i * 1000 fits in uint16
            var v = (ushort)(i * 1000);
            inBytes[i * 2]     = (byte)(v & 0xFF);
            inBytes[i * 2 + 1] = (byte)(v >> 8);
        }
        var outPath = await svc.SaveSiblingAsync(src, "_denoise", inBytes, 8, 4, 1);
        Assert.That(outPath, Is.Not.Null);

        BaseImageData reRead;
        using (var fs = File.OpenRead(outPath!)) reRead = FITSReader.Read(fs);
        Assert.That(reRead.Properties.Width,  Is.EqualTo(8));
        Assert.That(reRead.Properties.Height, Is.EqualTo(4));
        for (int i = 0; i < 32; i++) {
            Assert.That(reRead.Data[i], Is.EqualTo((ushort)(i * 1000)),
                $"Pixel {i} differs after FITS round-trip");
        }
    }

    // ─── helpers ────────────────────────────────────────────────────

    /// <summary>Write a synthetic FITS with the given dimensions and a
    /// per-index fill function. Returns the absolute path.</summary>
    private string WriteFakeFits(int width, int height, Func<int, ushort> fill) {
        var data = new ushort[width * height];
        for (int i = 0; i < data.Length; i++) data[i] = fill(i);
        var props = new ImageProperties {
            Width = width, Height = height,
            BitDepth = 16, IsBayered = false,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
            Channels = 1,
        };
        var img = new BaseImageData(data, props);
        var path = Path.Combine(_tempDir, "src_" + Guid.NewGuid().ToString("N") + ".fits");
        FITSWriter.Write(img, path);
        return path;
    }

    /// <summary>Write a synthetic 3-channel RGB FITS. fill(i, c) returns
    /// the pixel for plane index c (0=R, 1=G, 2=B) at flat position i.
    /// Data is plane-sequential: [R0..R_n, G0..G_n, B0..B_n].</summary>
    private string WriteFakeRgbFits(int width, int height, Func<int, int, ushort> fill) {
        var plane = width * height;
        var data = new ushort[plane * 3];
        for (int c = 0; c < 3; c++) {
            for (int i = 0; i < plane; i++) data[c * plane + i] = fill(i, c);
        }
        var props = new ImageProperties {
            Width = width, Height = height,
            BitDepth = 16, IsBayered = false,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
            Channels = 3,
        };
        var img = new BaseImageData(data, props);
        var path = Path.Combine(_tempDir, "src_rgb_" + Guid.NewGuid().ToString("N") + ".fits");
        FITSWriter.Write(img, path);
        return path;
    }
}
