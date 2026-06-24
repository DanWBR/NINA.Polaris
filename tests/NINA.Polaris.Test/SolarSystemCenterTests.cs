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
/// Tests for the pure helpers behind the "Center on Sun/Moon/planet" workflow
/// (solve-near-and-offset, Mode A): the offset-field selection and body-name
/// parsing. The orchestration itself (slew/solve/sync) needs live hardware so
/// it's covered by end-to-end testing, not here.
/// </summary>
[TestFixture]
public class SolarSystemCenterTests {

    [Test]
    public void OffsetField_KeepsRa() {
        var (ra, _) = SolarSystemCenterService.ComputeOffsetField(13.5, 40.0, 4.0);
        Assert.That(ra, Is.EqualTo(13.5).Within(1e-9), "RA must be unchanged");
    }

    [Test]
    public void OffsetField_NorthernTarget_PushesTowardEquator() {
        // Dec +40 → offset 4° toward the equator → +36.
        var (_, dec) = SolarSystemCenterService.ComputeOffsetField(13.5, 40.0, 4.0);
        Assert.That(dec, Is.EqualTo(36.0).Within(1e-9));
    }

    [Test]
    public void OffsetField_SouthernTarget_PushesTowardEquator() {
        // Dec -20 → offset 4° toward the equator → -16.
        var (_, dec) = SolarSystemCenterService.ComputeOffsetField(6.0, -20.0, 4.0);
        Assert.That(dec, Is.EqualTo(-16.0).Within(1e-9));
    }

    [Test]
    public void OffsetField_OnEquator_NudgesNorth() {
        var (_, dec) = SolarSystemCenterService.ComputeOffsetField(0.0, 0.0, 4.0);
        Assert.That(dec, Is.EqualTo(4.0).Within(1e-9));
    }

    [Test]
    public void OffsetField_NearPole_ClampsToSlewable() {
        // Dec +88, offset 4° toward equator → +84 (well within the clamp).
        var (_, dec) = SolarSystemCenterService.ComputeOffsetField(2.0, 88.0, 4.0);
        Assert.That(dec, Is.EqualTo(84.0).Within(1e-9));
        Assert.That(dec, Is.LessThanOrEqualTo(89.0));
    }

    [TestCase("Sun")]
    [TestCase("moon")]
    [TestCase("JUPITER")]
    [TestCase(" Saturn ")]
    public void TryParseBody_AcceptsSupportedNames(string name) {
        Assert.That(SolarSystemCenterService.TryParseBody(name, out _), Is.True);
    }

    [TestCase("Pluto")]
    [TestCase("Andromeda")]
    [TestCase("")]
    [TestCase(null)]
    public void TryParseBody_RejectsUnsupported(string? name) {
        Assert.That(SolarSystemCenterService.TryParseBody(name, out _), Is.False);
    }

    [Test]
    public void SupportedBodies_CoversSunMoonAndEightPlanetsMinusEarthPluto() {
        // Sun + Moon + the 7 planets we can image (Mercury..Neptune) = 9.
        Assert.That(SolarSystemCenterService.SupportedBodies.Count, Is.EqualTo(9));
        foreach (var b in SolarSystemCenterService.SupportedBodies)
            Assert.That(SolarSystemCenterService.TryParseBody(b, out _), Is.True,
                $"SupportedBodies entry '{b}' must be parseable");
    }
}
