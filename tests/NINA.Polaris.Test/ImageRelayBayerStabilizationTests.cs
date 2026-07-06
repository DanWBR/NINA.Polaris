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

using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Regression coverage for the transient CCD_CFA dropout fix in
/// ImageRelayService. Some drivers momentarily report BayerPattern=None
/// mid-session; without stabilization that single frame relays as mono and
/// the client-side debayer paints a grey / raw-mosaic frame ("frame não
/// debayerado" during live stacking). The relay now reuses the last good
/// pattern for any run of None frames once a real pattern is known, but never
/// forces colour onto a genuinely mono source (last-good never set).
/// LatestImage is populated even with no WS clients,
/// so we can assert the wire-side pattern without a socket.
/// </summary>
[TestFixture]
public class ImageRelayBayerStabilizationTests {
    private static IImageData Frame(BayerPatternEnum pattern) {
        var props = new ImageProperties {
            Width = 2, Height = 2, BitDepth = 16, Channels = 1,
            IsBayered = pattern != BayerPatternEnum.None,
            BayerPattern = pattern
        };
        return new BaseImageData(new ushort[] { 1, 2, 3, 4 }, props, new ImageMetaData());
    }

    private static ImageRelayService NewRelay() =>
        // Legacy ctor (no ProfileService) so there's no operator override:
        // the relay trusts the per-frame source pattern, which is the path
        // the dropout stabilization guards.
        new ImageRelayService(NullLogger<ImageRelayService>.Instance);

    [Test]
    public async Task TransientDropout_ReusesLastGoodPattern() {
        var relay = NewRelay();
        await relay.RelayImageAsync(Frame(BayerPatternEnum.RGGB));
        Assert.That(relay.LatestImage!.BayerPattern, Is.EqualTo(BayerPatternEnum.RGGB));

        // A single frame whose CCD_CFA came back empty must NOT flip to mono.
        await relay.RelayImageAsync(Frame(BayerPatternEnum.None));
        Assert.That(relay.LatestImage!.BayerPattern, Is.EqualTo(BayerPatternEnum.RGGB));

        // Pattern recovers on the next good frame.
        await relay.RelayImageAsync(Frame(BayerPatternEnum.RGGB));
        Assert.That(relay.LatestImage!.BayerPattern, Is.EqualTo(BayerPatternEnum.RGGB));
    }

    [Test]
    public async Task MonoSource_IsNeverForcedToColour() {
        var relay = NewRelay();
        // No good pattern was ever seen -> a None frame stays None.
        await relay.RelayImageAsync(Frame(BayerPatternEnum.None));
        Assert.That(relay.LatestImage!.BayerPattern, Is.EqualTo(BayerPatternEnum.None));
        await relay.RelayImageAsync(Frame(BayerPatternEnum.None));
        Assert.That(relay.LatestImage!.BayerPattern, Is.EqualTo(BayerPatternEnum.None));
    }

    [Test]
    public async Task SustainedNone_KeepsStabilizingOncePatternKnown() {
        var relay = NewRelay();
        await relay.RelayImageAsync(Frame(BayerPatternEnum.RGGB));

        // Once a real pattern is locked for the session, a long run of None
        // frames is a CCD_CFA dropout (some OSC INDI drivers only publish the
        // pattern intermittently), so reuse is UNBOUNDED — the old 5-frame cap
        // flashed the LIVE view mono a few seconds after each stack (field
        // report). A genuine OSC->mono change only happens on a camera
        // reconnect, which builds a fresh relay with _lastRelayBayer = None.
        for (int i = 0; i < 40; i++) {
            await relay.RelayImageAsync(Frame(BayerPatternEnum.None));
            Assert.That(relay.LatestImage!.BayerPattern, Is.EqualTo(BayerPatternEnum.RGGB),
                $"dropout frame {i + 1} should still be stabilized");
        }
    }
}
