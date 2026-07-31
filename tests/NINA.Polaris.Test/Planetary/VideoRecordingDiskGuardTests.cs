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

using System;
using System.IO;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test.Planetary;

/// <summary>
/// FIELD8-4. Planetary video is the fastest writer in the app: 640x640 at
/// 16 bits and 130 fps is about 106 MB/s, so a disk with a few GB free fills
/// in under a minute. On 2026-07-31 a 4.10 GB clip landed on a root
/// filesystem that had 4.2 GB free; the board came back rebooted and the clip
/// was unreadable.
///
/// The free-space probe is the piece worth pinning: it has to answer for a
/// folder the writer has NOT created yet (the recorder makes it lazily), and
/// it has to fail soft, because refusing to record over a question we could
/// not ask would be worse than the risk.
/// </summary>
[TestFixture]
public class VideoRecordingDiskGuardTests {

    [Test]
    public void FreeBytesFor_ExistingDirectory_ReportsThatVolume() {
        var path = Path.Combine(Path.GetTempPath(), "polaris-disk-guard", "clip.ser");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try {
            long free = VideoRecordingService.FreeBytesFor(path);
            Assert.That(free, Is.GreaterThan(0));
            var expected = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            // Same volume, so the two readings agree within normal churn.
            Assert.That(free, Is.EqualTo(expected).Within((long)(expected * 0.05)));
        } finally {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }

    /// <summary>The recorder composes
    /// {ImageOutputDir}/planetary/{target}/{stamp}.ser and only creates that
    /// folder when the first frame arrives, so the pre-flight check runs
    /// against a path that does not exist yet. It must still find the volume.
    /// </summary>
    [Test]
    public void FreeBytesFor_DirectoryNotCreatedYet_WalksUpToTheVolume() {
        var path = Path.Combine(Path.GetTempPath(), "polaris-disk-guard-missing",
                                Guid.NewGuid().ToString("N"), "deeper", "clip.ser");
        Assert.That(Directory.Exists(Path.GetDirectoryName(path)), Is.False,
            "precondition: the target folder must not exist");

        long free = VideoRecordingService.FreeBytesFor(path);
        Assert.That(free, Is.GreaterThan(0),
            "an uncreated folder still resolves to a mounted filesystem");
    }

    [Test]
    public void FreeBytesFor_Garbage_ReturnsMinusOneInsteadOfThrowing() {
        Assert.That(VideoRecordingService.FreeBytesFor(""), Is.EqualTo(-1));
        Assert.That(VideoRecordingService.FreeBytesFor("\0invalid"), Is.EqualTo(-1));
    }
}
