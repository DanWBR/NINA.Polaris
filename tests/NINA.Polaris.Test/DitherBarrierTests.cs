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
using NINA.Polaris.Services;
using Row = NINA.Polaris.Services.DitherBarrier.CadenceRow;

namespace NINA.Polaris.Test;

/// <summary>Unit tests for the pure decision helpers of
/// <see cref="DitherBarrier"/> — cadence ownership (the slowest camera drives
/// dither) and the every-N due rule. The async rendezvous needs a live guider
/// and is exercised on the bench, not here.</summary>
[TestFixture]
public class DitherBarrierTests {

    // ----- SelectCadenceOwner: the slowest active camera owns the cadence -----

    [Test]
    public void CadenceOwner_SlowestWins_AuxLongerSub() {
        // main 30s, aux 300s => the aux (slowest) drives the dither cadence so
        // the fast main camera is never stalled waiting on a dither.
        var owner = DitherBarrier.SelectCadenceOwner(new[] {
            new Row("main", 1, true, 30),
            new Row("aux",  1, false, 300),
        });
        Assert.That(owner, Is.EqualTo("aux"));
    }

    [Test]
    public void CadenceOwner_SlowestWins_MainLongerSub() {
        var owner = DitherBarrier.SelectCadenceOwner(new[] {
            new Row("main", 1, true, 300),
            new Row("aux",  1, false, 60),
        });
        Assert.That(owner, Is.EqualTo("main"));
    }

    [Test]
    public void CadenceOwner_EqualSubs_PrimaryBreaksTie() {
        var owner = DitherBarrier.SelectCadenceOwner(new[] {
            new Row("aux",  1, false, 120),
            new Row("main", 1, true, 120),
        });
        Assert.That(owner, Is.EqualTo("main"));
    }

    [Test]
    public void CadenceOwner_IgnoresInactiveParticipants() {
        // aux is registered but not active (refcount 0) — must not win even
        // though its sub length is longer.
        var owner = DitherBarrier.SelectCadenceOwner(new[] {
            new Row("main", 1, true, 60),
            new Row("aux",  0, false, 600),
        });
        Assert.That(owner, Is.EqualTo("main"));
    }

    [Test]
    public void CadenceOwner_NoneActive_ReturnsNull() {
        var owner = DitherBarrier.SelectCadenceOwner(new[] {
            new Row("main", 0, true, 60),
        });
        Assert.That(owner, Is.Null);
    }

    // ----- IsDitherDue: every-N with the round-in-flight guard -----

    [Test]
    public void DitherDue_ReachedEveryN_True() {
        Assert.That(DitherBarrier.IsDitherDue(3, 3, roundActive: false), Is.True);
    }

    [Test]
    public void DitherDue_BelowEveryN_False() {
        Assert.That(DitherBarrier.IsDitherDue(2, 3, roundActive: false), Is.False);
    }

    [Test]
    public void DitherDue_RoundAlreadyActive_False() {
        Assert.That(DitherBarrier.IsDitherDue(5, 3, roundActive: true), Is.False);
    }

    [Test]
    public void DitherDue_EveryNDisabled_False() {
        Assert.That(DitherBarrier.IsDitherDue(10, 0, roundActive: false), Is.False);
    }

    // ----- the settle tolerance can never be as large as the dither -----

    /// <summary>The field case, 2026-09-21: an FRA400 rig on a 120 mm guide
    /// scope (6.4 arcsec/px) dithering 3 px with the tolerance left at 3. The
    /// settle was satisfied the moment the dither landed, so guiding resumed
    /// with the star up to 19 arcsec off target and the deadband kept it there.
    /// Every settle reported done and nothing was ever reported lost, which is
    /// why the log looked clean while the screen did not.</summary>
    [Test]
    public void SettleTolerance_CannotEqualTheDither() {
        Assert.That(DitherBarrier.EffectiveSettlePixels(3.0, 3.0), Is.EqualTo(1.5));
        Assert.That(DitherBarrier.EffectiveSettlePixels(5.0, 5.0), Is.EqualTo(2.5));
    }

    [Test]
    public void SettleTolerance_LeavesASensiblePairingAlone() {
        Assert.That(DitherBarrier.EffectiveSettlePixels(5.0, 1.5), Is.EqualTo(1.5));
        Assert.That(DitherBarrier.EffectiveSettlePixels(3.0, 1.0), Is.EqualTo(1.0));
    }

    /// <summary>The helper is a ceiling, not a target: a tolerance tighter than
    /// the cap is the operator asking for better and is left alone. Only a
    /// missing one falls back to the floor, which stays usable even for a
    /// dither so small that half of it would be unsettleable.</summary>
    [Test]
    public void SettleTolerance_IsACeilingNotATarget() {
        Assert.That(DitherBarrier.EffectiveSettlePixels(0.4, 0.3), Is.EqualTo(0.3));
        Assert.That(DitherBarrier.EffectiveSettlePixels(5.0, 0.2), Is.EqualTo(0.2));
    }

    [Test]
    public void SettleTolerance_FallsBackToTheFloorWhenUnset() {
        Assert.That(DitherBarrier.EffectiveSettlePixels(0.4, 0.0), Is.EqualTo(0.5));
        Assert.That(DitherBarrier.EffectiveSettlePixels(3.0, 0.0), Is.EqualTo(1.5));
        Assert.That(DitherBarrier.EffectiveSettlePixels(3.0, -1.0), Is.EqualTo(1.5));
    }

    [Test]
    public void SettleTolerance_IgnoresTheSignOfTheDither() {
        Assert.That(DitherBarrier.EffectiveSettlePixels(-4.0, 4.0), Is.EqualTo(2.0));
    }

    /// <summary>The shipped defaults have to pair sensibly on their own, or
    /// every rig that never touches them starts out in the broken state.</summary>
    [Test]
    public void ShippedDefaults_PairSensibly() {
        var p = new DitherParams();
        Assert.That(DitherBarrier.EffectiveSettlePixels(p.Pixels, p.SettlePixels),
            Is.EqualTo(p.SettlePixels), "the record's own pairing survives the clamp");
        var live = new NINA.Polaris.Services.LiveStackTriggers();
        Assert.That(live.DitherSettlePixels, Is.LessThanOrEqualTo(live.DitherPixels / 2));
    }
}
