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

namespace NINA.Polaris.Services;

/// <summary>
/// The two states a filter wheel can be in where the number on screen is a lie,
/// both learned the hard way on 2026-10-02 with a ZWO EFW 8x1.25".
///
/// <b>It does not know where it is.</b> The wheel reported slot 0 against a
/// driver minimum of 1. A ZWO EFW answers -1 to the SDK while uncalibrated, and
/// a firmware write leaves it there. In that state nothing it says about itself
/// is true, including how many slots it has: it insisted on 4 for an 8-position
/// carousel, on Linux and on Windows, through two SDK versions, and a night went
/// into chasing the software before a calibration fixed it in 51 seconds. The
/// signal was there the whole time and nothing was reading it.
///
/// <b>The slot count and the names disagree.</b> After the firmware write the
/// driver published FILTER_SLOT with max=8 while FILTER_NAME still had four
/// elements, and every consumer sizes its filter list from the names, so the
/// interface went on showing four slots behind an eight-slot wheel. Reloading
/// the driver rebuilds the vector; when that is not enough the driver's saved
/// config is in the way, and Polaris writes that file itself on every filter
/// change (see IndiFilterWheel.SetPositionAsync), so it is our own trap as much
/// as anyone's.
///
/// Pure on purpose: the judgement is what the tests drive, and a wheel is not
/// needed to drive it.
/// </summary>
public static class FilterWheelHealth {

    /// <summary>What to tell the operator, or null when there is nothing wrong.
    /// English source strings, matching the i18n convention: the key is the
    /// text.</summary>
    public sealed record Verdict(bool NeedsCalibration, bool NamesShortOfSlots, string? Message);

    private const string Uncalibrated =
        "The wheel does not report a valid position, so it does not know where it is. "
        + "Nothing it reports is reliable until it is calibrated, including how many slots it has.";

    private const string NamesShort =
        "The driver reports more slots than it published filter names for, so the list "
        + "above is short. Reconnect the wheel; if it stays short, the driver's saved "
        + "configuration is overriding the names and has to be purged.";

    /// <summary>
    /// <paramref name="position"/>, <paramref name="slotMin"/> and
    /// <paramref name="slotMax"/> are the driver's own numbers;
    /// <paramref name="nameCount"/> is how many names it published. A min or max
    /// of 0 means the driver did not say, and then nothing is judged from it:
    /// no claim is better than a guess, and backends other than INDI do not
    /// publish a range at all.
    /// </summary>
    public static Verdict Judge(int position, int slotMin, int slotMax, int nameCount) {
        bool uncalibrated = slotMin > 0 && position < slotMin;
        bool shortNames = slotMax > 0 && nameCount > 0 && slotMax > nameCount;

        // The uncalibrated case comes first and alone. While it holds, the slot
        // count is not to be trusted either, so a second line about the names
        // would be reporting a consequence as if it were a separate fault.
        string? message = uncalibrated ? Uncalibrated
                        : shortNames ? NamesShort
                        : null;

        return new Verdict(uncalibrated, !uncalibrated && shortNames, message);
    }
}
