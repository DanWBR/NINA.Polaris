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

using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// The browser's IANA timezone goes into a timedatectl argument, so the id it
/// sends is validated before it gets anywhere near a process start.
/// </summary>
[TestFixture]
public class ClockSyncTimeZoneTests {

    [TestCase("UTC")]
    [TestCase("America/Fortaleza")]
    [TestCase("America/Sao_Paulo")]
    [TestCase("Europe/London")]
    [TestCase("Etc/GMT+3")]
    [TestCase("America/Argentina/Buenos_Aires")]
    public void PlausibleId_AcceptsRealZones(string id) {
        Assert.That(ClockSyncService.IsPlausibleTimeZoneId(id), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("America/Sao Paulo")]       // a space would split the argument
    [TestCase("UTC; rm -rf /")]
    [TestCase("UTC\nEurope/London")]
    [TestCase("$(id)")]
    [TestCase("`id`")]
    [TestCase("../../etc/passwd")]
    [TestCase("/etc/localtime")]
    [TestCase("America/")]
    public void PlausibleId_RejectsAnythingElse(string? id) {
        Assert.That(ClockSyncService.IsPlausibleTimeZoneId(id), Is.False);
    }

    [Test]
    public void PlausibleId_RejectsAnOverlongId() {
        Assert.That(ClockSyncService.IsPlausibleTimeZoneId(new string('a', 65)), Is.False);
    }
}
