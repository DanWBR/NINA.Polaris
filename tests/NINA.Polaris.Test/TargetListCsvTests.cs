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


using System.Collections.Generic;
using NUnit.Framework;
using NINA.Polaris.Endpoints;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test;

/// <summary>Reading a target list exported as CSV into favourites: header
/// discovery by name, every coordinate spelling the exports use, bad rows
/// reported instead of aborting, and the duplicate rule.</summary>
[TestFixture]
public class TargetListCsvTests {

    [Test]
    public void TelescopiusStyleExport_ReadsNameCommonNameTypeAndCoordinates() {
        var csv = "My list\n"
            + "Catalogue Entry,Familiar Name,Type,Right Ascension,Declination,Magnitude\n"
            + "M 42,Orion Nebula,Nebula,05h 35m 17s,-05° 23' 28\",4.0\n"
            + "\"NGC 7000\",\"North America Nebula\",Nebula,20:58:47,+44:19:48,4\n"
            + "M 31,,Galaxy,00h42m44.3s,+41d16m09s,3.4\n";
        var r = TargetListCsv.Parse(csv);
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Targets.Count, Is.EqualTo(3));
        Assert.That(r.Targets[0].Name, Is.EqualTo("M 42"));
        Assert.That(r.Targets[0].CommonName, Is.EqualTo("Orion Nebula"));
        Assert.That(r.Targets[0].RaHours, Is.EqualTo(5.588056).Within(1e-5));
        Assert.That(r.Targets[0].DecDeg, Is.EqualTo(-5.391111).Within(1e-5));
        Assert.That(r.Targets[1].RaHours, Is.EqualTo(20.979722).Within(1e-5));
        Assert.That(r.Targets[1].DecDeg, Is.EqualTo(44.33).Within(1e-5));
        Assert.That(r.Targets[2].CommonName, Is.Null);
        Assert.That(r.Targets[2].DecDeg, Is.EqualTo(41.269167).Within(1e-5));
    }

    [Test]
    public void DecimalDegreesRa_AndSemicolons_AndBadRows() {
        var csv = "name;ra;dec\nA;83.82;-5.39\nB;5.59;-5.39\nC;;\nD;abc;12\n";
        var r = TargetListCsv.Parse(csv);
        Assert.That(r.Targets.Count, Is.EqualTo(2));
        Assert.That(r.Targets[0].RaHours, Is.EqualTo(83.82 / 15).Within(1e-9), "a decimal RA above 24 is degrees");
        Assert.That(r.Targets[1].RaHours, Is.EqualTo(5.59).Within(1e-9), "a small decimal RA stays hours");
        Assert.That(r.Skipped, Is.EqualTo(2));
        Assert.That(r.Errors.Count, Is.EqualTo(2));
    }

    [Test]
    public void NoUsableHeader_IsOneClearError() {
        var r = TargetListCsv.Parse("foo,bar\n1,2\n");
        Assert.That(r.Targets, Is.Empty);
        Assert.That(r.Errors.Count, Is.EqualTo(1));
        Assert.That(r.Errors[0], Does.Contain("header"));
    }

    [Test]
    public void FindFavourite_MatchesByNameOrByPosition() {
        var list = new List<FavouriteTarget> {
            new() { Name = "M 42", RaHours = 5.588, DecDeg = -5.391 },
        };
        Assert.That(SkyEndpoints.FindFavourite(list, "m 42", 0, 0), Is.Not.Null, "name, case-insensitive");
        Assert.That(SkyEndpoints.FindFavourite(list, "Orion", 5.5883, -5.3915), Is.Not.Null, "same spot, other name");
        Assert.That(SkyEndpoints.FindFavourite(list, "Orion", 5.7, -5.391), Is.Null, "a different spot");
    }
}
