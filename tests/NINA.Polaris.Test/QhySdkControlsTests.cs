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

using System.Text;
using NINA.Camera.QhySdk;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The pure parts of the QHY control panel: read mode names out of the SDK's
/// buffer, panel values turned into enum indices, and the amp glow choices
/// trimmed to what the camera reports.
/// </summary>
[TestFixture]
public class QhySdkControlsTests {

    // ----- Read mode names -----

    [Test]
    public void ReadModeNameStopsAtTheTerminator() {
        var buf = new byte[256];
        Encoding.ASCII.GetBytes("Extend Fullwell").CopyTo(buf, 0);
        buf[20] = (byte)'x';   // junk after the NUL is not part of the name
        Assert.That(QhySdkCamera.ReadModeName(buf, 2), Is.EqualTo("Extend Fullwell"));
    }

    [Test]
    public void ReadModeNameIsTrimmed() {
        var buf = new byte[32];
        Encoding.ASCII.GetBytes(" High Gain Mode ").CopyTo(buf, 0);
        Assert.That(QhySdkCamera.ReadModeName(buf, 1), Is.EqualTo("High Gain Mode"));
    }

    [Test]
    public void ReadModeNameWithoutATerminatorUsesTheWholeBuffer() {
        Assert.That(QhySdkCamera.ReadModeName(Encoding.ASCII.GetBytes("STD"), 0), Is.EqualTo("STD"));
    }

    [Test]
    public void FailedOrEmptyNameFallsBackToTheIndex() {
        Assert.That(QhySdkCamera.ReadModeName(null, 3), Is.EqualTo("Mode 3"));
        Assert.That(QhySdkCamera.ReadModeName(new byte[16], 0), Is.EqualTo("Mode 0"));
    }

    // ----- Enum indices -----

    [TestCase(0.0, 3, 0)]
    [TestCase(2.0, 3, 2)]
    [TestCase(1.4, 3, 1)]
    [TestCase(1.6, 3, 2)]
    public void ValuesInsideTheListAreIndices(double value, int count, int expected) {
        Assert.That(QhySdkCamera.EnumIndex(value, count), Is.EqualTo(expected));
    }

    [TestCase(-1.0, 3)]
    [TestCase(3.0, 3)]
    [TestCase(0.0, 0)]
    [TestCase(double.NaN, 3)]
    [TestCase(double.PositiveInfinity, 3)]
    public void ValuesOutsideTheListAreRefused(double value, int count) {
        Assert.That(QhySdkCamera.EnumIndex(value, count), Is.Null);
    }

    // ----- Amp glow choices -----

    [Test]
    public void FullRangeKeepsEveryAmpGlowChoice() {
        Assert.That(QhySdkCamera.TrimOptions(QhySdkCamera.AmpGlowModes, 2), Is.EqualTo(new[] { "Auto", "On", "Off" }));
    }

    [Test]
    public void ShorterRangeDropsTheChoicesAboveIt() {
        Assert.That(QhySdkCamera.TrimOptions(QhySdkCamera.AmpGlowModes, 1), Is.EqualTo(new[] { "Auto", "On" }));
    }

    [TestCase(0.0)]     // no range reported
    [TestCase(1.5)]     // not a whole number
    [TestCase(255.0)]   // larger than the list
    public void UnusableRangeKeepsTheWholeList(double sdkMax) {
        Assert.That(QhySdkCamera.TrimOptions(QhySdkCamera.AmpGlowModes, sdkMax), Is.EqualTo(QhySdkCamera.AmpGlowModes));
    }
}
