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
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// The flat wizard runs on a rig without a filter wheel: no filter named
/// means one set with whatever is in the light path.
/// </summary>
[TestFixture]
public class FlatWizardNoWheelTests {

    [Test]
    public void NoFilterNamed_IsOneSetWithTheFilterInPlace() {
        Assert.That(FlatWizardService.NormalizeFilters(null), Is.EqualTo(new[] { "" }));
        Assert.That(FlatWizardService.NormalizeFilters(new List<string>()), Is.EqualTo(new[] { "" }));
        Assert.That(FlatWizardService.NormalizeFilters(new List<string> { " ", "" }), Is.EqualTo(new[] { "" }));
    }

    [Test]
    public void NamedFilters_KeepTheirOrder() {
        Assert.That(FlatWizardService.NormalizeFilters(new List<string> { "Ha", " ", "OIII " }),
            Is.EqualTo(new[] { "Ha", "OIII" }));
    }
}
