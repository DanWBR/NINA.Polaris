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

using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Polaris.Services.Studio;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The violet pedestal an ED doublet leaves around bright stars: a broad, faint,
/// blue-only skirt under an otherwise normal star.
///
/// Measured on an SV503 + ASI585MC stack before this was written: identical
/// cores (B/G = 1.00 out to 4 px) and blue running 3 to 8 times the other two
/// from 10 px outwards, with red and green tracking each other all the way.
/// </summary>
[TestFixture]
public class VioletHaloTests {

    private const int W = 161, H = 161, CX = 80, CY = 80;

    /// <summary>A star with a tight core plus, on blue only, a broad skirt.
    /// <paramref name="coreBlue"/> scales the blue CORE, so a genuinely blue or
    /// yellow star can be built too.</summary>
    private static (double[] R, double[] G, double[] B) MakeStar(
            double pedestal, double coreBlue = 1.0, double sky = 0.01) {
        var R = new double[W * H];
        var G = new double[W * H];
        var B = new double[W * H];
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                double d = Math.Sqrt((x - CX) * (x - CX) + (y - CY) * (y - CY));
                double core = Math.Exp(-(d * d) / (2 * 2.2 * 2.2));
                double skirt = pedestal * Math.Exp(-(d * d) / (2 * 12.0 * 12.0));
                int i = y * W + x;
                R[i] = sky + core;
                G[i] = sky + core;
                B[i] = sky + core * coreBlue + skirt;
            }
        }
        return (R, G, B);
    }

    private static double RingMedian(double[] a, double r0, double r1) {
        var v = new List<double>();
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                double d = Math.Sqrt((x - CX) * (x - CX) + (y - CY) * (y - CY));
                if (d >= r0 && d < r1) v.Add(a[y * W + x]);
            }
        }
        v.Sort();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    private static void Run(double[] R, double[] G, double[] B,
                            double amount = 1.0, double radius = 40.0) {
        var m = typeof(VioletHaloService).GetMethod(
            "RemoveVioletHalo",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        m.Invoke(null, new object[] {
            R, G, B, W, H, new List<(int x, int y)> { (CX, CY) }, amount, radius });
    }

    [Test]
    public void TheBlueSkirtIsBroughtDownToTheOtherTwoChannels() {
        var (R, G, B) = MakeStar(pedestal: 0.05);
        double before = RingMedian(B, 12, 18) - RingMedian(G, 12, 18);
        Assert.That(before, Is.GreaterThan(0.005), "o teste tem de comecar com halo azul");

        Run(R, G, B);

        double after = RingMedian(B, 12, 18) - RingMedian(G, 12, 18);
        Assert.That(after, Is.LessThan(before * 0.2),
            $"o pedestal tem de cair: antes {before:F5}, depois {after:F5}");
        Assert.That(after, Is.GreaterThan(-0.002),
            $"e nao pode passar do ponto e virar verde: depois {after:F5}");
    }

    [Test]
    public void TheCoreIsLeftAlone() {
        var (R, G, B) = MakeStar(pedestal: 0.05);
        double coreBefore = RingMedian(B, 0, 3);
        double redBefore = RingMedian(R, 0, 3);

        Run(R, G, B);

        Assert.That(RingMedian(B, 0, 3), Is.EqualTo(coreBefore).Within(1e-9));
        Assert.That(RingMedian(R, 0, 3), Is.EqualTo(redBefore).Within(1e-9));
    }

    /// <summary>The whole reason the target is the other two channels and not
    /// the star's own core: a yellowish star has a blue-poor CORE, and keying
    /// on it drove blue below green out in the halo and painted a green ring
    /// where the violet one had been.</summary>
    [Test]
    public void AYellowStarDoesNotEndUpWithAGreenRing() {
        var (R, G, B) = MakeStar(pedestal: 0.05, coreBlue: 0.66);

        Run(R, G, B);

        double after = RingMedian(B, 12, 18) - RingMedian(G, 12, 18);
        Assert.That(after, Is.GreaterThan(-0.002).And.LessThan(0.004),
            $"halo tem de ficar neutro, nao verde: B-G = {after:F5}");
        Assert.That(RingMedian(B, 0, 3), Is.LessThan(RingMedian(G, 0, 3)),
            "e a estrela continua amarelada no nucleo");
    }

    [Test]
    public void AStarWithNoPedestalIsNotTouched() {
        var (R, G, B) = MakeStar(pedestal: 0.0);
        var before = (double[])B.Clone();

        Run(R, G, B);

        Assert.That(B.Zip(before, (a, b) => Math.Abs(a - b)).Max(), Is.LessThan(1e-6),
            "sem pedestal nao ha o que remover");
    }

    [Test]
    public void AmountScalesTheCorrection() {
        var (R1, G1, B1) = MakeStar(pedestal: 0.05);
        var (R2, G2, B2) = MakeStar(pedestal: 0.05);
        double before = RingMedian(B1, 12, 18) - RingMedian(G1, 12, 18);

        Run(R1, G1, B1, amount: 0.5);
        Run(R2, G2, B2, amount: 1.0);

        double half = RingMedian(B1, 12, 18) - RingMedian(G1, 12, 18);
        double full = RingMedian(B2, 12, 18) - RingMedian(G2, 12, 18);
        Assert.That(half, Is.GreaterThan(full), "metade da intensidade remove menos");
        Assert.That(half, Is.LessThan(before), "mas ainda remove");
    }

    [Test]
    public void TheSkyOutsideTheHaloIsNotDisturbed() {
        var (R, G, B) = MakeStar(pedestal: 0.05);
        double skyBefore = RingMedian(B, 55, 70);

        Run(R, G, B);

        Assert.That(RingMedian(B, 55, 70), Is.EqualTo(skyBefore).Within(1e-9));
    }
    /// <summary>The field bug this clamp exists for.
    ///
    /// A real halo is not azimuthally uniform: a saturated star bleeds more to
    /// one side. The ring gives ONE number for the whole ring, so subtracting it
    /// from every pixel in that ring drove the faint side's blue below zero,
    /// where it clipped, and the star grew a bright YELLOW lobe. That is what
    /// the first run on an SV503 stack produced.
    ///
    /// The invariant that rules it out: a pixel's blue never ends below the mean
    /// of its own red and green, and never below where it started. Nothing about
    /// the star's shape can break it.</summary>
    [Test]
    public void AnAsymmetricHaloDoesNotLeaveAYellowLobe() {
        var (R, G, B) = MakeStar(pedestal: 0.05);
        // one side of the skirt three times the other, like a bleeding star
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                double dx = x - CX, dy = y - CY;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 4) continue;
                double lobe = 1.0 + 1.0 * (dx / Math.Max(d, 1e-9));   // 0..2 across the star
                B[y * W + x] = 0.01 + Math.Exp(-(d * d) / (2 * 2.2 * 2.2))
                             + 0.05 * lobe * Math.Exp(-(d * d) / (2 * 12.0 * 12.0));
            }
        }
        var before = (double[])B.Clone();

        Run(R, G, B);

        double worst = 0; int wi = -1;
        for (int i = 0; i < B.Length; i++) {
            double floor = Math.Min(before[i], 0.5 * (R[i] + G[i]));
            double under = floor - B[i];
            if (under > worst) { worst = under; wi = i; }
        }
        Assert.That(worst, Is.LessThan(1e-9),
            $"azul foi abaixo do par no pixel {wi % W},{wi / W}: {worst:F6} abaixo do piso");

        // e o lado forte realmente foi corrigido
        double strong = RingMedian(B, 12, 18);
        Assert.That(strong, Is.LessThan(RingMedian(before, 12, 18)),
            "o lado forte do halo tem de cair");
    }
}
