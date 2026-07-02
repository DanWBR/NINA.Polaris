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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Workflow;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the Auto Workflow store contract: save → list → load → delete a
/// named workflow document, round-tripping the raw JSON verbatim (the store
/// is schema-agnostic; the document shape lives on the client).
/// </summary>
[TestFixture]
public class WorkflowStoreTests {

    private readonly List<string> _tempDirs = new();

    private WorkflowStore NewStore() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-wf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = dir
            })
            .Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        return new WorkflowStore(profiles, NullLogger<WorkflowStore>.Instance);
    }

    [TearDown]
    public void TearDown() {
        foreach (var d in _tempDirs) {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
        _tempDirs.Clear();
    }

    [Test]
    public void Save_List_Load_RoundTrips() {
        var store = NewStore();
        const string json = "{\"version\":1,\"name\":\"My Flow\",\"steps\":[" +
            "{\"$type\":\"bge\",\"enabled\":true,\"params\":{\"smoothing\":1.0}}," +
            "{\"$type\":\"editor\",\"enabled\":true,\"params\":{\"format\":\"png\"}}]}";

        store.Save("My Flow", json);

        Assert.That(store.List(), Does.Contain("My Flow"));
        var loaded = store.Load("My Flow");
        Assert.That(loaded, Is.EqualTo(json));   // verbatim round-trip
    }

    [Test]
    public void Load_Missing_ReturnsNull() {
        var store = NewStore();
        Assert.That(store.Load("nope"), Is.Null);
    }

    [Test]
    public void Delete_RemovesEntry() {
        var store = NewStore();
        store.Save("temp", "{\"steps\":[]}");
        Assert.That(store.Delete("temp"), Is.True);
        Assert.That(store.List(), Does.Not.Contain("temp"));
        Assert.That(store.Delete("temp"), Is.False);   // second delete is a no-op
    }

    [Test]
    public void SeedDefaults_WritesStandardOnce_AndRespectsDeletion() {
        var store = NewStore();

        store.SeedDefaults();
        Assert.That(store.List(), Does.Contain("Standard"), "first run seeds Standard");
        var json = store.Load("Standard");
        Assert.That(json, Is.Not.Null);
        // Valid JSON with the expected first + last steps.
        using (var doc = System.Text.Json.JsonDocument.Parse(json!)) {
            var steps = doc.RootElement.GetProperty("steps");
            Assert.That(steps[0].GetProperty("$type").GetString(), Is.EqualTo("autocrop"));
            Assert.That(steps[steps.GetArrayLength() - 1].GetProperty("$type").GetString(),
                Is.EqualTo("export"));
        }

        // Marker guard: a user who deletes it does NOT get it back.
        store.Delete("Standard");
        store.SeedDefaults();
        Assert.That(store.List(), Does.Not.Contain("Standard"),
            "deleting the default must be permanent");
    }

    [Test]
    public void ResolvePath_RejectsTraversal() {
        var store = NewStore();
        // Path separators are stripped so a "../evil" name can never escape
        // the workflows dir. The exact sanitised stem is unimportant; what
        // matters is the file lands directly under store.Dir.
        store.Save("../evil", "{\"steps\":[]}");
        var names = store.List().ToList();
        Assert.That(names.Count, Is.EqualTo(1));
        var saved = Path.Combine(store.Dir, names[0] + ".json");
        Assert.That(File.Exists(saved), Is.True);
        Assert.That(Path.GetFullPath(Path.GetDirectoryName(saved)!),
                    Is.EqualTo(Path.GetFullPath(store.Dir)));
    }
}
