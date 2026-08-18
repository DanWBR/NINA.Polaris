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

    // ----- SelectCadenceOwner: the "main"/"independent" strategies -----

    [Test]
    public void CadenceOwner_MainStrategy_PicksPrimaryEvenWhenFaster() {
        // Strategy "main": the primary drives the cadence regardless of exposure,
        // so the fast main camera owns it even though aux has a much longer sub.
        var owner = DitherBarrier.SelectCadenceOwner(new[] {
            new Row("main", 1, true, 30),
            new Row("aux",  1, false, 300),
        }, "main");
        Assert.That(owner, Is.EqualTo("main"));
    }

    [Test]
    public void CadenceOwner_MainStrategy_NoPrimary_FallsBackToSlowest() {
        var owner = DitherBarrier.SelectCadenceOwner(new[] {
            new Row("imager-3", 1, false, 120),
            new Row("imager-4", 1, false, 300),
        }, "main");
        Assert.That(owner, Is.EqualTo("imager-4"));   // slowest, no primary present
    }

    [Test]
    public void CadenceOwner_IndependentStrategy_ReturnsNull() {
        // Independent = no synchronization; the barrier owns nothing.
        var owner = DitherBarrier.SelectCadenceOwner(new[] {
            new Row("main", 1, true, 30),
            new Row("aux",  1, false, 300),
        }, "independent");
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
}
