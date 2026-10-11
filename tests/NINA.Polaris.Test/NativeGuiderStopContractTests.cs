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
using Microsoft.Extensions.Logging.Abstractions;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// What STOP means for the native guider.
///
/// Field report, 2026-10-01: a guided live stack whose target went behind the
/// roof. The safety guard stopped the mount and the guider, the operator picked
/// a new target in SKY, and from then on the GUIDE tab was lying. The
/// "Dithering: settling to under 1.5 px" banner never came down, the badge
/// showed a state that was not happening, pressing STOP and starting again
/// cleared neither, and the guide frame still carried the markers of the stars
/// the previous session had been using.
///
/// Three defects, all on the stop path:
///
/// 1. the teardown returned early when no loop was running, so it cleared
///    nothing: no settle, no dither, no badge. A dither installed while a stop
///    was in flight (that stop waits up to ten seconds for the loop task, and
///    AppState still reads "Guiding" meanwhile) outlived the cleanup, and every
///    later STOP took the same early return. LiveCaptureService.ShouldPause
///    holds the LIVE loop while IsDithering is raised, so the stack also
///    stopped taking subs for as long as the banner was up.
/// 2. the badge was lowered only from a whitelist of four states, so a stop
///    during a slew, after a star pick, or during calibration left the old one
///    on screen.
/// 3. nothing dropped the lock or the frame markers, so the display claimed a
///    lock the guider no longer had.
///
/// These assert the contract, not the implementation: after a stop the guider
/// reports nothing in flight, nothing locked, no markers, and Stopped.
/// </summary>
[TestFixture]
public class NativeGuiderStopContractTests {

    private NativeGuider _g = null!;
    private readonly List<SettleResult> _settled = new();

    [SetUp]
    public void SetUp() {
        var cfg = new ConfigurationBuilder().Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        _g = new NativeGuider(equip, profiles, NullLogger<NativeGuider>.Instance);
        _settled.Clear();
        _g.Settled += r => _settled.Add(r);
    }

    private static object? Field(object payload, string name) =>
        payload.GetType().GetProperty(name)?.GetValue(payload);

    /// <summary>The banner the operator could not get rid of. It is driven by
    /// IsDithering alone, so a stop has to lower it whatever else is true.</summary>
    [Test]
    public async Task Stop_WithADitherInFlight_TakesTheBannerDown() {
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 10, timeoutSec: 40, dithering: true);
        Assume.That(_g.IsDithering, Is.True, "the dither has to be up for this to mean anything");

        await _g.StopAsync();

        Assert.Multiple(() => {
            Assert.That(_g.IsDithering, Is.False, "the dither banner stays on the guide frame");
            Assert.That(_g.IsSettling, Is.False, "the settle readout stays on the GUIDE panel");
            Assert.That(_g.SettleProgress, Is.Null, "the progress bar keeps counting a settle that ended");
            Assert.That(_g.LastSettleStatus, Is.Null,
                "a status left on settling reads as still running to anything that polls it");
        });
    }

    /// <summary>A stop that drops the settler owes its waiter an answer. The
    /// live-stack dither trigger and the sequencer both wait on Settled, and a
    /// settler dropped in silence left them waiting for an event that could no
    /// longer come.</summary>
    [Test]
    public async Task Stop_MidSettle_AnswersTheWaiter() {
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 10, timeoutSec: 40, dithering: false);

        await _g.StopAsync();

        Assert.That(_settled, Has.Count.EqualTo(1), "nobody told the waiter the settle was over");
        Assert.That(_settled[0].Status, Is.Not.EqualTo(0), "a settle cut short did not succeed");
    }

    /// <summary>A settle that was never installed must not invent a result.</summary>
    [Test]
    public async Task Stop_WithNothingInFlight_RaisesNothing() {
        await _g.StopAsync();
        Assert.That(_settled, Is.Empty);
    }

    /// <summary>The operator pressed STOP more than once. The second press has
    /// to be as complete as the first, which is what the early return broke.</summary>
    [Test]
    public async Task Stop_Twice_IsStillFullyStopped() {
        _g.InstallSessionForTest("Guiding");
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 10, timeoutSec: 40, dithering: true);

        await _g.StopAsync();
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 10, timeoutSec: 40, dithering: true);
        await _g.StopAsync();

        Assert.Multiple(() => {
            Assert.That(_g.IsDithering, Is.False);
            Assert.That(_g.AppState, Is.EqualTo("Stopped"));
        });
    }

    /// <summary>Every state a session passes through has to end at Stopped. The
    /// whitelist covered four of them.</summary>
    [TestCase("Guiding")]
    [TestCase("Looping")]
    [TestCase("Paused")]
    [TestCase("LostLock")]
    [TestCase("Slewing")]
    [TestCase("Selected")]
    [TestCase("Calibrating")]
    public async Task Stop_FromAnyState_PutsTheBadgeBackToStopped(string state) {
        _g.InstallSessionForTest(state);
        Assume.That(_g.AppState, Is.EqualTo(state));

        await _g.StopAsync();

        Assert.That(_g.AppState, Is.EqualTo("Stopped"),
            "the badge still reads " + state + " with nothing running behind it");
    }

    /// <summary>STOP clears the markers and keeps the picture: the operator
    /// still sees the guide camera, without the crosshair and star boxes of a
    /// session that is over.</summary>
    [Test]
    public async Task Stop_ClearsTheFrameMarkersAndKeepsTheFrame() {
        _g.InstallSessionForTest("Guiding");
        var before = _g.ViewState;
        Assume.That(before, Is.Not.Null);
        Assume.That(Field(before!, "lockX"), Is.Not.Null, "the session under test needs a lock");

        await _g.StopAsync();

        var after = _g.ViewState;
        Assert.That(after, Is.Not.Null, "the guide frame went blank instead of losing its markers");
        Assert.Multiple(() => {
            Assert.That(Field(after!, "lockX"), Is.Null, "the lock crosshair is still drawn");
            Assert.That(Field(after!, "lockY"), Is.Null);
            Assert.That((System.Collections.IEnumerable)Field(after!, "stars")!,
                Is.Empty, "the previous stars are still marked on the frame");
            Assert.That(Field(after!, "width"), Is.EqualTo(16), "the picture itself should stay");
        });
    }

    /// <summary>With the lock gone the next start picks again, which is what
    /// the operator asked for: markers come back from a fresh pick, manual or
    /// automatic, and never from the previous target.</summary>
    [Test]
    public async Task Stop_DropsTheLock() {
        _g.InstallSessionForTest("Guiding");
        Assume.That(_g.HasLockForTest, Is.True);

        await _g.StopAsync();

        Assert.That(_g.HasLockForTest, Is.False);
    }

    /// <summary>Only the guide loop finishes a settle, so a dither with no loop
    /// behind it can never end. It is refused instead of installed.</summary>
    [Test]
    public async Task Dither_WithNoLoopRunning_IsRefused() {
        _g.InstallSessionForTest("Guiding");
        Assume.That(_g.IsGuiding && _g.HasLockForTest, Is.True,
            "the guard under test is the missing loop, so the other two must pass");

        await _g.DitherAsync(pixels: 5, raOnly: false, settlePixels: 1.5,
            settleTime: 10, settleTimeout: 40);

        Assert.Multiple(() => {
            Assert.That(_g.IsDithering, Is.False,
                "a dither was installed with nothing to advance it: the banner that never cleared");
            Assert.That(_g.SettleProgress, Is.Null);
        });
    }

    /// <summary>The invariant the settle lock exists for still holds after a
    /// stop: a raised IsSettling with no settler behind it is the orphaned
    /// state that showed Settling for the rest of a night.</summary>
    [Test]
    public async Task Stop_LeavesTheSettleInvariantIntact() {
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 10, timeoutSec: 40, dithering: true);
        await _g.StopAsync();
        Assert.That(_g.SettleInvariantHolds, Is.True);
    }
}
