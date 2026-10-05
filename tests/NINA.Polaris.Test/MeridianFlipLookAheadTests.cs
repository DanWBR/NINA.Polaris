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
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Whether the next exposure would run past the meridian flip point.
///
/// Issue #30, from the field: flip point set to five minutes after the
/// meridian, a five minute exposure ending at meridian plus two, and the run
/// immediately started another one. Asking only "is a flip due right now"
/// answers no at meridian plus two, so the mount tracked on to meridian plus
/// seven with the shutter open. The operator stopped it by hand rather than
/// find out whether it would reach the tripod.
///
/// The arithmetic is tested here against the pure helper the service uses for
/// its own countdown, so these cases say what the decision is without needing
/// a mount, a clock or a sky.
/// </summary>
[TestFixture]
public class MeridianFlipLookAheadTests {

    // Greenwich keeps the local sidereal time equal to the Greenwich one, so a
    // target's hour angle is simply LST minus its RA and the cases below can be
    // read as minutes either side of the meridian.
    private const double Lon = 0.0;

    /// <summary>The RA that puts a target exactly <paramref name="minutes"/>
    /// past the meridian at <paramref name="utc"/>.</summary>
    private static double RaForMinutesPastMeridian(DateTime utc, double minutes) {
        double lst = MeridianFlipService.ComputeLstHours(utc, Lon);
        double ra = lst - minutes / 60.0;
        while (ra < 0) ra += 24;
        while (ra >= 24) ra -= 24;
        return ra;
    }

    private static double HoursToFlip(double raHours, DateTime utc, double minutesAfter)
        => MeridianFlipService.HoursUntilFlip(raHours, utc, Lon, minutesAfter);

    /// <summary>The decision itself, free of the service's equipment checks:
    /// hold when the exposure would outlast the time remaining.</summary>
    private static TimeSpan Hold(double hoursUntilFlip, double exposureSeconds) {
        if (exposureSeconds <= 0) return TimeSpan.Zero;
        if (hoursUntilFlip <= 0) return TimeSpan.Zero;
        if (exposureSeconds / 3600.0 <= hoursUntilFlip) return TimeSpan.Zero;
        return TimeSpan.FromHours(hoursUntilFlip);
    }

    /// <summary>The reported case: 5 minutes after the meridian configured, the
    /// target 2 minutes past, a 5 minute exposure. Three minutes of window left
    /// and five minutes of exposure, so the run has to wait those three.</summary>
    [Test]
    public void TheReportedCase_Holds() {
        var utc = DateTime.UtcNow;
        double ra = RaForMinutesPastMeridian(utc, 2);
        double h = HoursToFlip(ra, utc, minutesAfter: 5);

        var hold = Hold(h, exposureSeconds: 300);

        Assert.That(hold.TotalMinutes, Is.EqualTo(3).Within(0.2),
            "it must wait out the remaining window instead of starting a sub "
            + "that ends two minutes past the flip point");
    }

    [Test]
    public void AnExposureThatFitsInTheWindow_StartsNow() {
        var utc = DateTime.UtcNow;
        double ra = RaForMinutesPastMeridian(utc, 2);
        double h = HoursToFlip(ra, utc, minutesAfter: 5);

        Assert.That(Hold(h, exposureSeconds: 60), Is.EqualTo(TimeSpan.Zero),
            "a one minute sub ends inside the three minutes left");
    }

    /// <summary>Exactly filling the window is allowed: the sub ends as the
    /// point arrives, and the flip happens before the next one.</summary>
    [Test]
    public void AnExposureThatExactlyFillsTheWindow_StartsNow() {
        var utc = DateTime.UtcNow;
        double ra = RaForMinutesPastMeridian(utc, 2);
        double h = HoursToFlip(ra, utc, minutesAfter: 5);

        Assert.That(Hold(h, exposureSeconds: h * 3600.0), Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>Past the point already: ShouldFlipNow owns that, and holding
    /// here would only delay the flip that is about to happen.</summary>
    [Test]
    public void PastTheFlipPoint_DoesNotHold() {
        var utc = DateTime.UtcNow;
        double ra = RaForMinutesPastMeridian(utc, 7);
        double h = HoursToFlip(ra, utc, minutesAfter: 5);

        Assert.Multiple(() => {
            Assert.That(h, Is.LessThan(0));
            Assert.That(Hold(h, exposureSeconds: 300), Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public void ATargetHoursFromTheMeridian_DoesNotHold() {
        var utc = DateTime.UtcNow;
        double ra = RaForMinutesPastMeridian(utc, -180);   // three hours short
        double h = HoursToFlip(ra, utc, minutesAfter: 5);

        Assert.That(Hold(h, exposureSeconds: 600), Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>The property that bounds the cost: a hold is only returned when
    /// the exposure is longer than the time left, and it is that time. So one
    /// flip can never cost more than one exposure of sky.</summary>
    [Test]
    public void AHoldIsNeverLongerThanTheExposureItDefers() {
        var utc = DateTime.UtcNow;
        foreach (var minutesPast in new[] { -10.0, -1, 0, 1, 2, 4, 4.9 }) {
            foreach (var exposure in new[] { 30.0, 120, 300, 900 }) {
                double ra = RaForMinutesPastMeridian(utc, minutesPast);
                double h = HoursToFlip(ra, utc, minutesAfter: 5);
                var hold = Hold(h, exposure);
                Assert.That(hold.TotalSeconds, Is.LessThanOrEqualTo(exposure + 0.001),
                    $"{minutesPast} min past, {exposure}s exposure");
            }
        }
    }

    [Test]
    public void AZeroLengthExposure_NeverHolds() {
        var utc = DateTime.UtcNow;
        double ra = RaForMinutesPastMeridian(utc, 2);
        double h = HoursToFlip(ra, utc, minutesAfter: 5);

        Assert.That(Hold(h, exposureSeconds: 0), Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>With the setting at zero the flip point is the meridian itself,
    /// which is the default many operators leave alone.</summary>
    [Test]
    public void WithNoMinutesAfterMeridian_ThePointIsTheMeridian() {
        var utc = DateTime.UtcNow;
        double ra = RaForMinutesPastMeridian(utc, -2);   // two minutes short
        double h = HoursToFlip(ra, utc, minutesAfter: 0);

        Assert.Multiple(() => {
            Assert.That(h * 60, Is.EqualTo(2).Within(0.2));
            Assert.That(Hold(h, exposureSeconds: 300).TotalMinutes, Is.EqualTo(2).Within(0.2));
        });
    }
}
