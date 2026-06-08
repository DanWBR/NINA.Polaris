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

[TestFixture]
public class PHD2AlgoPresetsTests {

    [Test]
    public void BuiltinNames_ContainsExpectedThree() {
        Assert.That(PHD2AlgoPresets.BuiltinNames, Is.EquivalentTo(new[] { "Default", "Reactive", "Smooth" }));
    }

    [TestCase("Default")]
    [TestCase("Reactive")]
    [TestCase("Smooth")]
    public void GetBuiltin_KnownPresets_ReturnNonNullWithParams(string name) {
        var preset = PHD2AlgoPresets.GetBuiltin(name);
        Assert.That(preset, Is.Not.Null);
        Assert.That(preset!.Name, Is.EqualTo(name));
        Assert.That(preset.Params.Count, Is.GreaterThan(0));
        Assert.That(preset.Description, Is.Not.Empty);
    }

    [Test]
    public void GetBuiltin_UnknownName_ReturnsNull() {
        Assert.That(PHD2AlgoPresets.GetBuiltin("Aggressive"), Is.Null);
        Assert.That(PHD2AlgoPresets.GetBuiltin("Custom"), Is.Null, "Custom is sentinel, not a built-in");
        Assert.That(PHD2AlgoPresets.GetBuiltin(""), Is.Null);
    }

    [Test]
    public void GetBuiltin_CaseInsensitive() {
        Assert.That(PHD2AlgoPresets.GetBuiltin("default"), Is.Not.Null);
        Assert.That(PHD2AlgoPresets.GetBuiltin("REACTIVE"), Is.Not.Null);
    }

    [Test]
    public void DefaultPreset_HasHysteresisAggressivenessAndMinMove() {
        var p = PHD2AlgoPresets.GetBuiltin("Default")!;
        Assert.That(p.Params.Any(x => x.Axis == "ra"  && x.Name == "Hysteresis"));
        Assert.That(p.Params.Any(x => x.Axis == "ra"  && x.Name == "Aggressiveness"));
        Assert.That(p.Params.Any(x => x.Axis == "dec" && x.Name == "Aggressiveness"));
    }

    [Test]
    public void Reactive_HasLowerHysteresisThanSmooth() {
        var reactive = PHD2AlgoPresets.GetBuiltin("Reactive")!;
        var smooth   = PHD2AlgoPresets.GetBuiltin("Smooth")!;
        var rHyst = reactive.Params.First(p => p.Axis == "ra" && p.Name == "Hysteresis").Value;
        var sHyst = smooth.Params.First(p => p.Axis == "ra" && p.Name == "Hysteresis").Value;
        Assert.That(rHyst, Is.LessThan(sHyst),
            "Reactive should react faster (less hysteresis) than Smooth");
    }
}