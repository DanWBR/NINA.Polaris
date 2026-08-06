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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the live-stack memory estimate to measurements instead of to an
/// accounting of the buffers the design happens to name.
///
/// This number is not cosmetic: it is what the stacking-resolution dropdown
/// shows and what Auto picks against. The previous figure (38 B/px, no floor)
/// advertised 1:1 Full on a 26 MP OSC as "~945 MB" while the process actually
/// peaked at 3550 MB, so the interface was telling the operator that an option
/// fitted comfortably on a 5 GiB board when it took two thirds of the machine.
/// On a 4 GiB board that is the OOM killer at 2 a.m., on a choice the UI
/// endorsed.
///
/// Bench: Radxa Dragon Q6A, 5.1 GiB, IMX571-class OSC, colour stacking, mode
/// full. RSS of the polaris service, read from /proc.
/// </summary>
[TestFixture]
public class StackMemoryEstimateTests {

    private const long Mib = 1024 * 1024;

    // (label, width, height, measured MiB)
    private static readonly object[] Measured = {
        new object[] { "1:2 half", 3124, 2088, 1305L },
        new object[] { "1:1 full", 6248, 4176, 3096L },
    };

    [TestCaseSource(nameof(Measured))]
    public void EstimateTracksTheMeasuredWorkingSet(string label, int w, int h, long measuredMib) {
        long estimate = LiveStackingService.EstimateStackBytes((long)w * h, colour: true) / Mib;

        // The fit reproduces both bench points to under 1%; 5% leaves room for
        // rounding without letting the model quietly drift off the measurement.
        Assert.That(estimate, Is.EqualTo(measuredMib).Within(5).Percent,
            $"{label}: estimativa {estimate} MB contra {measuredMib} MB medidos no Q6A");
    }

    /// <summary>The old model had no fixed term, so shrinking the frame made
    /// the estimate approach zero. It cannot: a session costs the decoded sub,
    /// the preview and the star lists whatever the stacking resolution is.</summary>
    [Test]
    public void QuarterResolutionStillCostsTheFloor() {
        long quarter = LiveStackingService.EstimateStackBytes(1562L * 1044, colour: true);

        Assert.That(quarter, Is.GreaterThan(LiveStackingService.StackFloorBytes),
            "1:4 tem de custar mais que o piso, nao menos");
        Assert.That(quarter / Mib, Is.GreaterThan(500),
            "1:4 anunciado como ~59 MB foi o que motivou este teste");
    }

    /// <summary>What actually runs must be offered. 1:2 measured 1305 MB peak
    /// on a 5.1 GiB board and ran fine; a budget that rejects it is wrong.</summary>
    [Test]
    public void HalfResolutionFitsOnTheFiveGigBoard() {
        long ram = 5L * 1024 * Mib + 100 * Mib;
        long budget = LiveStackingService.StackBudgetBytes(ram);
        long half = LiveStackingService.EstimateStackBytes(3124L * 2088, colour: true);

        Assert.That(half, Is.LessThan(budget),
            "1:2 roda de fato nessa placa; o orcamento nao pode recusar");
    }

    [Test]
    public void AutoPicksAResolutionItsOwnEstimateAllows() {
        foreach (var ramGib in new[] { 2, 4, 8, 16 }) {
            long ram = ramGib * 1024L * Mib;
            int bin = LiveStackingService.ResolveAutoBinning(6248L * 4176, colour: true, ram);
            long cost = LiveStackingService.EstimateStackBytes(6248L * 4176 / ((long)bin * bin), true);

            Assert.That(bin, Is.AnyOf(1, 2, 4));
            if (bin != 4) {
                Assert.That(cost, Is.LessThanOrEqualTo(LiveStackingService.StackBudgetBytes(ram)),
                    $"{ramGib} GiB: Auto escolheu 1:{bin} acima do proprio orcamento");
            }
        }
    }

    /// <summary>1:4 must cost less than 1:2, which sounds obvious and is the
    /// thing a missing floor used to get wrong in the other direction: without
    /// it the estimate scaled to nothing, so the ordering held for the wrong
    /// reason and the absolute numbers were fiction.</summary>
    [Test]
    public void SmallerResolutionsCostLessButNeverApproachZero() {
        long full = LiveStackingService.EstimateStackBytes(6248L * 4176, true);
        long half = LiveStackingService.EstimateStackBytes(3124L * 2088, true);
        long quarter = LiveStackingService.EstimateStackBytes(1562L * 1044, true);

        Assert.That(half, Is.LessThan(full));
        Assert.That(quarter, Is.LessThan(half));
        Assert.That(quarter, Is.GreaterThan(full / 4),
            "o piso domina nas resolucoes pequenas: 1:4 nao custa um quarto de 1:1");
    }
}
