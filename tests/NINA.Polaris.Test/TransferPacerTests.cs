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
using NUnit.Framework;
using NINA.Polaris.Services.Storage;

namespace NINA.Polaris.Test;

/// <summary>
/// Leaving part of the uplink for the live view during a background push.
///
/// Field report, 2026-08-12 session: with SMB push on, the connection-lost /
/// slow-link banner fired several times per transferred frame. The watchdog was
/// telling the truth: the push wrote its chunks in a tight unpaced loop and took
/// the whole link the operator's browser was on.
///
/// A duty cycle rather than a MB/s cap, because a cap has to be calibrated per
/// site and the operator cannot know what their link does. This adapts: the
/// idle time is computed from how long the chunk actually took.
/// </summary>
[TestFixture]
public class TransferPacerTests {

    /// <summary>At half the link, the push waits as long as the transfer took,
    /// so it occupies half the time and leaves the other half.</summary>
    [Test]
    public void AtFiftyPercent_ItWaitsAsLongAsTheChunkTook() {
        var d = TransferPacer.DelayAfterChunk(TimeSpan.FromMilliseconds(200), 50);
        Assert.That(d.TotalMilliseconds, Is.EqualTo(200).Within(1));
    }

    /// <summary>A quarter of the link means three parts idle to one part busy.</summary>
    [Test]
    public void AtTwentyFivePercent_ItWaitsThreeTimesAsLong() {
        var d = TransferPacer.DelayAfterChunk(TimeSpan.FromMilliseconds(100), 25);
        Assert.That(d.TotalMilliseconds, Is.EqualTo(300).Within(1));
    }

    /// <summary>100 is the escape hatch for a NAS on its own wired path: no
    /// pacing at all, which is what the code did before this existed.</summary>
    [TestCase(100)]
    [TestCase(150)]
    public void AtOneHundredOrMore_ThereIsNoDelay(int share) {
        Assert.That(TransferPacer.DelayAfterChunk(TimeSpan.FromSeconds(1), share),
            Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>A share of 0 would mean "never transfer". It must degrade to
    /// the slowest useful setting instead of parking the queue forever.</summary>
    [TestCase(0)]
    [TestCase(-10)]
    public void ANonsenseShare_DoesNotStallTheQueue(int share) {
        var d = TransferPacer.DelayAfterChunk(TimeSpan.FromMilliseconds(50), share);
        Assert.That(d, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(d.TotalSeconds, Is.LessThanOrEqualTo(2), "and it is still capped");
    }

    /// <summary>THE ONE THAT PROTECTS THE QUEUE. A chunk that took ten seconds
    /// because the link stalled must not then park the push for thirty more:
    /// the cap keeps one bad chunk from compounding into a stopped queue.</summary>
    [Test]
    public void ASingleSlowChunk_CannotParkTheQueue() {
        var d = TransferPacer.DelayAfterChunk(TimeSpan.FromSeconds(10), 25);
        Assert.That(d.TotalSeconds, Is.LessThanOrEqualTo(2));
    }

    [Test]
    public void AnInstantChunk_NeedsNoDelay() {
        Assert.That(TransferPacer.DelayAfterChunk(TimeSpan.Zero, 50), Is.EqualTo(TimeSpan.Zero));
        Assert.That(TransferPacer.DelayAfterChunk(TimeSpan.FromMilliseconds(-5), 50),
            Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>The default has to actually pace something, or the setting
    /// ships doing nothing and the field report stands.</summary>
    [Test]
    public void TheDefaultLeavesRoomForTheLiveView() {
        Assert.That(TransferPacer.DefaultSharePercent, Is.InRange(10, 99));
        Assert.That(TransferPacer.DelayAfterChunk(TimeSpan.FromMilliseconds(100),
            TransferPacer.DefaultSharePercent), Is.GreaterThan(TimeSpan.Zero));
    }

    /// <summary>Lower share, more idle: the knob has to move the right way over
    /// its whole range, not just at the two points checked above.</summary>
    [Test]
    public void LessShareAlwaysMeansMoreIdle() {
        var chunk = TimeSpan.FromMilliseconds(20);
        double previous = 0;
        foreach (var share in new[] { 90, 75, 50, 25, 10 }) {
            var d = TransferPacer.DelayAfterChunk(chunk, share).TotalMilliseconds;
            Assert.That(d, Is.GreaterThan(previous),
                $"share {share} should idle MORE than the larger share before it");
            previous = d;
        }
    }
}
