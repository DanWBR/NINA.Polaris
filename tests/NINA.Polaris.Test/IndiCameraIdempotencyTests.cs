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
using NUnit.Framework;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// FIELD6-17: the per-capture property writes in <see cref="IndiCamera"/>
/// (binning / gain / offset / frame type) must be idempotent — a capture loop
/// that is already configured should send CCD_EXPOSURE and nothing else.
///
/// Why this is worth a test: unconditional re-writes are invisible in normal
/// use (the driver just says "same as current, no change needed") and only bite
/// on long guide runs, where ~100 pointless property writes a minute is exactly
/// the per-frame reconfig known to wedge indi_asi_ccd. A regression here would
/// be silent — nothing fails, frames just stop arriving hours later.
///
/// These pin <see cref="IndiCamera.AlreadyAt"/>, the guard every numeric setter
/// funnels through. The critical case is StaleAfterRestart: the guard must key
/// off the driver's echoed snapshot, never a cache of what we last wrote, or a
/// restarted driver silently keeps the wrong config.
/// </summary>
[TestFixture]
public class IndiCameraIdempotencyTests {
    private static IndiNumberProperty Prop(params (string Name, double Value)[] elements) {
        var p = new IndiNumberProperty();
        foreach (var (name, value) in elements) {
            p.Values[name] = new IndiNumberElement { Value = value };
        }
        return p;
    }

    /// <summary>Steady state: driver already at bin 1x1 → suppress the write.
    /// This is the whole point — the guide loop's every-frame CCD_BINNING.</summary>
    [Test]
    public void AlreadyAt_AllValuesMatch_IsSatisfied() {
        var prop = Prop(("HOR_BIN", 1), ("VER_BIN", 1));
        Assert.That(IndiCamera.AlreadyAt(prop, new Dictionary<string, double> {
            ["HOR_BIN"] = 1, ["VER_BIN"] = 1
        }), Is.True);
    }

    /// <summary>A real change must still go through, or binning would never apply.</summary>
    [Test]
    public void AlreadyAt_OneValueDiffers_IsNotSatisfied() {
        var prop = Prop(("HOR_BIN", 1), ("VER_BIN", 1));
        Assert.That(IndiCamera.AlreadyAt(prop, new Dictionary<string, double> {
            ["HOR_BIN"] = 2, ["VER_BIN"] = 2
        }), Is.False);
    }

    /// <summary>Partial match is NOT a match: bin 2x1 when asked for 2x2 must
    /// still write, otherwise a half-applied binning would stick.</summary>
    [Test]
    public void AlreadyAt_PartialMatch_IsNotSatisfied() {
        var prop = Prop(("HOR_BIN", 2), ("VER_BIN", 1));
        Assert.That(IndiCamera.AlreadyAt(prop, new Dictionary<string, double> {
            ["HOR_BIN"] = 2, ["VER_BIN"] = 2
        }), Is.False);
    }

    /// <summary>Driver hasn't published the property (or doesn't have it) →
    /// never suppress. Preserves the pre-guard behaviour of just trying.</summary>
    [Test]
    public void AlreadyAt_NullProperty_IsNotSatisfied() {
        Assert.That(IndiCamera.AlreadyAt(null, new Dictionary<string, double> {
            ["HOR_BIN"] = 1
        }), Is.False);
    }

    /// <summary>Property exists but lacks the element we want (driver casing
    /// differences, trimmed vectors) → write, don't assume.</summary>
    [Test]
    public void AlreadyAt_MissingElement_IsNotSatisfied() {
        var prop = Prop(("HOR_BIN", 1));
        Assert.That(IndiCamera.AlreadyAt(prop, new Dictionary<string, double> {
            ["HOR_BIN"] = 1, ["VER_BIN"] = 1
        }), Is.False);
    }

    /// <summary>THE regression this guard must not cause. After the watchdog
    /// restarts a driver, its properties come back at defaults (gain 10 here).
    /// The guard reads that live snapshot, so the next capture re-sends gain 120
    /// and the camera is correctly configured. Had the guard cached "we already
    /// wrote 120", it would skip the write and shoot every remaining sub at the
    /// driver's default gain — a silent data-loss bug worse than the churn.</summary>
    [Test]
    public void AlreadyAt_StaleAfterRestart_ReWritesFromSnapshot() {
        // Driver restarted: CCD_CONTROLS is back at its default, not our 120.
        var afterRestart = Prop(("Gain", 10));
        Assert.That(IndiCamera.AlreadyAt(afterRestart, new Dictionary<string, double> {
            ["Gain"] = 120
        }), Is.False, "must re-send gain after a driver restart resets it");

        // Once the driver echoes 120 back, we stop re-sending it.
        var settled = Prop(("Gain", 120));
        Assert.That(IndiCamera.AlreadyAt(settled, new Dictionary<string, double> {
            ["Gain"] = 120
        }), Is.True);
    }
}
