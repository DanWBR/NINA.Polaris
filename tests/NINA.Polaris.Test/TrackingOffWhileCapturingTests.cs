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
using G = NINA.Polaris.Services.MountSafetyGuardService;

namespace NINA.Polaris.Test;

[TestFixture]
public class TrackingOffWhileCapturingTests {
    private static readonly DateTime Now = new(2026, 11, 10, 2, 45, 0, DateTimeKind.Utc);

    private static bool Warn(bool capturing = true, bool needs = true, bool tracking = false,
            bool slewing = false, bool parked = false, bool flip = false, bool safety = false,
            DateTime? last = null)
        => G.ShouldWarnTrackingOff(capturing, needs, tracking, slewing, parked, flip, safety, last, Now);

    [Test]
    public void TrackingOffDuringACaptureWarns() => Assert.That(Warn(), Is.True);

    [Test]
    public void NoCaptureNoWarning() => Assert.That(Warn(capturing: false), Is.False);

    [Test]
    public void TrackingOnNoWarning() => Assert.That(Warn(tracking: true), Is.False);

    [Test]
    public void KnownReasonsForTrackingOffDoNotWarn() {
        Assert.Multiple(() => {
            Assert.That(Warn(slewing: true), Is.False, "slew");
            Assert.That(Warn(parked: true), Is.False, "park");
            Assert.That(Warn(flip: true), Is.False, "meridian flip");
            Assert.That(Warn(safety: true), Is.False, "Polaris's own safety stop");
        });
    }

    [Test]
    public void TheWarningRepeatsEveryFiveMinutes() {
        Assert.Multiple(() => {
            Assert.That(Warn(last: Now.AddMinutes(-1)), Is.False);
            Assert.That(Warn(last: Now - G.TrackingOffRepeat), Is.True);
        });
    }

    [Test]
    public void CalibrationFramesDoNotNeedTracking() {
        Assert.Multiple(() => {
            foreach (var t in new[] { "FLAT", "dark", "Bias", "DARKFLAT" })
                Assert.That(G.FrameTypeNeedsTracking(t), Is.False, t);
            Assert.That(G.FrameTypeNeedsTracking("LIGHT"), Is.True);
            Assert.That(G.FrameTypeNeedsTracking(null), Is.True, "live stack and live capture take lights");
            Assert.That(Warn(needs: false), Is.False);
        });
    }
}
