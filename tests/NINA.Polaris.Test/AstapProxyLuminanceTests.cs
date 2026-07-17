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
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Test;

/// <summary>
/// FIELD6-14: the multi-channel ASTAP proxy is now a synthetic luminance (mean of
/// the channels) rather than the green plane alone. Green-only starved ASTAP's
/// star detector on green-weak targets (Ha/red nebulae) — ~7 detected stars, exit
/// 1 "did not converge" — while Polaris found ~200 on the full frame.
///
/// These pin the plane math. The end-to-end "does it now solve a frame that
/// previously failed" is a field check (needs ASTAP + a real failing FITS).
/// </summary>
[TestFixture]
public class AstapProxyLuminanceTests {
    /// <summary>Plane-sequential [R…][G…][B…] → per-pixel mean. This is the core
    /// improvement: a red star (bright R, dim G) that green-only nearly lost keeps
    /// meaningful signal in the average.</summary>
    [Test]
    public void Luminance_IsPerPixelMeanAcrossChannels() {
        // 2 pixels, 3 channels, plane-sequential: R=[100,0] G=[10,0] B=[40,0]
        ushort[] data = { 100, 0, /*G*/ 10, 0, /*B*/ 40, 0 };
        var luma = AstapSolver.BuildSolveLuminance(data, planeLen: 2, channels: 3);

        Assert.That(luma.Length, Is.EqualTo(2));
        Assert.That(luma[0], Is.EqualTo(50), "(100+10+40)/3 = 50 — a red star survives; green-only would give 10");
        Assert.That(luma[1], Is.EqualTo(0));
    }

    /// <summary>Rounds to nearest, not truncates: (100+10+41)/3 = 50.33 → 50,
    /// (100+11+41)/3 = 50.67 → 51.</summary>
    [Test]
    public void Luminance_RoundsToNearest() {
        Assert.That(AstapSolver.BuildSolveLuminance(new ushort[] { 100, 10, 41 }, 1, 3)[0], Is.EqualTo(50));
        Assert.That(AstapSolver.BuildSolveLuminance(new ushort[] { 100, 11, 41 }, 1, 3)[0], Is.EqualTo(51));
    }

    /// <summary>A saturated star in every channel stays saturated — the mean can't
    /// exceed ushort.MaxValue, and no overflow in the long accumulator.</summary>
    [Test]
    public void Luminance_AllChannelsSaturated_StaysSaturated() {
        ushort[] data = { 65535, 65535, 65535 };
        Assert.That(AstapSolver.BuildSolveLuminance(data, 1, 3)[0], Is.EqualTo(65535));
    }

    /// <summary>Single-channel input (defensive; the proxy path only fires for
    /// channels>1) copies through unchanged.</summary>
    [Test]
    public void Luminance_SingleChannel_CopiesThrough() {
        ushort[] data = { 7, 8, 9, 10 };
        var luma = AstapSolver.BuildSolveLuminance(data, planeLen: 4, channels: 1);
        Assert.That(luma, Is.EqualTo(data));
    }

    /// <summary>4-channel (LRGB-ish) also averages cleanly.</summary>
    [Test]
    public void Luminance_FourChannels_Averages() {
        // one pixel, 4 channels: 10, 20, 30, 40 → 25
        Assert.That(AstapSolver.BuildSolveLuminance(new ushort[] { 10, 20, 30, 40 }, 1, 4)[0], Is.EqualTo(25));
    }
}
