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
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Which disks the capture-disk card is allowed to suggest.
///
/// Field report, Orange Pi 4 Pro, 2026-08-08: a host that had been writing to
/// its NVMe for weeks offered to set the NVMe up again. The candidate list only
/// excluded the boot disk, so the disk already carrying the captures was still
/// in it, and "mount this disk" was being offered for a disk that is mounted.
/// </summary>
[TestFixture]
public class StorageOfferTests {

    private static StorageSetupService.Candidate Disk(
            string device, string mountPoint = "", bool onBootDisk = false) =>
        new(Device: device,
            Uuid: "uuid-" + device.Replace("/dev/", ""),
            FsType: "ext4",
            Label: "",
            SizeBytes: 1_000_000_000_000,
            Model: "Samsung SSD 990 PRO 1TB",
            MountPoint: mountPoint,
            Removable: false,
            OnBootDisk: onBootDisk);

    /// <summary>The reported bug.</summary>
    [Test]
    public void TheDiskAlreadyHoldingTheCapturesIsNotOffered() {
        var all = new[] { Disk("/dev/nvme0n1p1", "/data") };

        var offered = StorageSetupService.Offerable(all, "/dev/nvme0n1p1");

        Assert.That(offered, Is.Empty,
            "offering to mount the filesystem the frames are already being "
            + "written to is the field report: a configured host asking to be "
            + "configured");
    }

    /// <summary>A second, genuinely unused disk still has to show up, or the
    /// filter would have thrown out the feature along with the bug.</summary>
    [Test]
    public void AnotherIdleDiskIsStillOffered() {
        var all = new[] {
            Disk("/dev/nvme0n1p1", "/data"),
            Disk("/dev/sda1"),
        };

        var offered = StorageSetupService.Offerable(all, "/dev/nvme0n1p1");

        Assert.That(offered.Select(c => c.Device), Is.EqualTo(new[] { "/dev/sda1" }));
    }

    [Test]
    public void TheBootDiskIsNeverOffered() {
        var all = new[] { Disk("/dev/mmcblk0p2", "/home", onBootDisk: true) };

        Assert.That(StorageSetupService.Offerable(all, "/dev/nvme0n1p1"), Is.Empty);
    }

    /// <summary>Captures still on the boot disk, nothing resolved: the case the
    /// card exists for. Every candidate must survive.</summary>
    [TestCase("")]
    [TestCase("   ")]
    public void WithNoResolvedCaptureFilesystemEveryCandidateSurvives(string source) {
        var all = new[] { Disk("/dev/nvme0n1p1"), Disk("/dev/sda1") };

        Assert.That(StorageSetupService.Offerable(all, source).Count, Is.EqualTo(2));
    }

    /// <summary>Matching is on the device node findmnt resolved, not on the
    /// mount point: a capture root under /data/files, a bind mount, or a
    /// symlink all report the same SOURCE but different paths.</summary>
    [Test]
    public void MatchingIsOnTheDeviceNodeNotTheMountPoint() {
        var all = new[] { Disk("/dev/nvme0n1p1", "/data") };

        Assert.That(StorageSetupService.Offerable(all, "/data"), Is.Not.Empty,
            "a mount point is not a device node; passing one must not silently "
            + "match, or the filter would depend on how the path was spelled");
        Assert.That(StorageSetupService.Offerable(all, "/dev/nvme0n1p1"), Is.Empty);
    }
}
