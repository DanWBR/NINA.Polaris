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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Mosaic;

namespace NINA.Polaris.Test;

[TestFixture]
public class MosaicPanelGuidingTests {
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    [TestCase(null, true, true, 30, ExpectedResult = true)]
    [TestCase(null, true, true, 10, ExpectedResult = false)]
    [TestCase(null, true, false, 120, ExpectedResult = false)]
    [TestCase(true, true, false, 5, ExpectedResult = true)]
    [TestCase(false, true, true, 120, ExpectedResult = false)]
    [TestCase(true, false, true, 120, ExpectedResult = false)]
    public bool GuidesEachPanel_FollowsTheChoiceElseTheExposure(bool? requested, bool connected,
            bool wasGuiding, double exposure) =>
        MosaicRunService.GuidesEachPanel(requested, connected, wasGuiding, exposure);

    [Test]
    public async Task SettledGuiding_ReturnsNoWarning() {
        var g = new FakeGuider { SettleAfterStart = new SettleResult { Status = 0 } };
        var warning = await Run(g);
        Assert.That(warning, Is.Null);
        Assert.That(g.Calls, Is.EqualTo(new[] { "find_star", "guide 1.5/3/60" }));
    }

    [Test]
    public async Task StarSelectionFailure_StillStartsGuiding() {
        var g = new FakeGuider { FindStarThrows = true, SettleAfterStart = new SettleResult { Status = 0 } };
        Assert.That(await Run(g), Is.Null);
        Assert.That(g.Calls, Does.Contain("guide 1.5/3/60"));
    }

    [Test]
    public async Task GuiderThatGivesUpWithoutASettle_IsNotWaitedOnForTheTimeout() {
        var g = new FakeGuider { GuideOnStart = false };
        var started = DateTime.UtcNow;
        var warning = await Run(g);
        Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(5)));
        Assert.That(warning, Does.Contain("Panel 3: guiding did not start"));
        Assert.That(warning, Does.Contain("tracking alone"));
    }

    [Test]
    public async Task FailedSettle_WhileStillGuiding_ShootsAnyway() {
        var g = new FakeGuider { SettleAfterStart = new SettleResult { Status = 1, Error = "timed out" } };
        var warning = await Run(g);
        Assert.That(warning, Is.EqualTo("Panel 3: guiding did not settle (timed out); shot anyway."));
    }

    [Test]
    public async Task LostStar_ReportsGuidingDidNotStart() {
        var g = new FakeGuider {
            SettleAfterStart = new SettleResult { Status = 1, Error = "star lost" }, StopOnSettle = true
        };
        var warning = await Run(g);
        Assert.That(warning, Is.EqualTo("Panel 3: guiding did not start (star lost); shot on tracking alone."));
    }

    [Test]
    public async Task StartThatThrows_ReportsAndDoesNotThrow() {
        var g = new FakeGuider { GuideThrows = true };
        var warning = await Run(g);
        Assert.That(warning, Is.EqualTo("Panel 3: guiding could not start (no calibration); shot on tracking alone."));
    }

    [Test]
    public void Cancellation_Propagates() {
        var g = new FakeGuider();   // guiding, never settles
        using var cts = new CancellationTokenSource(200);
        Assert.That(async () => await MosaicRunService.StartAndSettleAsync(g, 3, 60, Grace,
            NullLogger.Instance, cts.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    private static Task<string?> Run(FakeGuider g) =>
        MosaicRunService.StartAndSettleAsync(g, 3, 60, Grace, NullLogger.Instance, CancellationToken.None);

    private sealed class FakeGuider : IGuider {
        public List<string> Calls { get; } = new();
        public bool FindStarThrows { get; init; }
        public bool GuideThrows { get; init; }
        public bool GuideOnStart { get; init; } = true;
        public bool StopOnSettle { get; init; }
        public SettleResult? SettleAfterStart { get; init; }

        public string Backend => "fake";
        public bool IsConnected => true;
        public string AppState => IsGuiding ? "Guiding" : "Stopped";
        public bool IsGuiding { get; private set; }
        public bool IsCalibrating => false;
        public bool IsPaused => false;
        public bool IsLooping => false;
        public bool IsSettling => false;
        public double PixelScale => 1;
        public string? LastAlert => null;
        public DateTime? LastAlertAt => null;
        public string? LastSettleStatus => null;
        public double RmsRA => 0;
        public double RmsDec => 0;
        public double RmsTotal => 0;
        public double PeakRA => 0;
        public double PeakDec => 0;
        public List<GuideStep> SnapshotSteps() => new();
        public void ClearStepHistory() { }

        public Task ConnectAsync(string host = "localhost", int port = 4400, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task StartGuidingAsync(double settlePixels = 1.5, int settleTime = 10,
                int settleTimeout = 40, bool recalibrate = false, CancellationToken ct = default) {
            Calls.Add($"guide {settlePixels}/{settleTime}/{settleTimeout}");
            if (GuideThrows) throw new InvalidOperationException("no calibration");
            IsGuiding = GuideOnStart;
            if (SettleAfterStart != null) {
                _ = Task.Run(async () => {
                    await Task.Delay(50);
                    if (StopOnSettle) IsGuiding = false;
                    Settled?.Invoke(SettleAfterStart);
                });
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default) { IsGuiding = false; return Task.CompletedTask; }
        public Task LoopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DitherAsync(double pixels = 5.0, bool raOnly = false, double settlePixels = 1.5,
            int settleTime = 10, int settleTimeout = 40, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetExposureAsync(int milliseconds, CancellationToken ct = default) => Task.CompletedTask;

        public Task AutoSelectStarAsync(CancellationToken ct = default) {
            Calls.Add("find_star");
            if (FindStarThrows) throw new InvalidOperationException("no star");
            return Task.CompletedTask;
        }

        public Task ClearCalibrationAsync(CancellationToken ct = default) => Task.CompletedTask;

        public event Action<string>? AppStateChanged { add { } remove { } }
        public event Action<GuideStep>? GuideStepReceived { add { } remove { } }
        public event Action<string>? Alert { add { } remove { } }
        public event Action<SettleResult>? Settled;
    }
}
