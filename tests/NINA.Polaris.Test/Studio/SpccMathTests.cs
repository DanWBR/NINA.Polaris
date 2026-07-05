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

namespace NINA.Polaris.Test.Studio;

[TestFixture]
public class SpccMathTests {

    private static double[] Grid(double lo = 380, double hi = 720, double step = 5) {
        var xs = new List<double>();
        for (double x = lo; x <= hi + 1e-9; x += step) xs.Add(x);
        return xs.ToArray();
    }

    private static SpccMath.Spectrum Flat(double[] grid, double level = 1.0)
        => new(grid, grid.Select(_ => level).ToArray());

    // Boxcar response = 1 within [lo,hi], else 0, on the given grid.
    private static SpccMath.ResponseCurve Box(double[] grid, double lo, double hi) {
        var r = grid.Select(w => (w >= lo && w <= hi) ? 1.0 : 0.0).ToArray();
        return new SpccMath.ResponseCurve(grid, r);
    }

    [Test]
    public void TeffFromBv_Sunlike_IsAboutSolar() {
        // Ballesteros (2012) for the Sun's B-V (~0.65) lands near 5750 K.
        double t = SpccMath.TeffFromBv(0.656);
        Assert.That(t, Is.InRange(5600.0, 5900.0), $"got {t} K");
        // Bluer star (A0V, B-V ~0) is much hotter; redder (M, B-V ~1.5) cooler.
        Assert.That(SpccMath.TeffFromBv(0.0), Is.GreaterThan(SpccMath.TeffFromBv(0.656)));
        Assert.That(SpccMath.TeffFromBv(1.5), Is.LessThan(SpccMath.TeffFromBv(0.656)));
    }

    [Test]
    public void Interp_GridPointsExact_MidpointLinear_OutsideZero() {
        double[] xs = { 400, 500, 600 };
        double[] ys = { 1, 3, 2 };
        Assert.That(SpccMath.Interp(xs, ys, 500), Is.EqualTo(3).Within(1e-9));
        Assert.That(SpccMath.Interp(xs, ys, 450), Is.EqualTo(2).Within(1e-9)); // (1+3)/2
        Assert.That(SpccMath.Interp(xs, ys, 380), Is.EqualTo(0));
        Assert.That(SpccMath.Interp(xs, ys, 620), Is.EqualTo(0));
    }

    [Test]
    public void IntegrateChannel_ScalesLinearlyWithFlux() {
        var grid = Grid();
        var resp = Box(grid, 480, 580);
        double a = SpccMath.IntegrateChannel(Flat(grid, 1.0), resp);
        double b = SpccMath.IntegrateChannel(Flat(grid, 2.0), resp);
        Assert.That(a, Is.GreaterThan(0));
        Assert.That(b, Is.EqualTo(2 * a).Within(1e-6 * a));
    }

    [Test]
    public void Solve_FlatEverything_GivesUnitGains() {
        var grid = Grid();
        var resp = Box(grid, 400, 700);            // identical R=G=B
        var white = Flat(grid);
        var star = new SpccMath.SpccStar(1, 1, 1, Flat(grid));
        var g = SpccMath.Solve(new[] { star }, white, resp, resp, resp);
        Assert.That(g[0], Is.EqualTo(1).Within(1e-9));
        Assert.That(g[1], Is.EqualTo(1).Within(1e-9));
        Assert.That(g[2], Is.EqualTo(1).Within(1e-9));
    }

    [Test]
    public void Solve_WhiteRefEqualsStar_NeutralisesThatObject() {
        // Distinct, realistic-ish RGB bands. A star whose spectrum equals the
        // white reference must come out neutral after the gains, regardless of
        // the (arbitrary) per-channel system throughput baked into obs.
        var grid = Grid();
        var respR = Box(grid, 570, 690);
        var respG = Box(grid, 480, 580);
        var respB = Box(grid, 400, 500);
        var bb = SpccMath.BlackbodyFromBv(0.656, grid);   // ~solar
        double eR = SpccMath.IntegrateChannel(bb, respR);
        double eG = SpccMath.IntegrateChannel(bb, respG);
        double eB = SpccMath.IntegrateChannel(bb, respB);
        double[] k = { 2.0, 1.0, 0.5 };                    // arbitrary system scale
        var star = new SpccMath.SpccStar(k[0] * eR, k[1] * eG, k[2] * eB, bb);

        var g = SpccMath.Solve(new[] { star }, bb, respR, respG, respB);
        double calR = star.ObsR * g[0];
        double calG = star.ObsG * g[1];
        double calB = star.ObsB * g[2];
        Assert.That(calR, Is.EqualTo(calG).Within(1e-6 * calG));
        Assert.That(calB, Is.EqualTo(calG).Within(1e-6 * calG));
    }

    [Test]
    public void Solve_RedBiasedRaw_AttenuatesRed() {
        // Truly-white star (flat), but the raw over-records red (system too
        // sensitive there). SPCC should pull red gain below 1.
        var grid = Grid();
        var resp = Box(grid, 400, 700);
        var white = Flat(grid);
        var star = new SpccMath.SpccStar(2.0, 1.0, 1.0, Flat(grid));
        var g = SpccMath.Solve(new[] { star }, white, resp, resp, resp);
        Assert.That(g[0], Is.LessThan(1.0));
        Assert.That(g[1], Is.EqualTo(1).Within(1e-9));
        Assert.That(g[2], Is.EqualTo(1).Within(1e-9));
    }

    [Test]
    public void ComputeThroughput_EmptyStars_Throws() {
        var grid = Grid();
        var resp = Box(grid, 400, 700);
        Assert.Throws<ArgumentException>(() =>
            SpccMath.ComputeThroughput(new List<SpccMath.SpccStar>(), resp, resp, resp));
    }

    [Test]
    public void CombineResponse_IsFilterTimesQe() {
        var grid = Grid();
        var filter = Box(grid, 480, 580);
        // QE = constant 0.5 across the band.
        var qe = new SpccMath.ResponseCurve(grid, grid.Select(_ => 0.5).ToArray());
        var combined = SpccMath.CombineResponse(filter, qe);
        // Inside the band the product is 0.5; outside it is 0.
        Assert.That(SpccMath.Interp(combined.WavelengthNm, combined.Response, 500),
            Is.EqualTo(0.5).Within(1e-9));
        Assert.That(SpccMath.Interp(combined.WavelengthNm, combined.Response, 420),
            Is.EqualTo(0.0).Within(1e-9));
    }
}
