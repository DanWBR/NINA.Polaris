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
using System.Linq;
using NUnit.Framework;
using NINA.Camera.ZwoSdk;
using Entry = NINA.Camera.ZwoSdk.ZwoDiscovery.ZwoCameraEntry;

namespace NINA.Polaris.Test;

/// <summary>
/// A second ZWO camera vanishing from the picker while the first one is open.
///
/// Field, 2026-08-13 (OPi 5 Pro, ASI585MC + ASI678MC both on the bus). Measured
/// on the board, not inferred:
///
///     fresh process, nothing open     ASIGetNumOfConnectedCameras() = 2
///     Polaris, holding the 585 open   ASIGetNumOfConnectedCameras() = 1
///
/// and confirmed the other way round: disconnecting the imaging camera made the
/// ASI678MC appear in the guide picker. So the SDK masks the cameras this
/// process has NOT opened, and the guide picker, which is naturally used while
/// the imaging camera is connected, could never show the second camera.
///
/// The merge keeps every camera seen during the process's lifetime and marks
/// which ones the SDK can currently see. These tests drive the pure Merge, so
/// they need no camera and no SDK.
/// </summary>
[TestFixture]
[NonParallelizable]   // Merge folds into a process-wide registry
public class ZwoDiscoveryMaskingTests {

    [SetUp]
    public void Reset() => ZwoDiscovery.ForgetSeen();

    [TearDown]
    public void Clean() => ZwoDiscovery.ForgetSeen();

    private static Entry Live(int id, string model) => new(id.ToString(), model, model);

    /// <summary>THE FIELD SEQUENCE. Both cameras enumerate while nothing is
    /// open; then the imaging camera is opened and the SDK reports only it. The
    /// second camera must survive that, flagged as currently masked.</summary>
    [Test]
    public void ACameraSeenBeforeAnOpen_SurvivesTheMasking() {
        var both = ZwoDiscovery.Merge(new[] {
            Live(0, "ZWO ASI585MC Pro"),
            Live(1, "ZWO ASI678MC"),
        });
        Assert.That(both.Select(e => e.Model),
            Is.EqualTo(new[] { "ZWO ASI585MC Pro", "ZWO ASI678MC" }));
        Assert.That(both.All(e => e.Present), Is.True);

        // The imaging camera is now open: the SDK reports only that one.
        var masked = ZwoDiscovery.Merge(new[] { Live(0, "ZWO ASI585MC Pro") });

        Assert.That(masked.Count, Is.EqualTo(2), "the 678 must not disappear from the picker");
        var seenNow = masked.Single(e => e.Id == "0");
        var hidden = masked.Single(e => e.Id == "1");
        Assert.That(seenNow.Present, Is.True);
        Assert.That(hidden.Present, Is.False, "and it must be flagged, not passed off as available");
        Assert.That(hidden.Model, Is.EqualTo("ZWO ASI678MC"));
    }

    /// <summary>The first scan of a fresh process has nothing remembered, so it
    /// must be exactly the live scan. Anything else would invent cameras.</summary>
    [Test]
    public void AFirstScan_IsJustTheLiveScan() {
        var one = ZwoDiscovery.Merge(new[] { Live(0, "ZWO ASI585MC Pro") });

        Assert.That(one.Count, Is.EqualTo(1));
        Assert.That(one[0].Present, Is.True);
    }

    /// <summary>A camera coming back into view flips Present again rather than
    /// being listed twice, which is what happens on disconnect.</summary>
    [Test]
    public void WhenTheMaskLifts_TheCameraIsPresentAgain() {
        ZwoDiscovery.Merge(new[] { Live(0, "ZWO ASI585MC Pro"), Live(1, "ZWO ASI678MC") });
        ZwoDiscovery.Merge(new[] { Live(0, "ZWO ASI585MC Pro") });

        var after = ZwoDiscovery.Merge(new[] {
            Live(0, "ZWO ASI585MC Pro"), Live(1, "ZWO ASI678MC"),
        });

        Assert.That(after.Count, Is.EqualTo(2), "no duplicate entry for the camera that returned");
        Assert.That(after.All(e => e.Present), Is.True);
    }

    /// <summary>An empty scan is the masked-everything case (or an SDK that
    /// threw). The remembered cameras still list, all flagged.</summary>
    [Test]
    public void AnEmptyScan_StillListsWhatWasSeen() {
        ZwoDiscovery.Merge(new[] { Live(0, "ZWO ASI585MC Pro"), Live(1, "ZWO ASI678MC") });

        var none = ZwoDiscovery.Merge(new List<Entry>());

        Assert.That(none.Count, Is.EqualTo(2));
        Assert.That(none.Any(e => e.Present), Is.False);
    }

    /// <summary>Ids order the list, so the picker does not reshuffle between
    /// scans as cameras are masked and unmasked.</summary>
    [Test]
    public void TheListIsOrderedById() {
        ZwoDiscovery.Merge(new[] { Live(2, "C"), Live(0, "A") });
        var merged = ZwoDiscovery.Merge(new[] { Live(1, "B") });

        Assert.That(merged.Select(e => e.Id), Is.EqualTo(new[] { "0", "1", "2" }));
    }

    /// <summary>A model rename for the same id (a different camera plugged into
    /// the same slot) takes the live value rather than keeping the stale one.
    /// </summary>
    [Test]
    public void TheLiveModelWinsForTheSameId() {
        ZwoDiscovery.Merge(new[] { Live(0, "ZWO ASI585MC Pro") });
        var merged = ZwoDiscovery.Merge(new[] { Live(0, "ZWO ASI294MC Pro") });

        Assert.That(merged.Single().Model, Is.EqualTo("ZWO ASI294MC Pro"));
    }

    [Test]
    public void ForgetSeen_ClearsTheMemory() {
        ZwoDiscovery.Merge(new[] { Live(0, "A"), Live(1, "B") });
        ZwoDiscovery.ForgetSeen();

        var after = ZwoDiscovery.Merge(new[] { Live(0, "A") });
        Assert.That(after.Count, Is.EqualTo(1), "nothing should be remembered after a forget");
    }
}
