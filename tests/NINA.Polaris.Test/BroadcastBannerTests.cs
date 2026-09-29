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
/// The line along the bottom of a broadcast. It is rewritten every second for
/// hours, so what has to be guarded against is not a crash but a number that
/// quietly reads wrong in front of an audience.
/// </summary>
[TestFixture]
public class BroadcastBannerTests {

    private static readonly BannerFacts Session = new() {
        Target = "M42", Filter = "L", ExposureSeconds = 120, Gain = 100,
        FrameCount = 14, IntegratedSeconds = 1680, Snr = 31.42,
        GuideRmsArcsec = 0.618, SensorTempC = -10
    };

    [Test]
    public void AFullSessionReadsLeftToRightInTheOrderPeopleLookForIt() {
        Assert.That(BroadcastBanner.Compose(Session), Is.EqualTo(
            "M42  ·  L 120s g100  ·  14 frames  ·  28 min  ·  SNR 31.4  ·  RMS 0.62\"  ·  -10 C"));
    }

    [Test]
    public void NothingKnownYetIsAnEmptyLineRatherThanAWallOfZeros() {
        // The seconds between pressing start and the first sub landing.
        Assert.That(BroadcastBanner.Compose(new BannerFacts()), Is.Empty);
    }

    [Test]
    public void ZeroFramesIsNotAFrameCount() {
        // "0 frames" reads as something being broken rather than as something
        // not having happened yet.
        var beforeTheFirstSub = new BannerFacts { Target = "M42", FrameCount = 0 };
        Assert.That(BroadcastBanner.Compose(beforeTheFirstSub), Is.EqualTo("M42"));

        var one = Session with { FrameCount = 1, IntegratedSeconds = 120 };
        Assert.That(BroadcastBanner.Compose(one), Does.Contain("1 frame  ·"));
        Assert.That(BroadcastBanner.Compose(one), Does.Not.Contain("1 frames"));
    }

    [Test]
    public void NotGuidingIsNotPerfectGuiding() {
        // An RMS of zero drawn on a broadcast claims a night nobody has ever
        // had. No guider means no number.
        var unguided = Session with { GuideRmsArcsec = 0 };
        Assert.That(BroadcastBanner.Compose(unguided), Does.Not.Contain("RMS"));

        var guided = Session with { GuideRmsArcsec = 1.5 };
        Assert.That(BroadcastBanner.Compose(guided), Does.Contain("RMS 1.50\""));
    }

    [Test]
    public void ACameraWithNoCoolerReportsNoTemperature() {
        // Not 0.0 C, which is a plausible looking lie on a winter night.
        var uncooled = Session with { SensorTempC = null };
        Assert.That(BroadcastBanner.Compose(uncooled), Does.Not.Contain(" C"));
        Assert.That(BroadcastBanner.Compose(Session with { SensorTempC = 0 }), Does.Contain("0 C"),
            "a cooler that is actually at zero still says so");
        Assert.That(BroadcastBanner.Compose(Session with { SensorTempC = double.NaN }),
            Does.Not.Contain("NaN"));
    }

    [Test]
    public void IntegrationIsSaidTheWayAPersonWouldSayIt() {
        Assert.That(BroadcastBanner.Compose(new BannerFacts { IntegratedSeconds = 45 }), Is.EqualTo("45 s"));
        Assert.That(BroadcastBanner.Compose(new BannerFacts { IntegratedSeconds = 1680 }), Is.EqualTo("28 min"));
        Assert.That(BroadcastBanner.Compose(new BannerFacts { IntegratedSeconds = 3600 }), Is.EqualTo("1 h"));
        Assert.That(BroadcastBanner.Compose(new BannerFacts { IntegratedSeconds = 8040 }), Is.EqualTo("2 h 14 min"));
        Assert.That(BroadcastBanner.Compose(new BannerFacts { IntegratedSeconds = 0 }), Is.Empty);
    }

    [Test]
    public void APlanetaryExposureKeepsItsDecimals() {
        // Milliseconds matter at 20 ms and would round to "0s".
        var planetary = new BannerFacts { Target = "Saturn", ExposureSeconds = 0.02, Gain = 300 };
        Assert.That(BroadcastBanner.Compose(planetary), Is.EqualTo("Saturn  ·  0.02s g300"));

        var deepSky = new BannerFacts { ExposureSeconds = 120 };
        Assert.That(BroadcastBanner.Compose(deepSky), Is.EqualTo("120s"), "and a long sub does not want them");
    }

    [Test]
    public void APartlyKnownSessionLeavesNoGaps() {
        // Live stacking with no filter wheel and no guider, which is a very
        // common rig, must not come out as separators with air between them.
        var simple = new BannerFacts { Target = "NGC 7000", ExposureSeconds = 60, FrameCount = 3, IntegratedSeconds = 180 };
        Assert.That(BroadcastBanner.Compose(simple), Is.EqualTo("NGC 7000  ·  60s  ·  3 frames  ·  3 min"));
        Assert.That(BroadcastBanner.Compose(simple), Does.Not.Contain("·  ·"));
    }

    [Test]
    public void AGainWithNothingToAttachItToIsDropped() {
        // Gain on its own, with no filter and no exposure, is a number with no
        // label in front of an audience.
        Assert.That(BroadcastBanner.Compose(new BannerFacts { Target = "M42", Gain = 100 }), Is.EqualTo("M42"));
    }
}
