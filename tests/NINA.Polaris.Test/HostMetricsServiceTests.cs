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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.ResourceMonitoring;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using System.Collections.Generic;
using System.Diagnostics;

#pragma warning disable EXTOBS0001

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the HostMetricsService snapshot shape + the public Sample()
/// method's behaviour around process CPU computation. The full
/// BackgroundService loop is not exercised here, it needs a live
/// IResourceMonitor implementation which only resolves via the real
/// DI graph. Tests use a hand-rolled stub that returns canned
/// utilisation numbers.
/// </summary>
[TestFixture]
public class HostMetricsServiceTests {

    private sealed class StubResourceMonitor : IResourceMonitor {
        public double Cpu = 12.3;
        public double Mem = 45.6;
        private const ulong MaxMemBytes = 1_000_000_000;
        public ResourceUtilization GetUtilization(TimeSpan window) {
            // ResourceUtilization.MemoryUsedPercentage is COMPUTED
            // inside the type as 100 * memoryUsedInBytes /
            // maximumMemoryInBytes. To stub a specific percentage
            // we have to back-solve the bytes value.
            var bytes = (ulong)(Mem / 100.0 * MaxMemBytes);
            return new ResourceUtilization(
                cpuUsedPercentage: Cpu,
                memoryUsedInBytes: bytes,
                systemResources: new SystemResources(
                    guaranteedCpuUnits: 1.0,
                    maximumCpuUnits: 1.0,
                    guaranteedMemoryInBytes: MaxMemBytes,
                    maximumMemoryInBytes: MaxMemBytes));
        }
    }

    private static ProfileService MakeProfileService() {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        return new ProfileService(cfg, NullLogger<ProfileService>.Instance);
    }

    [Test]
    public void Snapshot_Defaults_AreZero() {
        // Before the first sample, Latest holds the zero record.
        // The UI handles that as "no data yet", important the
        // service doesn't expose null or throw.
        var stub = new StubResourceMonitor();
        var svc = new HostMetricsService(stub, MakeProfileService(),
            new FileBrowserService(NullLogger<FileBrowserService>.Instance),
            NullLogger<HostMetricsService>.Instance);
        Assert.That(svc.Latest, Is.Not.Null);
        Assert.That(svc.Latest.CpuPercent, Is.EqualTo(0));
        Assert.That(svc.Latest.MemoryUsedMB, Is.EqualTo(0));
        Assert.That(svc.Latest.ProcessCpuPercent, Is.EqualTo(0));
    }

    [Test]
    public void Sample_PopulatesAllFields() {
        // Drive Sample directly with the stub. We can't predict
        // ProcessCpuPercent precisely (depends on host scheduling),
        // but every other field has a deterministic source.
        var stub = new StubResourceMonitor { Cpu = 25.5, Mem = 60.0 };
        var svc = new HostMetricsService(stub, MakeProfileService(),
            new FileBrowserService(NullLogger<FileBrowserService>.Instance),
            NullLogger<HostMetricsService>.Instance);

        var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastTime = DateTime.UtcNow.AddSeconds(-1);   // small but positive window
        var snap = svc.Sample(proc, ref lastCpu, ref lastTime,
            coreCount: Environment.ProcessorCount);

        Assert.That(snap.CpuPercent, Is.EqualTo(25.5));
        // MemoryPercent deliberately does NOT come from the stub: on
        // Linux it reads /proc/meminfo and elsewhere GCMemoryInfo's
        // OS-sourced MemoryLoadBytes (the IResourceMonitor value is
        // process-scoped on Windows and wrong for a system display).
        // The stub only feeds the last-resort branch when no GC has
        // run yet — which is exactly why the old Is.EqualTo(60.0)
        // passed in isolation and failed in the full suite (earlier
        // tests trigger GCs and populate GCMemoryInfo).
        Assert.That(snap.MemoryPercent, Is.InRange(0.0, 100.0));
        Assert.That(snap.MemoryTotalMB, Is.GreaterThan(0));
        Assert.That(snap.MemoryUsedMB, Is.GreaterThan(0));
        Assert.That(snap.ProcessMemoryMB, Is.GreaterThan(0),
            "Polaris process must have non-zero working set");
        Assert.That(snap.ProcessCpuPercent, Is.InRange(0.0, 100.0),
            "Process CPU must be clamped to [0, 100]");
        Assert.That(snap.SampledAt, Is.GreaterThan(DateTime.UtcNow.AddSeconds(-5)),
            "Snapshot timestamp should be recent");
    }

    [Test]
    public void Sample_RoundedToOneDecimal() {
        // UI doesn't need sub-percent resolution and jittery numbers
        // (38.213% → 38.198% → 38.241%) look broken. Round at the
        // source.
        var stub = new StubResourceMonitor { Cpu = 38.21385, Mem = 12.94912 };
        var svc = new HostMetricsService(stub, MakeProfileService(),
            new FileBrowserService(NullLogger<FileBrowserService>.Instance),
            NullLogger<HostMetricsService>.Instance);

        var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastTime = DateTime.UtcNow.AddSeconds(-1);
        var snap = svc.Sample(proc, ref lastCpu, ref lastTime, coreCount: 4);

        Assert.That(snap.CpuPercent, Is.EqualTo(38.2));
        // MemoryPercent comes from the OS, not the stub (see note in
        // Sample_PopulatesAllFields) — assert only the rounding.
        Assert.That(snap.MemoryPercent,
            Is.EqualTo(Math.Round(snap.MemoryPercent, 1)),
            "MemoryPercent must be rounded to one decimal at the source");
    }

    [Test]
    public void Sample_AdvancesCpuTracker() {
        // The two ref parameters must be updated so the next call
        // computes ProcessCpuPercent from the right window. If they
        // don't advance, every subsequent sample would compare
        // against the original baseline and the % drifts upward.
        var stub = new StubResourceMonitor();
        var svc = new HostMetricsService(stub, MakeProfileService(),
            new FileBrowserService(NullLogger<FileBrowserService>.Instance),
            NullLogger<HostMetricsService>.Instance);

        var proc = Process.GetCurrentProcess();
        var lastCpu = TimeSpan.FromSeconds(10);   // fake baseline
        var lastTime = DateTime.UtcNow.AddSeconds(-2);

        svc.Sample(proc, ref lastCpu, ref lastTime, coreCount: 4);

        Assert.That(lastCpu, Is.Not.EqualTo(TimeSpan.FromSeconds(10)),
            "lastCpu should be advanced to the current process CPU time");
        Assert.That(lastTime, Is.GreaterThan(DateTime.UtcNow.AddSeconds(-1)),
            "lastTime should be advanced to ~now");
    }

    // The mount table from the field report: the capture root sits on an NVMe,
    // and the gauge showed the SD card the system booted from.
    private static readonly string[] PiMounts = {
        "/dev/mmcblk0p2 / ext4 rw,relatime 0 0",
        "/dev/mmcblk0p1 /boot/firmware vfat rw 0 0",
        "/dev/nvme0n1p1 /mnt/nvme ext4 rw,relatime 0 0",
        "tmpfs /run tmpfs rw,nosuid 0 0",
    };

    [Test]
    public void ResolveMountFrom_PicksTheNvme_NotTheRootFilesystem() {
        var (mount, device) = HostMetricsService.ResolveMountFrom(PiMounts, "/mnt/nvme/files");
        Assert.That(mount, Is.EqualTo("/mnt/nvme"),
            "the longest mountpoint containing the path is the NVMe, not \"/\"");
        Assert.That(device, Is.EqualTo("/dev/nvme0n1p1"));
    }

    [Test]
    public void ResolveMountFrom_FallsBackToRoot_WhenNothingElseContainsThePath() {
        var (mount, device) = HostMetricsService.ResolveMountFrom(PiMounts, "/home/polaris/files");
        Assert.That(mount, Is.EqualTo("/"));
        Assert.That(device, Is.EqualTo("/dev/mmcblk0p2"));
    }

    [Test]
    public void ResolveMountFrom_DoesNotMatchASiblingWithACommonPrefix() {
        // "/mnt/nvme2" must not be attributed to "/mnt/nvme": the guard is the
        // separator, not the string prefix.
        var table = new[] { "/dev/sda1 / ext4 rw 0 0", "/dev/nvme0n1p1 /mnt/nvme ext4 rw 0 0" };
        var (mount, _) = HostMetricsService.ResolveMountFrom(table, "/mnt/nvme2/files");
        Assert.That(mount, Is.EqualTo("/"));
    }

    [Test]
    public void ResolveMountFrom_UnescapesSpacesInTheMountpoint() {
        var table = new[] { "/dev/sda1 / ext4 rw 0 0",
                            "/dev/sdb1 /media/My\\040Disk ext4 rw 0 0" };
        var (mount, device) = HostMetricsService.ResolveMountFrom(table, "/media/My Disk/files");
        Assert.That(mount, Is.EqualTo("/media/My Disk"));
        Assert.That(device, Is.EqualTo("/dev/sdb1"));
    }

    [Test]
    public void TryGetDiskInfo_MeasuresTheVolumeHoldingThePath() {
        // Whatever the mount table says, the numbers have to come from the
        // filesystem the path is actually on. Measured against the test's own
        // working directory, which is the only volume this test can be sure of.
        var dir = TestContext.CurrentContext.WorkDirectory;
        var (free, total, name) = HostMetricsService.TryGetDiskInfo(dir);
        var expected = new DriveInfo(dir);

        Assert.That(total, Is.EqualTo(expected.TotalSize),
            "total must be the size of the filesystem containing the path");
        Assert.That(free, Is.EqualTo(expected.AvailableFreeSpace).Within(expected.TotalSize / 100),
            "free space is sampled twice, so allow a little drift");
        Assert.That(name, Is.Not.Empty, "the operator needs to see which volume was measured");
    }

    [Test]
    public void ResolveSymlinks_ReturnsTheInput_WhenThereIsNoLink() {
        var dir = TestContext.CurrentContext.WorkDirectory;
        Assert.That(HostMetricsService.ResolveSymlinks(dir), Is.EqualTo(Path.GetFullPath(dir)));
    }
}