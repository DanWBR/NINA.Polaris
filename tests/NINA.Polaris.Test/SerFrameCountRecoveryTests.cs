using System;
using System.IO;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// SERCOUNT-2: the SER header count is refreshed only every 100 frames and on
/// close, so an interrupted clip can claim FEWER frames than it holds. A field
/// clip said 1 frame and carried 2 plus their two timestamps. The reader trusts
/// the file length when the bytes past the last whole frame are exactly a
/// timestamp trailer for that many frames (or nothing); a partial frame keeps
/// the header's claim, and a header claiming MORE than the file holds is still
/// clamped as before.
/// </summary>
[TestFixture]
public class SerFrameCountRecoveryTests {
    private string _dir = null!;
    private const int W = 6, H = 4;
    private const int FrameBytes = W * H * 2;
    private const int CountOffset = 38;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-sercount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A proper clip with <paramref name="frames"/> frames and a
    /// timestamp trailer, then the header count overwritten with
    /// <paramref name="claimed"/>.</summary>
    private string Clip(string name, int frames, int claimed, int extraTailBytes = 0) {
        var path = Path.Combine(_dir, name);
        using (var w = new SerFileWriter(path, W, H, 16, SerColorMode.Mono, "Polaris", "TestCam", "TestScope")) {
            for (int f = 0; f < frames; f++) {
                var px = new ushort[W * H];
                for (int i = 0; i < px.Length; i++) px[i] = (ushort)(f * 100 + i);
                w.WriteFrame(px, DateTime.UtcNow.AddSeconds(f));
            }
        }
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            fs.Seek(CountOffset, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes(claimed), 0, 4);
            if (extraTailBytes > 0) {
                fs.Seek(0, SeekOrigin.End);
                fs.Write(new byte[extraTailBytes], 0, extraTailBytes);
            }
        }
        return path;
    }

    [Test]
    public void HeaderUndercount_WithTrailerForTheRealCount_IsRecovered() {
        var path = Clip("under.ser", frames: 2, claimed: 1);
        using var r = new SerFileReader(path);
        Assert.That(r.FrameCount, Is.EqualTo(2));
        Assert.That(r.RecoveredFrameCount, Is.True);
        Assert.That(r.TruncatedFrameCount, Is.EqualTo(0));
        Assert.That(r.ReadFrameAsUshort(1)[5], Is.EqualTo(105));   // second frame really is readable
    }

    [Test]
    public void HeaderCorrect_IsLeftAlone() {
        var path = Clip("exact.ser", frames: 2, claimed: 2);
        using var r = new SerFileReader(path);
        Assert.That(r.FrameCount, Is.EqualTo(2));
        Assert.That(r.RecoveredFrameCount, Is.False);
    }

    [Test]
    public void HeaderUndercount_ButOddTail_KeepsTheHeadersClaim() {
        // 2 frames, trailer for 2, plus 5 stray bytes: the tail is neither a
        // clean trailer nor empty, so the recovery declines to guess.
        var path = Clip("odd.ser", frames: 2, claimed: 1, extraTailBytes: 5);
        using var r = new SerFileReader(path);
        Assert.That(r.FrameCount, Is.EqualTo(1));
        Assert.That(r.RecoveredFrameCount, Is.False);
    }

    [Test]
    public void HeaderZero_StillRecovered() {
        var path = Clip("zero.ser", frames: 3, claimed: 0);
        using var r = new SerFileReader(path);
        Assert.That(r.FrameCount, Is.EqualTo(3));
        Assert.That(r.RecoveredFrameCount, Is.True);
    }

    [Test]
    public void HeaderOvercount_IsClampedAsTruncated() {
        var path = Clip("over.ser", frames: 2, claimed: 5);
        using var r = new SerFileReader(path);
        Assert.That(r.FrameCount, Is.EqualTo(2));
        Assert.That(r.TruncatedFrameCount, Is.EqualTo(5));
        Assert.That(r.RecoveredFrameCount, Is.False);
    }
}
