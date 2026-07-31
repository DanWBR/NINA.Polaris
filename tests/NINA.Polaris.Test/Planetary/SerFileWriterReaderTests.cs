// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using NUnit.Framework;
using NINA.Polaris.Services.Planetary;

namespace NINA.Polaris.Test.Planetary;

[TestFixture]
public class SerFileWriterReaderTests {
    private string _tempDir = null!;

    [SetUp]
    public void SetUp() {
        _tempDir = Path.Combine(Path.GetTempPath(), "polaris-ser-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Test]
    public void RoundTrip_16bitMono_PreservesPixels() {
        var path = Path.Combine(_tempDir, "mono16.ser");
        const int w = 8, h = 6;
        var frame = new ushort[w * h];
        for (int i = 0; i < frame.Length; i++) frame[i] = (ushort)(i * 257);  // 0, 257, 514, …

        using (var writer = new SerFileWriter(path, w, h, bitDepth: 16, SerColorMode.Mono,
            observer: "TestObs", instrument: "TestInst", telescope: "TestScope")) {
            writer.WriteFrame(frame, DateTime.UtcNow);
            writer.WriteFrame(frame, DateTime.UtcNow);
            writer.WriteFrame(frame, DateTime.UtcNow);
            Assert.That(writer.FrameCount, Is.EqualTo(3));
        }

        using var reader = new SerFileReader(path);
        Assert.That(reader.Width, Is.EqualTo(w));
        Assert.That(reader.Height, Is.EqualTo(h));
        Assert.That(reader.BitDepth, Is.EqualTo(16));
        Assert.That(reader.ColorMode, Is.EqualTo(SerColorMode.Mono));
        Assert.That(reader.FrameCount, Is.EqualTo(3));
        Assert.That(reader.Observer,   Is.EqualTo("TestObs"));
        Assert.That(reader.Instrument, Is.EqualTo("TestInst"));
        Assert.That(reader.Telescope,  Is.EqualTo("TestScope"));

        var read = reader.ReadFrameAsUshort(0);
        Assert.That(read, Has.Length.EqualTo(frame.Length));
        Assert.That(read, Is.EqualTo(frame));

        // All three frames identical in this test, but check that
        // reading frame 2 actually seeks to a different offset.
        var read2 = reader.ReadFrameAsUshort(2);
        Assert.That(read2, Is.EqualTo(frame));
    }

    [Test]
    public void Constructor_RejectsInvalidParams() {
        var path = Path.Combine(_tempDir, "bad.ser");
        Assert.Throws<ArgumentException>(() => new SerFileWriter(path, 0, 100, 16));
        Assert.Throws<ArgumentException>(() => new SerFileWriter(path, 100, 0, 16));
        Assert.Throws<ArgumentException>(() => new SerFileWriter(path, 100, 100, 12));  // not 8/16
    }

    [Test]
    public void WriteFrame_WrongSize_Throws() {
        var path = Path.Combine(_tempDir, "wrongsize.ser");
        using var writer = new SerFileWriter(path, 10, 10, 16);
        var tooSmall = new ushort[50];
        Assert.Throws<ArgumentException>(() => writer.WriteFrame(tooSmall));
    }

    [Test]
    public void Reader_RejectsNonSerFile() {
        var path = Path.Combine(_tempDir, "garbage.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        Assert.Throws<InvalidDataException>(() => new SerFileReader(path));
    }

    [Test]
    public void TimestampTrailer_RecordsRequestedUtc() {
        var path = Path.Combine(_tempDir, "ts.ser");
        var t1 = new DateTime(2026, 5, 22, 22, 30, 0, DateTimeKind.Utc);
        var t2 = t1.AddSeconds(1);
        var t3 = t1.AddSeconds(2);

        using (var writer = new SerFileWriter(path, 4, 4, 16)) {
            writer.WriteFrame(new ushort[16], t1);
            writer.WriteFrame(new ushort[16], t2);
            writer.WriteFrame(new ushort[16], t3);
        }
        using var reader = new SerFileReader(path);
        Assert.That(reader.TimestampOf(0), Is.EqualTo(t1));
        Assert.That(reader.TimestampOf(1), Is.EqualTo(t2));
        Assert.That(reader.TimestampOf(2), Is.EqualTo(t3));
    }

    [Test]
    public void Reader_BayerPattern_ReportsCorrectColorMode() {
        var path = Path.Combine(_tempDir, "bayer.ser");
        using (var writer = new SerFileWriter(path, 10, 10, 16, SerColorMode.BayerRGGB)) {
            writer.WriteFrame(new ushort[100]);
        }
        using var reader = new SerFileReader(path);
        Assert.That(reader.ColorMode, Is.EqualTo(SerColorMode.BayerRGGB));
    }

    /// <summary>FIELD8-3. A recording that never reaches Dispose keeps a header
    /// frame count of 0 while the frames themselves sit on disk. In the field
    /// (2026-07-31) two clips came out that way, 4.1 GB and 175 MB, and every
    /// reader called them empty. The bytes are the truth: recover the count
    /// from the file length.</summary>
    [Test]
    public void Reader_HeaderCountZeroButFramesPresent_RecoversFromFileLength() {
        var path = Path.Combine(_tempDir, "interrupted.ser");
        const int w = 8, h = 6;

        // Simulate the crash: write the header + frames by hand and never
        // patch the count, which is exactly the on-disk shape of the field
        // files (no trailer either).
        using (var writer = new SerFileWriter(path, w, h, 16, SerColorMode.Mono)) {
            for (int i = 0; i < 5; i++) writer.WriteFrame(new ushort[w * h]);
            // Dispose will patch the header, so undo that below.
        }
        WriteHeaderFrameCount(path, 0);
        // Drop the trailer Dispose wrote, so the file is header + frames only.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) {
            fs.SetLength(SerFileWriter.HeaderSize + 5L * w * h * 2);
        }

        Assert.That(ReadHeaderFrameCount(path), Is.EqualTo(0),
            "precondition: the header must still claim zero frames");

        using var reader = new SerFileReader(path);
        Assert.That(reader.FrameCount, Is.EqualTo(5), "frames on disk must be readable");
        Assert.That(reader.RecoveredFrameCount, Is.True, "the recovery should be reported");
        Assert.That(() => reader.ReadFrameAsUshort(4), Throws.Nothing);
    }

    /// <summary>The other direction: a file cut off mid-recording claims more
    /// frames than it holds, and reading the phantom ones throws deep in the
    /// decoder. The count has to come down to what is actually there.</summary>
    [Test]
    public void Reader_HeaderCountHigherThanFile_ClampsToWhatIsThere() {
        var path = Path.Combine(_tempDir, "truncated.ser");
        const int w = 8, h = 6;
        using (var writer = new SerFileWriter(path, w, h, 16, SerColorMode.Mono)) {
            for (int i = 0; i < 6; i++) writer.WriteFrame(new ushort[w * h]);
        }
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) {
            fs.SetLength(SerFileWriter.HeaderSize + 3L * w * h * 2);
        }

        using var reader = new SerFileReader(path);
        Assert.That(reader.FrameCount, Is.EqualTo(3));
        Assert.That(reader.TruncatedFrameCount, Is.EqualTo(6),
            "the header's claim is worth reporting");
        Assert.That(() => reader.ReadFrameAsUshort(2), Throws.Nothing);
    }

    /// <summary>The header count is refreshed while recording, so an
    /// interrupted clip is readable even by a reader that does not recover
    /// (other astro tools read these files too).</summary>
    [Test]
    public void Writer_RefreshesHeaderCountDuringRecording() {
        var path = Path.Combine(_tempDir, "live.ser");
        const int w = 4, h = 4;
        var writer = new SerFileWriter(path, w, h, 16, SerColorMode.Mono);
        try {
            for (int i = 0; i < 100; i++) writer.WriteFrame(new ushort[w * h]);
            Assert.That(ReadHeaderFrameCount(path), Is.EqualTo(100),
                "the header should carry the count without waiting for Dispose");
        } finally { writer.Dispose(); }
    }

    /// <summary>PLAN8. An 8-bit SER is the planetary norm and has to come back
    /// on the SAME scale as a 16-bit one, or every consumer downstream (quality
    /// metric, centroid, stacking accumulator) would need to know which kind of
    /// file it was handed. The convention is the camera backends': a RAW8
    /// sample is the TOP byte, widened with px &lt;&lt; 8.</summary>
    [Test]
    public void EightBit_RoundTrips_AndReadsBackOnTheSixteenBitScale() {
        var path = Path.Combine(_tempDir, "planet8.ser");
        const int w = 6, h = 4;
        var frame = new byte[w * h];
        for (int i = 0; i < frame.Length; i++) frame[i] = (byte)(i * 10);

        using (var writer = new SerFileWriter(path, w, h, bitDepth: 8, SerColorMode.Mono)) {
            writer.WriteFrame(frame, frame.Length, DateTime.UtcNow);
            writer.WriteFrame(frame, frame.Length, DateTime.UtcNow);
        }

        using var reader = new SerFileReader(path);
        Assert.That(reader.BitDepth, Is.EqualTo(8));
        Assert.That(reader.FrameCount, Is.EqualTo(2));

        var wide = reader.ReadFrameAsUshort(0);
        Assert.That(wide.Length, Is.EqualTo(w * h));
        for (int i = 0; i < wide.Length; i++) {
            Assert.That(wide[i], Is.EqualTo((ushort)(frame[i] << 8)),
                $"sample {i} must be left-aligned, not raw 0..255");
        }
    }

    /// <summary>Half the samples means half the file: the whole point of
    /// recording planetary video at 8 bits. Pinned because a regression here
    /// is silent, it just costs disk.</summary>
    [Test]
    public void EightBit_FileIsHalfTheSizeOfSixteenBit() {
        const int w = 32, h = 32, frames = 5;
        var eight = Path.Combine(_tempDir, "d8.ser");
        var sixteen = Path.Combine(_tempDir, "d16.ser");

        using (var a = new SerFileWriter(eight, w, h, 8, SerColorMode.Mono)) {
            for (int i = 0; i < frames; i++) a.WriteFrame(new byte[w * h], w * h, DateTime.UtcNow);
        }
        using (var b = new SerFileWriter(sixteen, w, h, 16, SerColorMode.Mono)) {
            for (int i = 0; i < frames; i++) b.WriteFrame(new ushort[w * h]);
        }

        long dataOnly(string p) => new FileInfo(p).Length - SerFileWriter.HeaderSize - frames * 8L;
        Assert.That(dataOnly(eight) * 2, Is.EqualTo(dataOnly(sixteen)));
    }

    private static int ReadHeaderFrameCount(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[42];
        fs.ReadExactly(buf, 0, buf.Length);
        return (int)BitConverter.ToUInt32(buf, 38);
    }

    private static void WriteHeaderFrameCount(string path, uint count) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.Seek(38, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(count), 0, 4);
    }

    [Test]
    public void Writer_CreatesDirectoryIfMissing() {
        // Sub-path that doesn't exist yet, writer should mkdir -p.
        var path = Path.Combine(_tempDir, "deep", "subdir", "video.ser");
        using (var writer = new SerFileWriter(path, 4, 4, 16)) {
            writer.WriteFrame(new ushort[16]);
        }
        Assert.That(File.Exists(path), Is.True);
    }
}