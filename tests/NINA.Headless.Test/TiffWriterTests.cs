using NUnit.Framework;
using NINA.Image.FileFormat.TIFF;
using System.Buffers.Binary;

namespace NINA.Headless.Test;

/// <summary>
/// Pins the on-disk layout the STUDIO 16-bit linear TIFF export
/// produces. The downstream consumers (PixInsight, Siril, Photoshop)
/// expect baseline TIFF with the specific tag set we write; if any of
/// these values shift the round-trip breaks silently.
/// </summary>
[TestFixture]
public class TiffWriterTests {

    private string _tmpDir = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-tiff-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void Write16_StartsWithLittleEndianHeader() {
        var path = Path.Combine(_tmpDir, "test.tif");
        TiffWriter.Write16(new ushort[] { 1, 2, 3, 4 }, 2, 2, path);
        var bytes = File.ReadAllBytes(path);
        Assert.That(bytes[0], Is.EqualTo((byte)'I'));
        Assert.That(bytes[1], Is.EqualTo((byte)'I'));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)), Is.EqualTo(42),
            "TIFF magic should be 42");
    }

    [Test]
    public void Write16_PixelDataMatchesInput() {
        // Pixels go directly after the 8-byte header.
        var path = Path.Combine(_tmpDir, "pixels.tif");
        var input = new ushort[] { 0x0000, 0xFFFF, 0xABCD, 0x1234 };
        TiffWriter.Write16(input, 2, 2, path);
        var bytes = File.ReadAllBytes(path);
        for (int i = 0; i < input.Length; i++) {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8 + i * 2));
            Assert.That(v, Is.EqualTo(input[i]), $"pixel {i} mismatch");
        }
    }

    [Test]
    public void Write16_IfdHasBitsPerSample16() {
        // After header + pixel strip there should be an IFD with a
        // BitsPerSample (tag 258) entry whose value is 16.
        var path = Path.Combine(_tmpDir, "bits.tif");
        TiffWriter.Write16(new ushort[100], 10, 10, path);
        var bytes = File.ReadAllBytes(path);
        // Header points to IFD offset at bytes 4..7
        var ifdOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ifdOffset));
        Assert.That(entryCount, Is.EqualTo(12));
        bool foundBitsPerSample = false;
        for (int i = 0; i < entryCount; i++) {
            var entryOff = ifdOffset + 2 + i * 12;
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryOff));
            if (tag == 258) {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryOff + 8));
                Assert.That(value, Is.EqualTo(16));
                foundBitsPerSample = true;
                break;
            }
        }
        Assert.That(foundBitsPerSample, Is.True, "BitsPerSample (258) tag missing");
    }

    [Test]
    public void Write8_BitsPerSampleIs8() {
        var path = Path.Combine(_tmpDir, "8bit.tif");
        TiffWriter.Write8(new byte[100], 10, 10, path);
        var bytes = File.ReadAllBytes(path);
        var ifdOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        bool ok = false;
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ifdOffset));
        for (int i = 0; i < entryCount; i++) {
            var entryOff = ifdOffset + 2 + i * 12;
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryOff));
            if (tag == 258) {
                Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryOff + 8)),
                    Is.EqualTo(8));
                ok = true;
                break;
            }
        }
        Assert.That(ok, Is.True);
    }

    [Test]
    public void Write16_FileSizeIsHeaderPlusPixelsPlusIfd() {
        // 8 header + 100·2 pixels + (2 entryCount + 12·12 entries + 4 next + 16 rationals)
        var path = Path.Combine(_tmpDir, "size.tif");
        TiffWriter.Write16(new ushort[100], 10, 10, path);
        var size = new FileInfo(path).Length;
        Assert.That(size, Is.EqualTo(8 + 200 + 2 + 12 * 12 + 4 + 16));
    }
}
