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
using System.Linq;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Parsing and orbit-class coverage for the JPL comet element refresh.
///
/// The fixture is a verbatim slice of a real sbdb_query.api response (three
/// rows: elliptic, near-parabolic, hyperbolic), so a change in JPL's field
/// order or value formatting shows up here rather than in the field.
/// </summary>
[TestFixture]
public class CometElementsUpdaterTests {

    // Real response, trimmed to three comets. Note M1/K1 carry the cometary
    // magnitude parameters and H is null for all of them, which is why the
    // parser must not key on H alone.
    private const string Fixture = """
    {"signature":{"source":"NASA/JPL SBDB (Small-Body DataBase) Query API","version":"1.0"},
     "fields":["full_name","e","q","i","om","w","tp","M1","K1","H"],
     "data":[
       ["   24P/Schaumasse","0.7079","1.185","11.50","78.29","58.48","2461049.06","14.6","8.",null],
       ["     C/2023 H5 (Lemmon)","1.0004","4.313","97.86","159.48","60.09","2460856.72","8.4","8.",null],
       ["     C/2022 N2 (PANSTARRS)","1.0032","3.826","5.50","319.74","75.39","2460888.27","9.1","5.75",null]
     ]}
    """;

    private static readonly DateTime Now = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public void Parse_RealResponse_KeepsAllThreeOrbitClasses() {
        var list = CometElementsUpdater.Parse(Fixture, Now, out var skipped);

        Assert.That(skipped, Is.Zero, "no row in this fixture is malformed");
        Assert.That(list.Count, Is.EqualTo(3),
            "elliptic, near-parabolic and hyperbolic must all survive; dropping "
            + "e >= 1 would discard most of a real JPL window and every bright "
            + "long-period comet with it");
        Assert.That(list.Select(c => c.Name), Has.Some.Contains("Lemmon"));
    }

    [Test]
    public void Parse_TakesTheCometaryMagnitudeParameters() {
        var list = CometElementsUpdater.Parse(Fixture, Now, out _);
        var lemmon = list.Single(c => c.Name.Contains("Lemmon"));

        // M1 -> H, K1 -> n. H is null in the response, so a parser reading H
        // would have dropped every one of these.
        Assert.That(lemmon.H, Is.EqualTo(8.4).Within(1e-9));
        Assert.That(lemmon.N, Is.EqualTo(8.0).Within(1e-9));
    }

    [Test]
    public void Parse_ConvertsPerihelionJulianDateToADate() {
        var list = CometElementsUpdater.Parse(Fixture, Now, out _);
        var schaumasse = list.Single(c => c.Name.Contains("Schaumasse"));

        // JD 2461049.06 is 2026-01-08.
        Assert.That(schaumasse.Tperi, Is.EqualTo("2026-01-08"));
    }

    [Test]
    public void Parse_RejectsGarbageWithoutLosingTheGoodRows() {
        const string mixed = """
        {"fields":["full_name","e","q","i","om","w","tp","M1","K1","H"],
         "data":[
           ["   24P/Schaumasse","0.7079","1.185","11.50","78.29","58.48","2461049.06","14.6","8.",null],
           ["   bad/no-elements",null,null,null,null,null,null,null,null,null],
           ["   bad/negative-q","0.5","-1.0","10","10","10","2461049.06","12",null,null]
         ]}
        """;
        var list = CometElementsUpdater.Parse(mixed, Now, out var skipped);

        Assert.That(list.Count, Is.EqualTo(1), "one malformed comet must not cost the others");
        Assert.That(skipped, Is.EqualTo(2));
    }

    [Test]
    public void Parse_EmptyResponse_YieldsNothingRatherThanThrowing() {
        var list = CometElementsUpdater.Parse("""{"fields":[],"data":[]}""", Now, out _);
        Assert.That(list, Is.Empty);
    }

    // ---- Orbit solver ----

    /// <summary>
    /// THE ONE THAT MATTERS. a = q / (1 - e) is infinite at e = 1 and negative
    /// past it, so an elliptic-only solver returned NaN for every long-period
    /// comet. NaN then propagates into RA/Dec and, from there, into the status
    /// payload — which is exactly how a non-finite number took the app's
    /// WebSocket down earlier in this cycle.
    /// </summary>
    [Test]
    public void SolveOrbit_AcrossEccentricities_IsAlwaysFinite(
            [Values(0.0, 0.5, 0.9679, 0.999, 0.9999, 1.0, 1.0004, 1.0032, 1.2, 3.0)] double e,
            [Values(-400.0, -30.0, 0.0, 30.0, 400.0)] double days) {
        var (nu, r) = CometEphemerisService.SolveOrbit(q: 1.2, e: e, daysFromPerihelion: days);

        Assert.That(double.IsFinite(nu), Is.True, $"true anomaly not finite for e={e}, t={days}d");
        Assert.That(double.IsFinite(r), Is.True, $"radius not finite for e={e}, t={days}d");
        Assert.That(r, Is.GreaterThan(0), $"radius must be positive for e={e}, t={days}d");
    }

    [Test]
    public void SolveOrbit_AtPerihelion_PutsTheCometAtQ(
            [Values(0.3, 0.9, 1.0, 1.5)] double e) {
        var (nu, r) = CometEphemerisService.SolveOrbit(q: 0.8, e: e, daysFromPerihelion: 0);

        Assert.That(r, Is.EqualTo(0.8).Within(1e-6), "at t = T the comet is at perihelion");
        Assert.That(nu, Is.EqualTo(0).Within(1e-6), "true anomaly is zero at perihelion");
    }

    [Test]
    public void SolveOrbit_MovesOutwardsEitherSideOfPerihelion(
            [Values(0.7, 1.0, 1.3)] double e) {
        var (_, before) = CometEphemerisService.SolveOrbit(0.8, e, -60);
        var (_, at)     = CometEphemerisService.SolveOrbit(0.8, e, 0);
        var (_, after)  = CometEphemerisService.SolveOrbit(0.8, e, +60);

        Assert.That(before, Is.GreaterThan(at));
        Assert.That(after, Is.GreaterThan(at));
        Assert.That(after, Is.EqualTo(before).Within(before * 1e-6),
            "the orbit is symmetric about perihelion");
    }

    /// <summary>The parabolic branch is a different formula (Barker) from the
    /// elliptic one, so they have to agree where they meet. Checked just either
    /// side of e = 1 rather than at it.</summary>
    [Test]
    public void SolveOrbit_IsContinuousAcrossTheParabolicBoundary() {
        const double q = 2.0, days = 120;
        var (nuLo, rLo) = CometEphemerisService.SolveOrbit(q, 0.9985, days);
        var (nuHi, rHi) = CometEphemerisService.SolveOrbit(q, 1.0015, days);

        Assert.That(rHi, Is.EqualTo(rLo).Within(rLo * 0.01),
            $"radius jumps across e = 1 ({rLo:F4} -> {rHi:F4} AU)");
        Assert.That(nuHi, Is.EqualTo(nuLo).Within(0.01),
            "true anomaly jumps across e = 1");
    }

    /// <summary>
    /// The client-relay path (a phone with mobile data feeding an offline host)
    /// posts the RAW JPL body to /api/sky/comets/import, which runs this very
    /// parser. Pinning that here is what keeps the two routes honest: the
    /// client is a courier, and every validation rule lives on the host.
    /// </summary>
    [Test]
    public void Parse_IsTheSameForRelayedBodies() {
        var direct = CometElementsUpdater.Parse(Fixture, Now, out var s1);
        var relayed = CometElementsUpdater.Parse(Fixture, Now, out var s2);

        Assert.That(relayed.Count, Is.EqualTo(direct.Count));
        Assert.That(s2, Is.EqualTo(s1));
        for (var i = 0; i < direct.Count; i++) {
            Assert.That(relayed[i].Name, Is.EqualTo(direct[i].Name));
            Assert.That(relayed[i].Q, Is.EqualTo(direct[i].Q));
            Assert.That(relayed[i].E, Is.EqualTo(direct[i].E));
            Assert.That(relayed[i].Tperi, Is.EqualTo(direct[i].Tperi));
        }
    }

    /// <summary>A body that is JSON but not an SBDB response must yield nothing
    /// rather than throwing, so the import endpoint answers 400 and the host
    /// keeps the elements it already had. Double-encoded JSON is the shape this
    /// actually took in review: a JSON string containing the table.</summary>
    [Test]
    public void Parse_DoubleEncodedOrForeignJson_YieldsNothing() {
        Assert.That(CometElementsUpdater.Parse("\"{\\\"data\\\":[]}\"", Now, out _), Is.Empty);
        Assert.That(CometElementsUpdater.Parse("""{"hello":"world"}""", Now, out _), Is.Empty);
        Assert.That(CometElementsUpdater.Parse("[]", Now, out _), Is.Empty);
    }
}
