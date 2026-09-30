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

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The ids in the shipped SPCC curve databases are unique.
///
/// <para>This exists because of a field report: the SPCC modal offered no
/// sensor at all while Settings counted 63 profiles installed. The imported
/// Siril database keyed its entries on manufacturer plus model, so the IMX178,
/// IMX183 and IMX585 - each sold in a mono and a colour version - produced two
/// entries sharing one id. The modal keys its option loop on the id, and a
/// repeated key does not lose that one option, it throws and leaves the select
/// empty. The same collision also made <c>BuildResponses</c> resolve an OSC
/// selection to the mono entry, which then refused the OSC filter set.</para>
///
/// <para>Data, not logic, so a test over the files is the thing that keeps it
/// fixed: the generator can be re-run against a newer Siril database at any
/// time, and this is what notices if that reintroduces a collision.</para>
/// </summary>
[TestFixture]
public class SpccCurveIdTests {

    private static readonly string[] Arrays = { "sensors", "filterSets", "whiteRefs" };
    private static readonly string[] Databases = { "curves.json", "curves-siril.json" };

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string SpccDir =>
        Path.Combine(RepoRoot(), "src", "NINA.Polaris", "wwwroot", "catalogs", "spcc");

    private static JsonDocument Load(string file) {
        var path = Path.Combine(SpccDir, file);
        Assert.That(File.Exists(path), $"no SPCC curve database at {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static List<(string Id, string Name)> Entries(string file, string array) {
        using var doc = Load(file);
        var list = new List<(string, string)>();
        if (!doc.RootElement.TryGetProperty(array, out var arr)) return list;
        foreach (var e in arr.EnumerateArray())
            list.Add((e.GetProperty("id").GetString()!, e.GetProperty("name").GetString() ?? ""));
        return list;
    }

    private static void AssertNoDuplicates(IEnumerable<(string Id, string Name)> entries, string what) {
        var dupes = entries.GroupBy(e => e.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({g.Count()}x: {string.Join(" / ", g.Select(e => e.Name))})")
            .ToList();
        Assert.That(dupes, Is.Empty, $"duplicate ids in {what}: {string.Join("; ", dupes)}");
    }

    [Test]
    public void NoDatabaseRepeatsAnIdWithinItself() {
        foreach (var file in Databases)
            foreach (var array in Arrays)
                AssertNoDuplicates(Entries(file, array), $"{file} / {array}");
    }

    [Test]
    public void TheMergedListsTheModalSeesHaveNoRepeatedId() {
        // SpccDatabase.EnumerateAll concatenates the generic database and the
        // Siril import, so uniqueness has to hold across the pair. This is the
        // list the dropdown is actually built from.
        foreach (var array in Arrays)
            AssertNoDuplicates(Databases.SelectMany(f => Entries(f, array)), $"merged {array}");
    }

    [Test]
    public void AChipSoldInBothVariantsKeepsBothEntries() {
        // The fix must not be "drop the duplicate": a mono IMX183 and a colour
        // IMX183 are different sensors with different curves, and an owner of
        // either needs theirs. Both survive, distinguished by id.
        var sensors = Databases.SelectMany(f => Entries(f, "sensors")).ToList();
        foreach (var chip in new[] { "imx178", "imx183", "imx585" }) {
            var matching = sensors.Where(s => s.Id.Contains(chip, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.That(matching.Select(m => m.Id), Has.Some.EndWith("-mono"), chip);
            Assert.That(matching.Select(m => m.Id), Has.Some.EndWith("-osc"), chip);
        }
    }

    [Test]
    public void EverySirilSensorIdSaysWhichVariantItIs() {
        // What stops the collision coming back for the next chip released in
        // both flavours: the variant is part of the id by construction, not by
        // luck of the model string.
        foreach (var (id, name) in Entries("curves-siril.json", "sensors"))
            Assert.That(id, Does.EndWith("-mono").Or.EndWith("-osc"), $"{id} ({name})");
        foreach (var (id, name) in Entries("curves-siril.json", "filterSets"))
            Assert.That(id, Does.EndWith("-mono").Or.EndWith("-osc"), $"{id} ({name})");
    }

    [Test]
    public void TheIdAndTheDeclaredVariantAgree() {
        // A -osc id on a mono entry would resolve to a curve set the caller
        // cannot use: BuildResponses branches on the entry's type, not the id.
        using var doc = Load("curves-siril.json");
        foreach (var s in doc.RootElement.GetProperty("sensors").EnumerateArray()) {
            var id = s.GetProperty("id").GetString()!;
            var type = s.GetProperty("type").GetString()!;
            Assert.That(id, Does.EndWith("-" + type), id);
        }
        foreach (var f in doc.RootElement.GetProperty("filterSets").EnumerateArray()) {
            var id = f.GetProperty("id").GetString()!;
            var forVal = f.GetProperty("for").GetString()!;
            Assert.That(id, Does.EndWith("-" + forVal), id);
        }
    }

    [Test]
    public void EveryEntryStillCarriesTheCurvesItPromises() {
        // Cheap guard that the re-id pass rewrote ids and nothing else.
        using var doc = Load("curves-siril.json");
        foreach (var s in doc.RootElement.GetProperty("sensors").EnumerateArray()) {
            var id = s.GetProperty("id").GetString()!;
            if (s.GetProperty("type").GetString() == "osc")
                foreach (var ch in new[] { "r", "g", "b" })
                    Assert.That(s.TryGetProperty(ch, out _), $"{id} has no {ch} curve");
            else
                Assert.That(s.TryGetProperty("qe", out _), $"{id} has no qe curve");
        }
    }
}

/// <summary>
/// The safety net under <see cref="SpccCurveIdTests"/>: a curve database that
/// does repeat an id must cost that one entry, never the dropdown.
///
/// <para>Users edit these files by hand - that is the documented way to add a
/// measured filter or QE curve - so a collision can arrive without the
/// generator being involved at all, on a machine nobody can debug.</para>
/// </summary>
[TestFixture]
public class SpccOptionsDeduplicationTests {

    private string _dir = null!;

    private const string Curves = """
        {
          "version": 1,
          "sensors": [
            { "id": "dup", "name": "First wins", "type": "mono", "qe": { "wl": [500], "v": [0.5] } },
            { "id": "other", "name": "Untouched", "type": "mono", "qe": { "wl": [500], "v": [0.5] } },
            { "id": "dup", "name": "Second hidden", "type": "osc",
              "r": { "wl": [500], "v": [0.1] }, "g": { "wl": [500], "v": [0.2] }, "b": { "wl": [500], "v": [0.3] } }
          ],
          "filterSets": [
            { "id": "fdup", "name": "Filter one", "for": "osc", "all": { "wl": [500], "v": [0.9] } },
            { "id": "fdup", "name": "Filter two", "for": "osc", "all": { "wl": [500], "v": [0.8] } }
          ],
          "whiteRefs": [
            { "id": "wdup", "name": "Ref one", "kind": "blackbody", "tempK": 5800 },
            { "id": "wdup", "name": "Ref two", "kind": "blackbody", "tempK": 6500 }
          ]
        }
        """;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-spcc-" + Guid.NewGuid().ToString("N")[..8]);
        var spcc = Path.Combine(_dir, "catalogs", "spcc");
        Directory.CreateDirectory(spcc);
        File.WriteAllText(Path.Combine(spcc, "curves.json"), Curves);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* a temp dir; not the test's business */ }
    }

    private JsonElement Options() {
        var db = new NINA.Polaris.Services.Studio.SpccDatabase(
            new DsoCatalogTests.TestEnv { WebRootPath = _dir },
            NullLogger<NINA.Polaris.Services.Studio.SpccDatabase>.Instance);
        // Options() returns an anonymous type; the endpoint serialises it, so
        // read it back the same way the browser does.
        return JsonSerializer.SerializeToDocument(db.Options()).RootElement;
    }

    private static string Field(JsonElement e, string name) {
        if (e.TryGetProperty(name, out var exact)) return exact.GetString()!;
        var lower = char.ToLowerInvariant(name[0]) + name[1..];
        Assert.That(e.TryGetProperty(lower, out var camel), $"no {name} on {e}");
        return camel.GetString()!;
    }

    private static List<string> Ids(JsonElement options, string array) =>
        options.GetProperty(array).EnumerateArray().Select(e => Field(e, "Id")).ToList();

    private static List<string> Names(JsonElement options, string array) =>
        options.GetProperty(array).EnumerateArray().Select(e => Field(e, "Name")).ToList();

    [Test]
    public void ARepeatedSensorIdHidesOnlyThatEntry() {
        var options = Options();
        Assert.That(Ids(options, "sensors"), Is.EqualTo(new[] { "dup", "other" }));
        Assert.That(Names(options, "sensors"), Does.Contain("Untouched"));
    }

    [Test]
    public void TheEntryKeptIsTheOneLookupsResolveTo() {
        // FindById returns the first match, so Options must keep the first too.
        // Showing the second while integrating against the first would be a
        // worse bug than either: silently wrong colour, nothing on screen to
        // say so.
        Assert.That(Names(Options(), "sensors"), Does.Contain("First wins").And.Not.Contain("Second hidden"));
    }

    [Test]
    public void FilterSetsAndWhiteReferencesGetTheSameTreatment() {
        var options = Options();
        Assert.That(Ids(options, "filterSets"), Is.EqualTo(new[] { "fdup" }));
        Assert.That(Ids(options, "whiteRefs"), Is.EqualTo(new[] { "wdup" }));
        Assert.That(Names(options, "filterSets"), Does.Contain("Filter one"));
        Assert.That(Names(options, "whiteRefs"), Does.Contain("Ref one"));
    }

    [Test]
    public void ACleanDatabaseIsPassedThroughUnchanged() {
        var spcc = Path.Combine(_dir, "catalogs", "spcc");
        File.WriteAllText(Path.Combine(spcc, "curves.json"),
            Curves.Replace("\"id\": \"dup\", \"name\": \"Second hidden\"", "\"id\": \"dup2\", \"name\": \"Second hidden\"")
                  .Replace("\"id\": \"fdup\", \"name\": \"Filter two\"", "\"id\": \"fdup2\", \"name\": \"Filter two\"")
                  .Replace("\"id\": \"wdup\", \"name\": \"Ref two\"", "\"id\": \"wdup2\", \"name\": \"Ref two\""));
        var options = Options();
        Assert.That(Ids(options, "sensors"), Has.Count.EqualTo(3));
        Assert.That(Ids(options, "filterSets"), Has.Count.EqualTo(2));
        Assert.That(Ids(options, "whiteRefs"), Has.Count.EqualTo(2));
    }
}
