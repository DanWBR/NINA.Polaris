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

using NINA.Polaris.Services;
using NINA.Polaris.Services.Storage;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class StoragePushTests {
    // ---- SHARESYNC: backfill routes recordings to the video lane ----
    // The one branchy bit of the backfill: a .ser/.avi/etc. must go to the
    // video lane (so a multi-GB clip can't head-of-line-block the night's subs),
    // everything else is an image.

    [TestCase("rig/target/planetary/jup_2026.ser", true)]
    [TestCase("clip.AVI", true)]
    [TestCase("timelapse.mp4", true)]
    [TestCase("pan.mov", true)]
    [TestCase("rig/target/lights/2026-06-19/light_1.fits", false)]
    [TestCase("master.xisf", false)]
    [TestCase("result.tif", false)]
    [TestCase("noext", false)]
    public void IsVideoFile_RoutesRecordingsToTheVideoLane(string path, bool expected) {
        Assert.That(StoragePushService.IsVideoFile(path), Is.EqualTo(expected));
    }

    // ---- StoragePath.Segments ----

    [Test]
    public void Segments_SplitsAndNormalisesSeparators() {
        var segs = StoragePath.Segments(@"rig\lights/M31\Ha/2026-06-19\light_1.fits");
        Assert.That(segs, Is.EqualTo(new[] { "rig", "lights", "M31", "Ha", "2026-06-19", "light_1.fits" }));
    }

    [Test]
    public void Segments_DropsEmptyAndDotParts() {
        var segs = StoragePath.Segments("./a//b/./c.fits");
        Assert.That(segs, Is.EqualTo(new[] { "a", "b", "c.fits" }));
    }

    [Test]
    public void Segments_RejectsParentTraversal() {
        Assert.Throws<ArgumentException>(() => StoragePath.Segments("a/../../etc/passwd"));
    }

    // ---- LocalStorageTarget: real round-trip mirroring the tree ----

    [Test]
    public async Task LocalTarget_MirrorsTreeAndCopies() {
        var src = Directory.CreateTempSubdirectory("polaris_src_");
        var dst = Directory.CreateTempSubdirectory("polaris_dst_");
        try {
            var srcFile = Path.Combine(src.FullName, "frame.fits");
            await File.WriteAllTextAsync(srcFile, "hello-fits");
            var rel = Path.Combine("rig", "lights", "M31", "frame.fits");

            using var target = new LocalStorageTarget();
            var cfg = new StorageConfig("local", "", 0, "", dst.FullName, "", "", "");
            await target.ConnectAsync(cfg, CancellationToken.None);
            await target.UploadAsync(srcFile, rel, CancellationToken.None);

            var expected = Path.Combine(dst.FullName, "rig", "lights", "M31", "frame.fits");
            Assert.That(File.Exists(expected), Is.True, "file mirrored into the tree");
            Assert.That(await File.ReadAllTextAsync(expected), Is.EqualTo("hello-fits"));
        } finally {
            src.Delete(true); dst.Delete(true);
        }
    }

    [Test]
    public async Task LocalTarget_Test_ReportsMissingPath() {
        using var target = new LocalStorageTarget();
        var cfg = new StorageConfig("local", "", 0, "", Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid()), "", "", "");
        var (ok, _) = await target.TestAsync(cfg, CancellationToken.None);
        Assert.That(ok, Is.False);
    }

    // ---- Factory ----

    [Test]
    public void Factory_MapsEachKind() {
        var f = new StorageTargetFactory();
        using (var t = f.Create("smb"))   Assert.That(t.Kind, Is.EqualTo("smb"));
        using (var t = f.Create("sftp"))  Assert.That(t.Kind, Is.EqualTo("sftp"));
        using (var t = f.Create("local")) Assert.That(t.Kind, Is.EqualTo("local"));
        Assert.Throws<NotSupportedException>(() => f.Create("ftp"));
    }

    // SHARESYNC-2: ListAsync backs the backfill pre-scan (enqueue only missing).
    [Test]
    public async Task LocalTarget_List_ReturnsRelPathsAndSizes() {
        var dst = Directory.CreateTempSubdirectory("polaris_list_");
        try {
            Directory.CreateDirectory(Path.Combine(dst.FullName, "rig", "lights", "M31"));
            await File.WriteAllTextAsync(Path.Combine(dst.FullName, "rig", "lights", "M31", "a.fits"), "abcde");
            await File.WriteAllTextAsync(Path.Combine(dst.FullName, "rig", "b.fits"), "xy");

            using var target = new LocalStorageTarget();
            var cfg = new StorageConfig("local", "", 0, "", dst.FullName, "", "", "");
            await target.ConnectAsync(cfg, CancellationToken.None);
            var map = await target.ListAsync(CancellationToken.None);

            Assert.That(map, Is.Not.Null);
            Assert.That(map!["rig/lights/M31/a.fits"], Is.EqualTo(5));
            Assert.That(map["rig/b.fits"], Is.EqualTo(2));
            Assert.That(map.Count, Is.EqualTo(2));
        } finally { dst.Delete(true); }
    }

    // SHARESYNC-2: the current-transfer progress bar is fed by UploadAsync's
    // IProgress; the local copy reports the whole file once at the end.
    [Test]
    public async Task LocalTarget_Upload_ReportsProgress() {
        var src = Directory.CreateTempSubdirectory("polaris_p_src_");
        var dst = Directory.CreateTempSubdirectory("polaris_p_dst_");
        try {
            var srcFile = Path.Combine(src.FullName, "f.fits");
            await File.WriteAllTextAsync(srcFile, "0123456789");   // 10 bytes
            using var target = new LocalStorageTarget();
            var cfg = new StorageConfig("local", "", 0, "", dst.FullName, "", "", "");
            await target.ConnectAsync(cfg, CancellationToken.None);

            long last = -1;
            var progress = new TestProgress(v => last = v);
            await target.UploadAsync(srcFile, "f.fits", CancellationToken.None, progress);
            Assert.That(last, Is.EqualTo(10));
        } finally { src.Delete(true); dst.Delete(true); }
    }

    private sealed class TestProgress : IProgress<long> {
        private readonly Action<long> _on;
        public TestProgress(Action<long> on) => _on = on;
        public void Report(long value) => _on(value);
    }

    [Test]
    public void LocalTarget_SkipsIdenticalLengthReupload() {
        // Re-pushing the same file is a no-op (no exception, content preserved).
        var src = Directory.CreateTempSubdirectory("polaris_src2_");
        var dst = Directory.CreateTempSubdirectory("polaris_dst2_");
        try {
            var srcFile = Path.Combine(src.FullName, "f.fits");
            File.WriteAllText(srcFile, "abc");
            using var target = new LocalStorageTarget();
            var cfg = new StorageConfig("local", "", 0, "", dst.FullName, "", "", "");
            target.ConnectAsync(cfg, CancellationToken.None).Wait();
            target.UploadAsync(srcFile, "f.fits", CancellationToken.None).Wait();
            Assert.DoesNotThrow(() => target.UploadAsync(srcFile, "f.fits", CancellationToken.None).Wait());
        } finally {
            src.Delete(true); dst.Delete(true);
        }
    }
}
