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

namespace NINA.Polaris.Test;

/// <summary>A dark run with 10 × 60 s left on one card and 20 × 180 s on the
/// next showed an ETA of 31 min: the average frame time so far, all 60 s
/// frames, times the frames left.</summary>
[TestFixture]
public class SequenceEtaTests {
    private static List<SequenceItem> DarkRun() => [
        new() { Name = "Target1", ImageType = "BIAS", Exposure = 0, Count = 20, Enabled = false },
        new() { Name = "Target2", ImageType = "DARK", Exposure = 60, Count = 30, Enabled = true },
        new() { Name = "Target3", ImageType = "DARK", Exposure = 180, Count = 20, Enabled = true },
    ];

    [Test]
    public void EachRemainingFrameCostsItsOwnExposurePlusTheMeasuredOverhead() {
        // 20 frames of 60 s done in 20 × 62 s: 2 s of overhead each.
        double eta = SequenceEngine.EstimateRemainingSeconds(DarkRun(), currentItem: 1, frameInItem: 20,
            elapsedSeconds: 20 * 62, exposureDoneSeconds: 20 * 60, framesDone: 20);
        Assert.That(eta, Is.EqualTo(10 * 62 + 20 * 182).Within(1e-6));
    }

    [Test]
    public void DisabledCardsAndFinishedCardsCostNothing() {
        double eta = SequenceEngine.EstimateRemainingSeconds(DarkRun(), currentItem: 2, frameInItem: 5,
            elapsedSeconds: 0, exposureDoneSeconds: 0, framesDone: 0);
        Assert.That(eta, Is.EqualTo(15 * 180));
    }

    [Test]
    public void BeforeTheFirstFrame_TheEstimateIsTheExposuresAlone() {
        double eta = SequenceEngine.EstimateRemainingSeconds(DarkRun(), currentItem: -1, frameInItem: 0,
            elapsedSeconds: 3, exposureDoneSeconds: 0, framesDone: 0);
        Assert.That(eta, Is.EqualTo(30 * 60 + 20 * 180));
    }

    [Test]
    public void OverheadNeverGoesNegative() {
        double eta = SequenceEngine.EstimateRemainingSeconds(DarkRun(), currentItem: 2, frameInItem: 19,
            elapsedSeconds: 100, exposureDoneSeconds: 180, framesDone: 1);
        Assert.That(eta, Is.EqualTo(180));
    }
}
