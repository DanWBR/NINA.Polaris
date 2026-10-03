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
/// The two filter-wheel states where the number on screen is a lie. Both came
/// out of one night with a ZWO EFW 8x1.25", 2026-10-02.
///
/// The wheel insisted it had 4 slots for an 8-position carousel, on Linux and on
/// Windows, through two SDK versions, with the wheel open. What nothing was
/// reading is that it also reported no valid position, which is a ZWO EFW saying
/// it is uncalibrated, and in that state its slot count is not to be believed
/// either. A firmware write leaves it exactly there, and it reads as a dead
/// wheel. The calibration fixed it in 51 seconds and the wheel then reported 8.
///
/// Then the driver published 8 slots while its name vector still had 4 entries,
/// and every consumer sizes its filter list from the names, so the interface
/// went on showing four.
///
/// These pin the judgement, including the cases where it must stay quiet: a
/// genuine 4-slot wheel, and a backend that publishes no range at all.
/// </summary>
[TestFixture]
public class FilterWheelHealthTests {

    [Test]
    public void HealthyWheel_SaysNothing() {
        var v = FilterWheelHealth.Judge(position: 1, slotMin: 1, slotMax: 8, nameCount: 8);

        Assert.Multiple(() => {
            Assert.That(v.NeedsCalibration, Is.False);
            Assert.That(v.NamesShortOfSlots, Is.False);
            Assert.That(v.Message, Is.Null, "a healthy wheel must not put a warning on screen");
        });
    }

    /// <summary>A real four-slot wheel is not a fault. If this warned, the
    /// warning would be on screen for most of the people who have one.</summary>
    [Test]
    public void GenuineFourSlotWheel_SaysNothing() {
        var v = FilterWheelHealth.Judge(position: 2, slotMin: 1, slotMax: 4, nameCount: 4);

        Assert.That(v.Message, Is.Null);
    }

    /// <summary>The state a firmware write leaves the wheel in: slot 0 against a
    /// minimum of 1, which is the INDI driver passing on the SDK's -1.</summary>
    [Test]
    public void PositionBelowTheDriverMinimum_NeedsCalibration() {
        var v = FilterWheelHealth.Judge(position: 0, slotMin: 1, slotMax: 8, nameCount: 8);

        Assert.Multiple(() => {
            Assert.That(v.NeedsCalibration, Is.True);
            Assert.That(v.Message, Does.Contain("calibrated"));
            Assert.That(v.Message, Does.Contain("how many slots"),
                "the operator has to know the slot count is suspect too, which is the"
                + " part that cost a night");
        });
    }

    /// <summary>The second half of that night: eight slots, four names.</summary>
    [Test]
    public void MoreSlotsThanNames_IsReported() {
        var v = FilterWheelHealth.Judge(position: 1, slotMin: 1, slotMax: 8, nameCount: 4);

        Assert.Multiple(() => {
            Assert.That(v.NamesShortOfSlots, Is.True);
            Assert.That(v.NeedsCalibration, Is.False);
            Assert.That(v.Message, Does.Contain("Reconnect"),
                "the first remedy is a reconnect, the saved config only if that fails");
        });
    }

    /// <summary>With no valid position the slot count is not to be trusted, so
    /// the short-name line would be reporting a consequence as a second fault.
    /// One message, the one that leads somewhere.</summary>
    [Test]
    public void Uncalibrated_TakesPrecedenceOverTheNameCount() {
        var v = FilterWheelHealth.Judge(position: 0, slotMin: 1, slotMax: 8, nameCount: 4);

        Assert.Multiple(() => {
            Assert.That(v.NeedsCalibration, Is.True);
            Assert.That(v.NamesShortOfSlots, Is.False);
            Assert.That(v.Message, Does.Contain("calibrated"));
        });
    }

    /// <summary>Only INDI publishes a slot range. An Alpaca or simulated wheel
    /// reports none, and a judgement from a zero would fire on every tick.</summary>
    [TestCase(0, 0, 0, 8)]
    [TestCase(0, 0, 8, 8)]
    [TestCase(1, 1, 0, 8)]
    public void NoRangePublished_SaysNothing(int position, int slotMin, int slotMax, int nameCount) {
        var v = FilterWheelHealth.Judge(position, slotMin, slotMax, nameCount);

        Assert.That(v.Message, Is.Null);
    }

    /// <summary>Before the first connect there are no names yet, which is not a
    /// wheel disagreeing with itself.</summary>
    [Test]
    public void NoNamesYet_SaysNothing() {
        var v = FilterWheelHealth.Judge(position: 1, slotMin: 1, slotMax: 8, nameCount: 0);

        Assert.That(v.NamesShortOfSlots, Is.False);
        Assert.That(v.Message, Is.Null);
    }

    /// <summary>More names than slots is the other way round and has its own
    /// report, which names the filters that did not fit (see
    /// EffectiveFilterWheel.NamesBeyondSlots). Not this one's business.</summary>
    [Test]
    public void MoreNamesThanSlots_IsNotThisVerdict() {
        var v = FilterWheelHealth.Judge(position: 1, slotMin: 1, slotMax: 4, nameCount: 5);

        Assert.That(v.Message, Is.Null);
    }
}
