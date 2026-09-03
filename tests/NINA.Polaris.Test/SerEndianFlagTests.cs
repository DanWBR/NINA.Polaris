using System;
using System.IO;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// SERENDIAN: the SER spec says LittleEndian = 1 means little-endian, but the
/// first implementations used the field the other way round and the tools
/// that matter (Siril, GoQat, ser-player, ZWO ASIVideoStack) follow that
/// inverted convention. Polaris wrote 1 with little-endian samples, so those
/// tools byte-swapped every frame (colour noise on a Moon clip). The writer
/// now emits 0; the reader keeps decoding every Polaris clip as LE; the
/// salvage tool rewrites legacy clips even when nothing else is wrong.
/// </summary>
[TestFixture]
public class SerEndianFlagTests {
    private string _dir = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-serendian-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const int FlagOffset = 22;

    private string WriteClip(string name, int frames, int max, out ushort[][] samples) {
        var path = Path.Combine(_dir, name);
        samples = new ushort[frames][];
        var rnd = new Random(42);
        using var w = new SerFileWriter(path, 6, 4, 16, SerColorMode.BayerRGGB, "Polaris", "TestCam", "TestScope");
        for (int f = 0; f < frames; f++) {
            var px = new ushort[6 * 4];
            for (int i = 0; i < px.Length; i++) px[i] = (ushort)rnd.Next(0, max + 1);
            px[0] = (ushort)max;
            samples[f] = px;
            w.WriteFrame(px, DateTime.UtcNow.AddSeconds(f));
        }
        return path;
    }

    private static int ReadFlag(string path) {
        using var fs = File.OpenRead(path);
        fs.Seek(FlagOffset, SeekOrigin.Begin);
        var b = new byte[4];
        fs.ReadExactly(b);
        return BitConverter.ToInt32(b, 0);
    }

    /// <summary>Turn a fresh clip into a pre-fix one: same LE samples, flag = 1.</summary>
    private static void StampLegacyFlag(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Seek(FlagOffset, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(1), 0, 4);
    }

    [Test]
    public void Writer_EmitsZero_TheInvertedDeFactoConvention() {
        var path = WriteClip("fresh.ser", 2, 65535, out _);
        Assert.That(ReadFlag(path), Is.EqualTo(0));
    }

    [Test]
    public void Reader_ExposesTheFlag_AndDecodesSamplesAsLittleEndianEitherWay() {
        var path = WriteClip("clip.ser", 3, 4095, out var samples);

        using (var fresh = new SerFileReader(path)) {
            Assert.That(fresh.HeaderLittleEndianFlag, Is.EqualTo(0));
            Assert.That(fresh.ReadFrameAsUshort(1), Is.EqualTo(samples[1]));
        }

        StampLegacyFlag(path);
        using (var legacy = new SerFileReader(path)) {
            Assert.That(legacy.HeaderLittleEndianFlag, Is.EqualTo(1));
            // A legacy Polaris clip is still little-endian data: no swap.
            Assert.That(legacy.ReadFrameAsUshort(1), Is.EqualTo(samples[1]));
        }
    }

    [Test]
    public void Rescale_LegacyFlag_FullRange_IsRewrittenWithFlagZero_SamplesUnchanged() {
        var src = WriteClip("legacy-full.ser", 3, 65535, out var samples);
        StampLegacyFlag(src);

        var res = SerRescale.Rescale(src, bitsOverride: null, outPath: null);

        Assert.That(res.Done, Is.True, res.Message);
        Assert.That(res.Shift, Is.EqualTo(0));
        Assert.That(res.Message, Does.Contain("endian"));
        Assert.That(ReadFlag(res.OutputPath!), Is.EqualTo(0));
        using var reader = new SerFileReader(res.OutputPath!);
        for (int f = 0; f < 3; f++)
            Assert.That(reader.ReadFrameAsUshort(f), Is.EqualTo(samples[f]));
    }

    [Test]
    public void Rescale_LegacyFlag_RightAligned12Bit_IsShiftedAndFlagFixed() {
        var src = WriteClip("legacy-12bit.ser", 2, 4095, out var samples);
        StampLegacyFlag(src);

        var res = SerRescale.Rescale(src, bitsOverride: null, outPath: null);

        Assert.That(res.Done, Is.True, res.Message);
        Assert.That(res.SignificantBits, Is.EqualTo(12));
        Assert.That(res.Shift, Is.EqualTo(4));
        Assert.That(ReadFlag(res.OutputPath!), Is.EqualTo(0));
        using var reader = new SerFileReader(res.OutputPath!);
        var got = reader.ReadFrameAsUshort(0);
        for (int i = 0; i < got.Length; i++)
            Assert.That(got[i], Is.EqualTo(Math.Min(0xFFFF, samples[0][i] << 4)));
    }

    [Test]
    public void Rescale_FreshFlag_FullRange_IsStillANoOp() {
        var src = WriteClip("fresh-full.ser", 2, 65535, out _);

        var res = SerRescale.Rescale(src, bitsOverride: null, outPath: null);

        Assert.That(res.Done, Is.False);
        Assert.That(res.OutputPath, Is.Null);
    }
}
