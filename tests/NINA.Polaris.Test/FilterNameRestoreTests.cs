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
using Decision = NINA.Polaris.Services.FilterNameRestoreService.Decision;

namespace NINA.Polaris.Test;

/// <summary>
/// Telling "no" apart from "not yet".
///
/// Every input here is published asynchronously after the filter wheel
/// connects, so a snapshot taken too early answers "no" to a question whose
/// answer becomes "yes" a second later. Two shipped bugs came from latching on
/// those early answers and never asking again: filter labels lost after a tab
/// reload, then again after a package update.
/// </summary>
[TestFixture]
public class FilterNameRestoreTests {

    private static readonly string[] Saved = { "Red", "Green", "Blue", "H_Alpha", "SII" };
    private static readonly string[] Defaults = { "Filter 1", "Filter 2", "Filter 3", "Filter 4", "Filter 5" };

    [Test]
    public void DriverCameUpWithDefaults_Pushes() {
        Assert.That(FilterNameRestoreService.Decide(true, Saved, Defaults),
            Is.EqualTo(Decision.Push));
    }

    [Test]
    public void NamesAlreadyMatch_DoesNothing() {
        Assert.That(FilterNameRestoreService.Decide(true, Saved, (string[])Saved.Clone()),
            Is.EqualTo(Decision.Nothing));
    }

    [Test]
    public void NoSavedNamesForTheRig_DoesNothing() {
        // Genuinely settled: no later event can produce saved names by itself.
        Assert.That(FilterNameRestoreService.Decide(true, System.Array.Empty<string>(), Defaults),
            Is.EqualTo(Decision.Nothing));
        Assert.That(FilterNameRestoreService.Decide(true, null, Defaults),
            Is.EqualTo(Decision.Nothing));
    }

    [Test]
    public void EditNamesNotAdvertisedYet_Waits() {
        // THE regression. SupportsEditNames is derived live from the INDI
        // FILTER_NAME property, which does not exist in the first moments
        // after a reconnect. Reading that as "this wheel cannot be renamed"
        // is what retired the retry before the property arrived.
        Assert.That(FilterNameRestoreService.Decide(false, Saved, Defaults),
            Is.EqualTo(Decision.Wait),
            "An absent capability is 'not yet', never 'no'.");
    }

    [Test]
    public void SlotListStillArriving_Waits() {
        Assert.That(FilterNameRestoreService.Decide(true, Saved, System.Array.Empty<string>()),
            Is.EqualTo(Decision.Wait));
        Assert.That(FilterNameRestoreService.Decide(true, Saved, null),
            Is.EqualTo(Decision.Wait));
        Assert.That(FilterNameRestoreService.Decide(true, Saved, new[] { "Filter 1", "Filter 2" }),
            Is.EqualTo(Decision.Wait),
            "A partially-published slot list is also 'not yet'.");
    }

    [Test]
    public void OnlyOneNameDiffers_Pushes() {
        var current = (string[])Saved.Clone();
        current[3] = "Filter 4";
        Assert.That(FilterNameRestoreService.Decide(true, Saved, current),
            Is.EqualTo(Decision.Push));
    }
}
