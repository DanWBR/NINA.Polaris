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
/// What the native guider reports while a settle runs, and how it gets out.
///
/// Two field symptoms, one cause. "Settling" stayed on the guider frame with
/// guiding visibly going on; and the LIVE loop, which refuses to start a sub
/// while the guider is settling, sat parked long after the dither it was
/// waiting for should have timed out. The settle was only advanced on frames
/// that found the star, so a star lost mid-settle froze the state: IsSettling
/// raised, nobody to lower it, and no Settled event for the waiter.
///
/// These drive the state machine through the loop's own updater, with a
/// settle installed the way DitherAsync installs one.
/// </summary>
[TestFixture]
public class NativeGuiderSettleStateTests {

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

    [Test]
    public void NoSettleInstalled_AdvanceIsANoOp() {
        _g.AdvanceSettle(null);
        _g.AdvanceSettle(0.3);
        Assert.Multiple(() => {
            Assert.That(_g.IsSettling, Is.False);
            Assert.That(_g.IsDithering, Is.False);
            Assert.That(_settled, Is.Empty);
        });
    }

    /// <summary>The happy path, to anchor the rest: within tolerance for the
    /// settle time completes with status 0 and lowers both flags.</summary>
    [Test]
    public async Task WithinTolerance_CompletesAndLowersBothFlags() {
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 0.05, timeoutSec: 5, dithering: true);
        Assert.That(_g.IsSettling && _g.IsDithering, Is.True);

        _g.AdvanceSettle(0.4);
        await Task.Delay(80);
        _g.AdvanceSettle(0.4);

        Assert.Multiple(() => {
            Assert.That(_g.IsSettling, Is.False);
            Assert.That(_g.IsDithering, Is.False);
            Assert.That(_g.LastSettleStatus, Is.EqualTo("done"));
            Assert.That(_g.SettleProgress, Is.Null, "no readout once the settle is over");
            Assert.That(_settled, Has.Count.EqualTo(1));
            Assert.That(_settled[0].Status, Is.EqualTo(0));
        });
    }

    /// <summary>THE REGRESSION. The star is never found again after the dither.
    /// Every frame is a lost-star frame. The settle must still end at its
    /// timeout, lower the flags, and tell the waiter, or the LIVE loop parks
    /// behind IsSettling for the entire loss.</summary>
    [Test]
    public async Task StarLostForTheWholeSettle_TimesOutInsteadOfSettlingForever() {
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 1, timeoutSec: 0.1, dithering: true);

        // Lost-star frames only, past the timeout.
        _g.AdvanceSettle(null);
        Assert.That(_g.IsSettling, Is.True, "inside the timeout the settle is still open");
        await Task.Delay(150);
        _g.AdvanceSettle(null);

        Assert.Multiple(() => {
            Assert.That(_g.IsSettling, Is.False, "the timeout ran even though no frame found the star");
            Assert.That(_g.IsDithering, Is.False);
            Assert.That(_g.LastSettleStatus, Is.EqualTo("failed"));
            Assert.That(_settled, Has.Count.EqualTo(1), "the waiter must be told");
            Assert.That(_settled[0].Status, Is.EqualTo(1));
            Assert.That(_settled[0].Error, Does.Contain("star lost"));
        });
    }

    /// <summary>A star that comes back inside the timeout settles as if
    /// nothing happened, except that the within-tolerance clock restarted.</summary>
    [Test]
    public async Task StarLostThenBack_StillSettles() {
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 0.05, timeoutSec: 5, dithering: true);
        _g.AdvanceSettle(null);
        _g.AdvanceSettle(null);
        _g.AdvanceSettle(0.5);
        await Task.Delay(80);
        _g.AdvanceSettle(0.5);
        Assert.Multiple(() => {
            Assert.That(_g.IsSettling, Is.False);
            Assert.That(_settled.Select(r => r.Status), Is.EqualTo(new[] { 0 }));
        });
    }

    /// <summary>Once over, a settle is gone: further frames, found or lost, do
    /// not raise the flags again or fire a second event.</summary>
    [Test]
    public async Task AFinishedSettleStaysFinished() {
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 1, timeoutSec: 0.05, dithering: false);
        await Task.Delay(80);
        _g.AdvanceSettle(null);
        Assert.That(_settled, Has.Count.EqualTo(1));

        _g.AdvanceSettle(null);
        _g.AdvanceSettle(3.0);
        _g.AdvanceSettle(0.1);
        Assert.Multiple(() => {
            Assert.That(_g.IsSettling, Is.False);
            Assert.That(_settled, Has.Count.EqualTo(1));
        });
    }

    /// <summary>The lost-star readout keeps the last measured error on the bar
    /// instead of a zero that would read as "on target".</summary>
    [Test]
    public void LostStarFrameKeepsTheLastMeasuredErrorOnTheReadout() {
        _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 10, timeoutSec: 40, dithering: true);
        _g.AdvanceSettle(4.2);
        _g.AdvanceSettle(null);
        var sp = _g.SettleProgress;
        Assert.That(sp, Is.Not.Null);
        var err = (double)sp!.GetType().GetProperty("errorPx")!.GetValue(sp)!;
        var below = (double)sp.GetType().GetProperty("belowSec")!.GetValue(sp)!;
        Assert.Multiple(() => {
            Assert.That(err, Is.EqualTo(4.2));
            Assert.That(below, Is.EqualTo(0.0));
        });
    }

    /// <summary>A dither installed from another thread while the loop is
    /// finishing an old settle: the two must serialize. Unserialized, the
    /// finish could clear the flags, the reinstall raise them and hand over a
    /// new settler, and the finish then null that settler on its way out,
    /// leaving IsSettling raised with nothing to ever lower it. Hammered,
    /// because the interleaving was a few instructions wide.</summary>
    [Test]
    public async Task ConcurrentFinishAndReinstall_NeverOrphanTheSettleFlag() {
        for (int round = 0; round < 300; round++) {
            _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 0, timeoutSec: 60, dithering: true);
            using var go = new ManualResetEventSlim(false);
            // settleSec 0: the first in-tolerance frame completes the settle.
            var finish = Task.Run(() => { go.Wait(); _g.AdvanceSettle(0.1); });
            var reinstall = Task.Run(() => {
                go.Wait();
                _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 60, timeoutSec: 60, dithering: true);
            });
            go.Set();
            await Task.WhenAll(finish, reinstall);

            Assert.That(_g.SettleInvariantHolds, Is.True,
                $"round {round}: IsSettling raised with no settler to lower it");
            // Leave the guider clean for the next round.
            _g.InstallSettleForTest(settlePixels: 1.5, settleSec: 1, timeoutSec: 0, dithering: false);
            _g.AdvanceSettle(null);
        }
    }
}
