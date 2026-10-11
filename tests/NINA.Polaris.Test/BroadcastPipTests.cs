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

using NINA.Polaris.Services.Broadcast;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The source of the second picture. What matters here is that a bad source
/// is refused at configuration time rather than on air, because a broadcast
/// is the worst place to discover a typo.
/// </summary>
[TestFixture]
public class BroadcastPipTests {

    [Test]
    public void TheSourceIsMatchedAgainstTheAllowlist() {
        Assert.That(PipSources.IsValid("guide"), Is.True);
        Assert.That(PipSources.IsValid("aux"), Is.True);
        Assert.That(PipSources.IsValid("url"), Is.True);
        Assert.That(PipSources.IsValid("off"), Is.True);
        Assert.That(PipSources.IsValid("webcam"), Is.False);
        Assert.That(PipSources.Parse("WEBCAM"), Is.EqualTo(PipSources.Off), "unknown means off, never a guess");
        Assert.That(PipSources.Parse(null), Is.EqualTo(PipSources.Off));
        Assert.That(PipSources.Parse("GUIDE"), Is.EqualTo("guide"));
    }

    [Test]
    public void EverySourceHasACaptionInEveryLanguage() {
        foreach (var lang in BroadcastStrings.Languages) {
            foreach (var src in new[] { PipSources.Guide, PipSources.Aux, PipSources.Url }) {
                Assert.That(BroadcastPipService.LabelFor(src, lang), Is.Not.Empty, $"{src}/{lang}");
            }
            Assert.That(BroadcastPipService.LabelFor(PipSources.Off, lang), Is.Empty);
        }
    }

    [Test]
    public void ThePollIsWellBelowTheFrameRate() {
        // The broadcast draws twice a second. Fetching a snapshot that often
        // would hammer a small camera's web server for pictures of a sky that
        // is not moving.
        Assert.That(BroadcastPipService.UrlPollInterval, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(2)));
    }
}
