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
}
