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

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

/// <summary>
/// TPPA with the RA axis turned by hand (star trackers, mounts Polaris cannot
/// drive): no mount is needed, a short turn is refused instead of fitted, and
/// Continue only acts while the sweep waits for a turn.
/// </summary>
[TestFixture]
public class PolarAlignmentHandRotationTests {

    private static PolarAlignmentService MakeService() {
        var cfg = new ConfigurationBuilder().Build();
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var solver = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance);
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        return new PolarAlignmentService(equip, solver, profiles,
            new NotificationService(), NullLogger<PolarAlignmentService>.Instance);
    }

    private static PolarPoint Pt(int i, double raHours, double decDeg)
        => new(i, raHours, decDeg, 0.0, DateTime.UtcNow);

    [Test]
    public void OldRequestsAndRigsStayOnTheSlewedSweep() {
        Assert.Multiple(() => {
            Assert.That(new PolarAlignmentOptions().ManualRotation, Is.False);
            Assert.That(new EquipmentProfile().PolarAlignManualRotation, Is.Null);
        });
    }

    [TestCase(30, 10.0)]
    [TestCase(60, 20.0)]
    [TestCase(5, 5.0)]
    [TestCase(10, 5.0)]
    public void TheShortestAcceptedTurnIsAThirdOfTheAskedOne(int asked, double expected) {
        Assert.That(PolarAlignmentService.MinHandRotationDeg(asked), Is.EqualTo(expected).Within(1e-9));
    }

    [Test]
    public void AFullTurnIsAccepted() {
        var earlier = new[] { Pt(0, 6.0, 40) };
        Assert.That(PolarAlignmentService.HandRotationProblem(earlier, 8.0, 40.2, 10), Is.Null);
    }

    [Test]
    public void AShortTurnIsRefused_NamingHowFarItWent() {
        var earlier = new[] { Pt(0, 6.0, 40) };
        var why = PolarAlignmentService.HandRotationProblem(earlier, 6.2, 40, 10);
        Assert.That(why, Does.Contain("only 3°").And.Contain("point 1"));
    }

    [Test]
    public void TheTurnIsMeasuredAcrossRaZero() {
        // 23.5h to 1.5h is 30 degrees, not 330.
        var earlier = new[] { Pt(0, 23.5, 50) };
        Assert.That(PolarAlignmentService.HandRotationProblem(earlier, 1.5, 50, 10), Is.Null);
    }

    [Test]
    public void TurningBackToAnEarlierPointIsRefused() {
        // Point 3 turned the wrong way, back onto point 1.
        var earlier = new[] { Pt(0, 6.0, 40), Pt(1, 8.0, 40) };
        var why = PolarAlignmentService.HandRotationProblem(earlier, 6.1, 40, 10);
        Assert.That(why, Does.Contain("point 1"));
    }

    [Test]
    public void NearThePole_OnlyAFieldThatHasNotMovedIsRefused() {
        var earlier = new[] { Pt(0, 2.0, 85) };
        Assert.Multiple(() => {
            Assert.That(PolarAlignmentService.HandRotationProblem(earlier, 2.01, 85, 10),
                Does.Contain("has not moved"));
            // A small RA change here can still be a real turn about an axis a
            // few degrees off the pole: accepted.
            Assert.That(PolarAlignmentService.HandRotationProblem(earlier, 3.0, 85.5, 10), Is.Null);
        });
    }

    [Test]
    public void ContinueOutsideAHandTurnIsRefused() {
        Assert.Throws<InvalidOperationException>(() => MakeService().ContinueManualRotation());
    }

    [Test]
    public async Task AHandTurnedSweepNeedsNoMount() {
        // Nothing connected: the hand-turned sweep gets past the mount check
        // and stops at the camera.
        var svc = MakeService();
        var job = svc.StartJob(new PolarAlignmentOptions(ManualRotation: true));
        await job.Task!;
        Assert.That(job.Phase, Is.EqualTo(PolarAlignmentPhase.Failed));
        Assert.That(job.LastError, Does.StartWith("Camera not connected"));
    }

    [Test]
    public async Task TheSlewedSweepWithoutAMountPointsAtTheHandTurn() {
        var svc = MakeService();
        var job = svc.StartJob(new PolarAlignmentOptions());
        await job.Task!;
        Assert.That(job.LastError, Does.StartWith("Telescope not connected").And.Contain("Rotate RA by hand"));
    }

    // ----- a whole hand-turned sweep, against a scripted solver -----

    private sealed class FrameCamera : ICamera {
        public bool IsConnected => true;
        public string DeviceName => "frames";
        public CameraStates State => CameraStates.Idle;
        public double Temperature => 0;
        public bool CoolerOn => false;
        public double CoolerPower => 0;
        public int BinX => 1;
        public int BinY => 1;
        public int BitDepth => 16;
        public int MaxX => 64;
        public int MaxY => 64;
        public double PixelSizeX => 3.76;
        public double PixelSizeY => 3.76;
        public int Gain => 0;
        public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
        public int SelectedIso => 0;
        public CameraCapabilities Capabilities => CameraCapabilities.Astro;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IImageData> CaptureAsync(double e, CaptureOptions? o = null, CancellationToken ct = default)
            => Task.FromResult<IImageData>(new BaseImageData(new ushort[64 * 64],
                new ImageProperties { Width = 64, Height = 64, BitDepth = 16, Channels = 1 }));
        public Task SetBinningAsync(int bx, int by, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetTemperatureAsync(double t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCoolerAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortExposureAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Answers each solve with the next scripted pointing (J2000) and
    /// records the position hint it was given.</summary>
    private sealed class ScriptedSolver(params (double Ra, double Dec)[] script) : IPlateSolver {
        private int _next;
        public readonly List<double?> Hints = new();
        public string Id => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;
        public bool SupportsBlindSolve => true;
        public Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options,
                CancellationToken ct = default, Action<string>? onLog = null) {
            Hints.Add(options.HintRa);
            var (ra, dec) = script[_next++];
            return Task.FromResult(new PlateSolveResult {
                Success = true, SolverUsed = Id, RaHours = ra, RaDeg = ra * 15, DecDeg = dec });
        }
    }

    private static async Task Until(Func<bool> condition, string what) {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.That(condition(), Is.True, "timed out waiting for " + what);
    }

    [Test]
    public async Task AHandTurnedSweep_WaitsForEachTurn_RefusesAShortOne_AndMeasures() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-handtppa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Profiles:Directory"] = dir }).Build();
            var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
            profiles.UpdateSettings(p => { p.Latitude = 45; p.Longitude = 10; p.PlateSolvePrimary = "scripted"; });

            // Point 1, then a turn of only 2 degrees (refused), then points 2 and 3.
            var solver = new ScriptedSolver((6.0, 40.0), (6.13, 40.0), (8.0, 40.1), (10.0, 40.2));
            var reg = new PlateSolverRegistry();
            reg.Add(solver, "test");
            var dispatcher = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance,
                new NINA.Polaris.Services.PlateSolving.AstapSolver(cfg, NullLogger<NINA.Polaris.Services.PlateSolving.AstapSolver>.Instance),
                new NINA.Polaris.Services.PlateSolving.PlateSolve3Solver(cfg, NullLogger<NINA.Polaris.Services.PlateSolving.PlateSolve3Solver>.Instance),
                new NINA.Polaris.Services.PlateSolving.AstrometryNetOnlineSolver(cfg, NullLogger<NINA.Polaris.Services.PlateSolving.AstrometryNetOnlineSolver>.Instance),
                new NINA.Polaris.Services.PlateSolving.AstrometryNetLocalSolver(cfg, NullLogger<NINA.Polaris.Services.PlateSolving.AstrometryNetLocalSolver>.Instance),
                null, profiles, null, reg);

            var equip = new EquipmentManager(new IndiClient("localhost", 7624), NullLogger<EquipmentManager>.Instance,
                new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
                new NINA.Polaris.Services.Simulator.Gear.SimGearService());
            typeof(EquipmentManager).GetProperty(nameof(EquipmentManager.Camera))!.SetValue(equip, new FrameCamera());
            var svc = new PolarAlignmentService(equip, dispatcher, profiles,
                new NotificationService(), NullLogger<PolarAlignmentService>.Instance);

            var job = svc.StartJob(new PolarAlignmentOptions(SettleSeconds: 0, ExposureSeconds: 0.01, ManualRotation: true));

            await Until(() => job.Phase == PolarAlignmentPhase.AwaitingRotation2, "the first turn");
            Assert.That(job.Points, Has.Count.EqualTo(1));
            Assert.That(job.RotateDirection, Is.EqualTo("east").Or.EqualTo("west"));

            svc.ContinueManualRotation();
            await Until(() => job.Phase == PolarAlignmentPhase.AwaitingRotation2 && job.LastError != null,
                "the short turn to be refused");
            Assert.That(job.LastError, Does.Contain("only 2°"));
            Assert.That(job.Points, Has.Count.EqualTo(1), "a short turn adds no point");

            svc.ContinueManualRotation();
            await Until(() => job.Phase == PolarAlignmentPhase.AwaitingRotation3, "the second turn");
            Assert.That(job.LastError, Is.Null);

            svc.ContinueManualRotation();
            await job.Task!;

            Assert.Multiple(() => {
                Assert.That(job.Phase, Is.EqualTo(PolarAlignmentPhase.Ok), job.LastError);
                Assert.That(job.Points, Has.Count.EqualTo(3));
                Assert.That(job.RefineTargetRaHours, Is.Not.Null, "point 3 anchors the refinement");
                Assert.That(solver.Hints[0], Is.Null, "point 1 solves blind");
                Assert.That(solver.Hints[2]!.Value, Is.EqualTo(6.0).Within(0.1), "point 2 is hinted with point 1");
                Assert.That(solver.Hints[3]!.Value, Is.EqualTo(8.0).Within(0.1), "point 3 is hinted with point 2");
            });
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static string Here([CallerFilePath] string p = "") => p;

    [Test]
    public void TheRigsChoiceIsTheDefault() {
        var ep = File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris", "Endpoints", "PolarAlignmentEndpoints.cs")));
        Assert.That(ep, Does.Contain("request?.ManualRotation  ?? rig.PolarAlignManualRotation ?? false"));
    }
}
