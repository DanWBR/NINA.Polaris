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
using NINA.Polaris.Services.Broadcast;
using NINA.Polaris.Services.Sky;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The descriptions Polaris ships for the well known objects.
///
/// <para>This text is read out on a broadcast, in front of an audience, from a
/// host that may have no network. It is the one part of the card nobody
/// reviews again after it is written, so what can be checked mechanically is
/// checked here: that every entry is reachable under the key the card looks
/// up, that none of them silently replaces another, and that none is longer
/// than the panel can hold.</para>
/// </summary>
[TestFixture]
public class ObjectNotesBundleTests {

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string NotesDir =>
        Path.Combine(RepoRoot(), "src", "NINA.Polaris", "wwwroot", "sky", "data", "skydata");

    private static string EnglishPath => Path.Combine(NotesDir, "object-notes.en.json");

    private static Dictionary<string, string> Raw() {
        Assert.That(File.Exists(EnglishPath), $"no bundled notes at {EnglishPath}");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(EnglishPath));
        Assert.That(parsed, Is.Not.Null.And.Not.Empty);
        return parsed!;
    }

    [Test]
    public void EveryEntryIsFiledUnderADesignationTheCardCanLookUp() {
        // The card looks notes up by the slug it derives from the catalogue
        // name. An entry whose key does not slug is unreachable: it sits in
        // the file looking present and never appears on screen.
        foreach (var key in Raw().Keys) {
            Assert.That(DsoThumbSlug.For(key), Is.Not.Empty,
                $"'{key}' is not a catalogue designation, so nothing will ever find it");
        }
    }

    [Test]
    public void NoTwoEntriesCollapseOntoTheSameKey() {
        // "M42" and "M 42" and "m42" are one entry once normalised, and the
        // last one loaded wins. A duplicate is therefore a text silently
        // replacing another, which is exactly what happened while writing
        // this file: a stray "NGC6960 " overwrote the real Veil entry.
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in Raw().Keys) {
            var slug = ObjectNoteStore.KeyFor(key);
            if (!byKey.TryGetValue(slug, out var list)) byKey[slug] = list = new List<string>();
            list.Add(key);
        }
        var clashes = byKey.Where(kv => kv.Value.Count > 1)
                           .Select(kv => kv.Key + ": " + string.Join(", ", kv.Value.Select(x => "'" + x + "'")));
        Assert.That(clashes, Is.Empty, "duplicate keys after normalisation");
    }

    [Test]
    public void EveryDescriptionFitsTheCardAndSaysSomething() {
        foreach (var (key, text) in Raw()) {
            Assert.That(text.Trim(), Is.Not.Empty, key);
            Assert.That(text.Trim().Length, Is.GreaterThan(30),
                $"{key}: too short to be worth preferring over the generated sentence");
            // The card wraps the description to five lines. Longer than this
            // and the tail is ellipsised away, so it may as well not be
            // written.
            Assert.That(text.Length, Is.LessThanOrEqualTo(260), $"{key}: too long for the card");
        }
    }

    [Test]
    public void TheTextIsWrittenTheWayPolarisWritesText() {
        foreach (var (key, text) in Raw()) {
            Assert.That(text, Does.Not.Contain("—"), $"{key}: no em dashes in user-facing text");
            Assert.That(text, Does.Not.Contain("–"), $"{key}: no en dashes in user-facing text");
            Assert.That(text.Trim(), Does.EndWith("."), $"{key}: finish the sentence");
            // A digit is fine: some objects are named for one, as 47 Tucanae is.
            var first = text.Trim()[0];
            Assert.That(char.IsUpper(first) || char.IsDigit(first), Is.True,
                $"{key}: start with a capital");
        }
    }

    [Test]
    public void TheFamousTargetsAreCovered() {
        // Not a coverage quota, just the objects a broadcast is most likely
        // to be pointed at. Everything else falls back to the sentence built
        // from the catalogue, which is always correct if never interesting.
        var notes = Raw().Keys.Select(ObjectNoteStore.KeyFor).ToHashSet(StringComparer.Ordinal);
        foreach (var must in new[] { "M42", "M31", "M45", "M51", "M13", "M27", "M57", "M81", "M82",
                                     "M101", "M104", "NGC7000", "IC434", "NGC7293", "NGC5128" }) {
            Assert.That(notes, Does.Contain(must), $"no bundled text for {must}");
        }
    }

    [Test]
    public void TheStoreReadsTheShippedFile() {
        // End to end against the real file, through the same code path the
        // broadcast uses.
        var store = new ObjectNoteStore(NotesDir,
            Path.Combine(Path.GetTempPath(), "polaris-notes-unused"), NullLogger.Instance);

        Assert.That(store.Bundled("M42", "en"), Does.Contain("Trapezium"));
        // The catalogue spells it "M 42" in places and the card normalises
        // before asking, so both have to arrive at the same entry.
        Assert.That(store.Bundled(ObjectNoteStore.KeyFor("M 42"), "en"), Is.Not.Null);
        Assert.That(store.Bundled(ObjectNoteStore.KeyFor("ngc 7000"), "en"), Is.Not.Null);

        // A language with no catalogue of its own still gets the English
        // text rather than nothing.
        Assert.That(store.Bundled("M42", "pt"), Is.EqualTo(store.Bundled("M42", "en")));

        // And an object nobody wrote about returns null, which is what sends
        // the card to the online lookup and then to the generated sentence.
        Assert.That(store.Bundled("PGC12345", "en"), Is.Null);
    }
}
