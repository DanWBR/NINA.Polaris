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

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services.Broadcast;
using NINA.Polaris.Services.Sky;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Where an object description comes from when nobody is asked: the texts
/// Polaris ships and the texts a previous night kept. Both have to work with
/// the network unplugged, which is the normal state of a host at a dark site.
/// </summary>
[TestFixture]
public class ObjectNoteStoreTests {

    private string _root = null!;
    private string _notesDir = null!;
    private string _cacheDir = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), "polaris-notes-" + Guid.NewGuid().ToString("N")[..8]);
        _notesDir = Path.Combine(_root, "skydata");
        _cacheDir = Path.Combine(_root, "notes-cache");
        Directory.CreateDirectory(_notesDir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ObjectNoteStore Store() => new(_notesDir, _cacheDir, NullLogger.Instance);

    private void WriteNotes(string lang, string json) =>
        File.WriteAllText(Path.Combine(_notesDir, $"object-notes.{lang}.json"), json);

    /// <summary>A cache entry with a chosen age, which is the only way to test
    /// a hundred and eighty day lifetime in a unit test.</summary>
    private void WriteCache(string key, string lang, string extract, bool found, TimeSpan age) {
        var path = new ObjectNoteStore(_notesDir, _cacheDir, NullLogger.Instance).CachePath(key, lang);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var when = (DateTime.UtcNow - age).ToString("o", CultureInfo.InvariantCulture);
        File.WriteAllText(path,
            $"{{\"Extract\":{System.Text.Json.JsonSerializer.Serialize(extract)},"
          + $"\"Found\":{(found ? "true" : "false")},\"FetchedUtc\":\"{when}\"}}");
    }

    [Test]
    public void NoFilesAtAllIsNotAFailure() {
        // The base install before anyone has broadcast anything.
        var s = Store();
        Assert.That(s.Bundled("M42", "en"), Is.Null);
        Assert.That(s.Cached("M42", "en"), Is.Null);
        Assert.That(s.ShouldFetch("M42", "en"), Is.True);
    }

    [Test]
    public void TheKeyIsTheDesignationHoweverItIsSpelled() {
        Assert.That(ObjectNoteStore.KeyFor("M 42"), Is.EqualTo("M42"));
        Assert.That(ObjectNoteStore.KeyFor("m42"), Is.EqualTo("M42"));
        Assert.That(ObjectNoteStore.KeyFor("NGC 0224"), Is.EqualTo("NGC224"));
        Assert.That(ObjectNoteStore.KeyFor("Sharpless 155"), Is.EqualTo("SH2155"));
        // Not a designation, so it is filed under itself rather than lost.
        Assert.That(ObjectNoteStore.KeyFor("Barnard's Loop"), Is.EqualTo("BARNARD'S LOOP"));
    }

    [Test]
    public void TheFileCanSpellTheKeyAnyWayToo() {
        WriteNotes("en", """{ "M 42": "The Orion Nebula.", "ngc0224": "Andromeda." }""");
        var s = Store();
        Assert.That(s.Bundled("M42", "en"), Is.EqualTo("The Orion Nebula."));
        Assert.That(s.Bundled("NGC224", "en"), Is.EqualTo("Andromeda."));
    }

    [Test]
    public void EnglishFillsTheGapsEntryByEntry() {
        // The English set is the complete one; the others are filled in over
        // time. A half translated catalogue must not mean a blank card for the
        // entries it has not reached.
        WriteNotes("en", """{ "M42": "A stellar nursery.", "M31": "The nearest large galaxy." }""");
        WriteNotes("pt", """{ "M42": "Um bercario de estrelas." }""");
        var s = Store();
        Assert.That(s.Bundled("M42", "pt"), Is.EqualTo("Um bercario de estrelas."));
        Assert.That(s.Bundled("M31", "pt"), Is.EqualTo("The nearest large galaxy."),
            "no Portuguese entry yet, and English is better than nothing");
        Assert.That(s.Bundled("M13", "pt"), Is.Null);
    }

    [Test]
    public void AnEmptyEntryIsNotAnEntry() {
        WriteNotes("en", """{ "M42": "In English." }""");
        WriteNotes("pt", """{ "M42": "   " }""");
        Assert.That(Store().Bundled("M42", "pt"), Is.EqualTo("In English."));
    }

    [Test]
    public void UnreadableNotesAreNotAStartupFailure() {
        WriteNotes("en", "{ nope");
        Assert.That(Store().Bundled("M42", "en"), Is.Null);
    }

    [Test]
    public void OurOwnTextMeansNoLookup() {
        WriteNotes("en", """{ "M42": "A stellar nursery." }""");
        Assert.That(Store().ShouldFetch("M42", "en"), Is.False);
    }

    [Test]
    public void ACachedTextIsUsedUntilItIsHalfAYearOld() {
        WriteCache("M42", "en", "From Wikipedia.", found: true, age: TimeSpan.FromDays(30));
        Assert.That(Store().Cached("M42", "en"), Is.EqualTo("From Wikipedia."));
        Assert.That(Store().ShouldFetch("M42", "en"), Is.False);

        WriteCache("M42", "en", "From Wikipedia.", found: true, age: ObjectNoteStore.HitTtl + TimeSpan.FromDays(1));
        Assert.That(Store().Cached("M42", "en"), Is.Null);
        Assert.That(Store().ShouldFetch("M42", "en"), Is.True);
    }

    [Test]
    public void KnowingThereIsNoArticleIsWorthRemembering() {
        // Without a cached miss, an object nobody has written about is looked
        // up again on every target change, all night, every night.
        WriteCache("PGC12345", "en", "", found: false, age: TimeSpan.FromDays(1));
        var s = Store();
        Assert.That(s.Cached("PGC12345", "en"), Is.Null);
        Assert.That(s.MissIsFresh("PGC12345", "en"), Is.True);
        Assert.That(s.ShouldFetch("PGC12345", "en"), Is.False);

        // But it is worth asking again eventually; articles get written.
        WriteCache("PGC12345", "en", "", found: false, age: ObjectNoteStore.MissTtl + TimeSpan.FromDays(1));
        Assert.That(Store().ShouldFetch("PGC12345", "en"), Is.True);
    }

    [Test]
    public void WhatWasRememberedComesBack() {
        var s = Store();
        s.Remember("M13", "pt", "Um aglomerado globular em Hercules.");
        Assert.That(Store().Cached("M13", "pt"), Is.EqualTo("Um aglomerado globular em Hercules."));
        // Per language: a Portuguese text is not an English one.
        Assert.That(Store().Cached("M13", "en"), Is.Null);

        s.Remember("M13", "en", null);
        Assert.That(Store().MissIsFresh("M13", "en"), Is.True);
    }

    [Test]
    public void AKeyWithAwkwardCharactersStillMakesAFilename() {
        var s = Store();
        s.Remember("BARNARD'S LOOP", "en", "A supernova remnant in Orion.");
        Assert.That(Store().Cached("BARNARD'S LOOP", "en"), Is.EqualTo("A supernova remnant in Orion."));
        Assert.DoesNotThrow(() => s.Remember(new string('X', 300), "en", "long"));
    }

    [Test]
    public void ALongParagraphIsCutAtASentence() {
        var text = "The Orion Nebula is a diffuse nebula situated in the Milky Way. "
                 + "It is south of Orion's Belt in the constellation of Orion, and is known as the middle star. "
                 + "It is one of the brightest nebulae and is visible to the naked eye in the night sky. "
                 + "It is 1,344 light years away and is the closest region of massive star formation to Earth.";
        var shortened = ObjectNoteStore.Shorten(text);
        Assert.That(shortened.Length, Is.LessThanOrEqualTo(320));
        Assert.That(shortened, Does.EndWith("."));
        Assert.That(shortened, Does.StartWith("The Orion Nebula is a diffuse nebula"));
        Assert.That(shortened, Does.Not.Contain("closest region"), "the tail was cut");
    }

    [Test]
    public void ShortTextIsLeftAlone() {
        Assert.That(ObjectNoteStore.Shorten("A globular cluster in Hercules."),
            Is.EqualTo("A globular cluster in Hercules."));
        // Line breaks and runs of spaces would otherwise reach the renderer.
        Assert.That(ObjectNoteStore.Shorten("Two\nlines   apart."), Is.EqualTo("Two lines apart."));
    }

    [Test]
    public void AParagraphWithNoEarlySentenceEndIsCutAtAWord() {
        // One very long sentence: cutting at the full stop would leave nothing
        // or everything, so it falls back to a word boundary and an ellipsis.
        var text = new string('a', 40) + " " + string.Join(" ", Enumerable.Repeat("word", 120)) + ".";
        var shortened = ObjectNoteStore.Shorten(text);
        Assert.That(shortened.Length, Is.LessThanOrEqualTo(324));
        Assert.That(shortened, Does.EndWith("..."));
        Assert.That(shortened, Does.Not.EndWith("wor..."), "cut between words, not inside one");
    }

    [Test]
    public void TheSlugRuleIsTheOneTheCutoutsAreNamedFor() {
        Assert.That(DsoThumbSlug.For("NGC 7000"), Is.EqualTo("NGC7000"));
        Assert.That(DsoThumbSlug.For("ic1396a"), Is.EqualTo("IC1396A"));
        Assert.That(DsoThumbSlug.For("Sh2-155"), Is.EqualTo("SH2155"));
        // A common name has no digits, so it cannot slug. The caller has to go
        // through the catalogue to reach a designation, and that is the point.
        Assert.That(DsoThumbSlug.For("Lagoon Nebula"), Is.Empty);
        Assert.That(DsoThumbSlug.For(""), Is.Empty);
        Assert.That(DsoThumbSlug.For(null), Is.Empty);
    }

    [Test]
    public void TheDesignationIsWhatOtherLanguagesAnswerTo() {
        // The common name looks like the better thing to ask Wikipedia for,
        // and for English it is. But the catalogue common names are English,
        // and only en.wikipedia has them: pt.wikipedia.org 404s on "Orion
        // Nebula" and resolves both "M42" and "NGC 1976" to the Portuguese
        // article. Checked against the live endpoint. Ask with the designation
        // first, or every non-English broadcast quietly falls back to English.
        var m42 = new DsoCatalog.DsoObject(
            Name: "M42", CommonName: "Orion Nebula,Great Nebula in Orion", Type: "Emission nebula",
            RaHours: 5.59, DecDeg: -5.39, Magnitude: 4.0, SizeArcmin: 85,
            Constellation: "Orion", Catalog: "M", CatalogId: "42",
            Aliases: new[] { "NGC 1976" });

        var titles = ObjectCardService.WikipediaTitles(m42, "Orion Nebula");
        Assert.That(titles[0], Is.EqualTo("M42"));
        Assert.That(titles, Does.Contain("Orion Nebula"));
        Assert.That(titles.Count, Is.LessThanOrEqualTo(2), "two round trips per language is the budget");

        // A hand typed target the catalogue does not know still gets asked
        // about, under the only name there is.
        Assert.That(ObjectCardService.WikipediaTitles(null, "Barnard's Loop"),
            Is.EqualTo(new[] { "Barnard's Loop" }));
    }

    [Test]
    public void TheBroadcastSpeaksTheInterfaceLanguageOrEnglish() {
        Assert.That(BroadcastStrings.Normalise("pt-BR"), Is.EqualTo("pt"));
        Assert.That(BroadcastStrings.Normalise("PT"), Is.EqualTo("pt"));
        Assert.That(BroadcastStrings.Normalise("ja"), Is.EqualTo("en"), "no words for it, so English");
        Assert.That(BroadcastStrings.Normalise(null), Is.EqualTo("en"));
        Assert.That(BroadcastStrings.TemplatesFor("pt").ViaWikipedia, Is.EqualTo("via Wikipédia"));
        Assert.That(BroadcastStrings.TemplatesFor("ja").ViaWikipedia, Is.EqualTo("via Wikipedia"));

        // Every language has to fill every slot, or a card in that language
        // prints an empty fragment.
        foreach (var lang in BroadcastStrings.Languages) {
            var t = BroadcastStrings.TemplatesFor(lang);
            Assert.That(t.TypeInConstellation, Does.Contain("{0}").And.Contain("{1}"), lang);
            Assert.That(t.MagnitudeLabel, Does.Contain("{0}"), lang);
            Assert.That(t.SizeLabel, Does.Contain("{0}"), lang);
            Assert.That(t.ViaWikipedia, Is.Not.Empty, lang);
            Assert.That(t.UnknownObject, Is.Not.Empty, lang);
        }
    }
}
