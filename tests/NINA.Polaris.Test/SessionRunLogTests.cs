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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Logging;
using NINA.Polaris.Services.Plan;

namespace NINA.Polaris.Test;

/// <summary>
/// A capture run leaves a readable log in the capture folder, beside the
/// frames, whether or not it saved any, and the end-of-run offload leaves
/// that file to the run log's own push.
/// </summary>
[TestFixture]
public class SessionRunLogTests {

    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void Cleanup() {
        foreach (var d in _tempDirs) {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }
        _tempDirs.Clear();
    }

    private string NewDir() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-runlog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class NoServices : IServiceProvider {
        public object? GetService(Type serviceType) => null;
    }

    private (SessionRunLogService svc, LogService log, ProfileService profiles, string captures) Make(
            string? layout = null) {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = NewDir() })
            .Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        var captures = NewDir();
        profiles.UpdateSettings(p => {
            p.ImageOutputDir = captures;
            p.ImageFolderLayout = layout ?? "target-first";
        });
        var log = new LogService();
        // The service's own lines go through a logger that feeds the same
        // buffer, as LogBufferLoggerProvider does in the host.
        var factory = LoggerFactory.Create(b => b.AddProvider(new LogBufferLoggerProvider(log)));
        var svc = new SessionRunLogService(log, profiles, new NoServices(),
            factory.CreateLogger<SessionRunLogService>());
        return (svc, log, profiles, captures);
    }

    private static LogEntry Line(string msg, string level = "info", string source = "server",
                                 string category = "NINA.Polaris.Services.Plan.PlanRunnerService") =>
        new(0, DateTime.UtcNow, level, source, msg, Category: category);

    [Test]
    public void SubDir_FollowsTheFolderLayout() {
        var night = new DateTime(2026, 11, 12);
        Assert.That(ImageWriterService.SessionLogSubDir("My Rig", null, night),
            Is.EqualTo(Path.Combine("My_Rig", "logs", "2026-11-12")));
        Assert.That(ImageWriterService.SessionLogSubDir("My Rig", "night-first", night),
            Is.EqualTo(Path.Combine("My_Rig", "20261112", "logs")));
        Assert.That(ImageWriterService.SessionLogSubDir("My Rig", "night-first-dashed", night),
            Is.EqualTo(Path.Combine("My_Rig", "2026-11-12", "logs")));
    }

    [Test]
    public void Run_WritesLeadIn_Lines_AndOutcome_UnderTheCaptureFolder() {
        var (svc, log, _, captures) = Make();
        log.Append(Line("Camera connected"));
        log.Append(Line("GET /api/status 200 3.1ms", source: "http"));

        var s = svc.Begin("plan", "M31 night", new[] { "Targets:", "  - M31" });
        Assert.That(s, Is.Not.Null);
        Assert.That(s!.Path, Does.StartWith(captures));
        Assert.That(s.Path, Does.Contain(Path.Combine("logs", "")));

        log.Append(Line("Slewing to M31"));
        log.Append(Line("Plate solve failed", level: "error"));
        log.Append(Line("GET /api/status 500 9.0ms", level: "error", source: "http"));
        svc.End(s, "FAILED", "Plate solve failed three times");

        var text = File.ReadAllText(s.Path);
        Assert.Multiple(() => {
            Assert.That(text, Does.Contain("Run: PLAN \"M31 night\""));
            Assert.That(text, Does.Contain("  - M31"));
            Assert.That(text, Does.Contain("Camera connected"), "lead-in from before the run");
            Assert.That(text, Does.Not.Contain("/api/status 200"), "routine HTTP stays out");
            Assert.That(text, Does.Contain("/api/status 500"), "failed HTTP is kept");
            Assert.That(text, Does.Contain("Slewing to M31"));
            Assert.That(text, Does.Contain("[PlanRunnerService] Plate solve failed"));
            Assert.That(text, Does.Contain("Outcome: FAILED"));
            Assert.That(text, Does.Contain("Error: Plate solve failed three times"));
        });
        // Each line once: the lead-in and the run do not overlap.
        Assert.That(text.Split('\n').Count(l => l.Contains("Camera connected")), Is.EqualTo(1));
    }

    [Test]
    public void Run_ThatSavesNothing_StillLeavesTheFolderAndTheLog() {
        var (svc, _, _, captures) = Make("night-first");
        var s = svc.Begin("plan", "Never started");
        svc.End(s, "did not start", "The engine refused to start.");
        var logs = Directory.GetFiles(captures, "*.log", SearchOption.AllDirectories);
        Assert.That(logs, Has.Length.EqualTo(1));
        Assert.That(Path.GetFileName(Path.GetDirectoryName(logs[0])), Is.EqualTo("logs"));
        Assert.That(File.ReadAllText(logs[0]), Does.Contain("Outcome: did not start"));
    }

    [Test]
    public void SecondBegin_WhileOneIsOpen_ReturnsNull_AndEndIsIdempotent() {
        var (svc, _, _, _) = Make();
        var plan = svc.Begin("plan", "A");
        Assert.That(svc.Begin("adv", "A's document"), Is.Null, "a plan drives the ADV engine");
        svc.End(null, "completed");
        svc.End(plan, "completed");
        svc.End(plan, "completed");
        var text = File.ReadAllText(plan!.Path);
        Assert.That(text.Split('\n').Count(l => l.StartsWith("Outcome:")), Is.EqualTo(1));
        Assert.That(svc.Begin("adv", "next"), Is.Not.Null, "closed, the next run gets its own");
    }

    [Test]
    public void NoCaptureFolder_LogGoesToTheHostLogFolder() {
        var (svc, _, profiles, _) = Make();
        profiles.UpdateSettings(p => p.ImageOutputDir = "");
        var s = svc.Begin("autorun", "x");
        try {
            Assert.That(s!.Path, Does.Contain(Path.Combine("NINA.Polaris", "logs", "runs")));
            Assert.That(File.ReadAllText(s.Path), Does.Contain("Capture folder: NOT SET"));
        } finally {
            svc.End(s, "completed");
            try { File.Delete(s!.Path); } catch { }
        }
    }

    [Test]
    public async Task GuideLogs_WrittenDuringTheRun_AreCopiedBesideTheRunLog() {
        var (svc, _, profiles, _) = Make();
        var phd2 = NewDir();
        profiles.UpdateSettings(p => p.Phd2GuideLogDir = phd2);
        var old = Path.Combine(phd2, "PHD2_GuideLog_2026-01-01_000000.txt");
        File.WriteAllText(old, "old night");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        var s = svc.Begin("live", "");
        var live = Path.Combine(phd2, "PHD2_GuideLog_2026-11-12_210000.txt");
        File.WriteAllText(live, "Guiding Begins\n1,0.5,0.1\n");
        svc.End(s, "stopped");
        await svc.LastSync;

        var guide = Path.Combine(Path.GetDirectoryName(s!.Path)!, "guide");
        Assert.That(File.ReadAllText(Path.Combine(guide, Path.GetFileName(live))), Does.Contain("Guiding Begins"));
        Assert.That(File.Exists(Path.Combine(guide, Path.GetFileName(old))), Is.False, "a log from another night stays out");
    }

    [TestCase("plan", "M 31 / Andromeda", "plan_M_31_Andromeda_2026-11-12_213005.log")]
    [TestCase("autorun", "", "autorun_2026-11-12_213005.log")]
    public void FileName_IsSafe(string kind, string name, string expected) {
        Assert.That(SessionRunLogService.FileName(kind, name, new DateTime(2026, 11, 12, 21, 30, 5)),
            Is.EqualTo(expected));
    }

    [Test]
    public void Offload_LeavesRunLogsToTheirOwnPush() {
        var root = Path.Combine("C:", "caps");
        Assert.That(StoragePushService.IsSessionLog(root, Path.Combine(root, "Rig", "logs", "2026-11-12", "plan_x.log")), Is.True);
        Assert.That(StoragePushService.IsSessionLog(root, Path.Combine(root, "Rig", "20261112", "logs", "plan_x.log")), Is.True);
        Assert.That(StoragePushService.IsSessionLog(root, Path.Combine(root, "Rig", "logs", "2026-11-12", "guide", "PHD2_GuideLog_x.txt")), Is.True);
        Assert.That(StoragePushService.IsSessionLog(root, Path.Combine(root, "Rig", "M31", "lights", "2026-11-12", "a.fits")), Is.False);
        Assert.That(StoragePushService.IsSessionLog(root, Path.Combine(root, "Rig", "M31", "notes.log")), Is.False);
    }

    [Test]
    public void PlanHeader_DescribesWhatWasAskedFor() {
        var plan = new ImagingPlan {
            Name = "N",
            StartMode = PlanStartMode.AtTime, StartAtUtc = "23:00",
            EndMode = PlanEndMode.Dawn,
            Targets = {
                new PlanTarget { Name = "M31", RaHours = 0.7123, DecDeg = 41.269,
                    Frames = { new PlanFrame { Count = 20, ExposureSeconds = 120, Filter = "L" } } }
            }
        };
        var lines = PlanRunnerService.DescribePlan(plan).ToList();
        Assert.That(lines, Does.Contain("Start: at 23:00 UTC"));
        Assert.That(lines, Does.Contain("End: at astronomical dawn"));
        Assert.That(lines.Any(l => l.Contains("M31") && l.Contains("20 x 120 s LIGHT L")), Is.True);
    }
}
