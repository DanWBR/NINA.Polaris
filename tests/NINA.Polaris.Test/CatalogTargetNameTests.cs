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

/// <summary>The target field's Auto button named the Sculptor Group
/// "Nearest galaxy group beyond the Local Group": the Notable catalogue keeps
/// a description where other catalogues keep a common name.</summary>
[TestFixture]
public class CatalogTargetNameTests {
    [Test]
    public void ANotableObjectIsNamedByItsDesignation() {
        Assert.That(CatalogObject.TargetLabel("Sculptor Group", "Nearest galaxy group beyond the Local Group", "Notable"),
            Is.EqualTo("Sculptor Group"));
        Assert.That(CatalogObject.TargetLabel("3C 273", "Brightest quasar", "Notable"), Is.EqualTo("3C 273"));
    }

    [Test]
    public void ASingleCommonNameIsUsed() {
        Assert.That(CatalogObject.TargetLabel("NGC 253", "Sculptor Galaxy", "NGC"), Is.EqualTo("Sculptor Galaxy"));
        Assert.That(CatalogObject.TargetLabel("NGC 253", "Sculptor Galaxy", null), Is.EqualTo("Sculptor Galaxy"));
    }

    [Test]
    public void AListOfCommonNamesFallsBackToTheDesignation() {
        Assert.That(CatalogObject.TargetLabel("M17", "Checkmark Nebula,Lobster Nebula,Swan Nebula,omega Nebula", "M"),
            Is.EqualTo("M17"));
    }

    [Test]
    public void NoCommonNameOrAVeryLongOneGivesTheDesignation() {
        Assert.That(CatalogObject.TargetLabel("NGC 891", null, "NGC"), Is.EqualTo("NGC 891"));
        Assert.That(CatalogObject.TargetLabel("NGC 891", "  ", "NGC"), Is.EqualTo("NGC 891"));
        Assert.That(CatalogObject.TargetLabel("SN 1885A", "Supernova seen in the Andromeda Galaxy in 1885", "SN"),
            Is.EqualTo("SN 1885A"));
    }
}
