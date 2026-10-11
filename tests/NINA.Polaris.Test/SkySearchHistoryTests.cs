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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The SKY search box remembers what was searched and offers it back when the
/// box takes focus.
///
/// <para>Two things are worth pinning rather than left to a future tidy-up.
/// Only a search that FOUND something is kept, because a list of your own
/// typos is worse than no list. And the list renders in the block under the
/// toolbar rather than floating beneath the input: .sky-toolbar is a
/// horizontal scroll container, so an absolutely positioned dropdown inside it
/// is clipped at its edge.</para>
/// </summary>
[TestFixture]
public class SkySearchHistoryTests {

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Read(params string[] parts) {
        var here = Path.GetDirectoryName(Here())!;
        var all = new List<string> { here, "..", ".." };
        all.AddRange(parts);
        var path = Path.GetFullPath(Path.Combine(all.ToArray()));
        Assert.That(File.Exists(path), $"nao achei {path}");
        return File.ReadAllText(path);
    }

    private static string App() => Read("src", "NINA.Polaris", "wwwroot", "js", "app.js");
    private static string Html() => Read("src", "NINA.Polaris", "wwwroot", "index.html");

    [Test]
    public void OnlyASearchThatFoundSomethingIsRemembered() {
        var js = App();
        var search = js.IndexOf("async searchSky() {", StringComparison.Ordinal);
        Assert.That(search, Is.GreaterThanOrEqualTo(0));

        var noneFound = js.IndexOf("No objects found for", search, StringComparison.Ordinal);
        var remember = js.IndexOf("this._skyRecentRemember(q);", noneFound, StringComparison.Ordinal);

        Assert.Multiple(() => {
            Assert.That(remember, Is.GreaterThan(noneFound),
                "the empty-result branch returns before anything is recorded");
            Assert.That(js, Does.Contain("return;   // nothing found, so nothing worth remembering"));
        });
    }

    /// <summary>A coordinate pair resolves without touching the catalog, and it
    /// is just as worth offering back as a name.</summary>
    [Test]
    public void ACoordinatePairIsRememberedToo() {
        var js = App();
        var coords = js.IndexOf("const coords = this.parseRaDecLine(q);", StringComparison.Ordinal);
        Assert.That(coords, Is.GreaterThanOrEqualTo(0));
        Assert.That(js.IndexOf("this._skyRecentRemember(q);", coords, StringComparison.Ordinal),
            Is.GreaterThan(coords));
    }

    [Test]
    public void TheListIsCappedAndDeduplicatedCaseInsensitively() {
        var js = App();
        Assert.Multiple(() => {
            Assert.That(js, Does.Contain("s.toLowerCase() !== v.toLowerCase()"),
                "searching the same object again must move it up, not pile up");
            Assert.That(js, Does.Contain("[v, ...rest].slice(0, 8)"));
        });
    }

    /// <summary>Every localStorage touch is wrapped: a private window throws on
    /// access, and losing the list is not worth taking the tab down for.</summary>
    [Test]
    public void EveryStorageAccessIsGuarded() {
        var js = App();
        foreach (var call in new[] {
            "localStorage.getItem('polaris.sky.recent')",
            "localStorage.setItem('polaris.sky.recent'",
            "localStorage.removeItem('polaris.sky.recent')" }) {
            var at = js.IndexOf(call, StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), call + " is gone");
            var before = js.Substring(Math.Max(0, at - 200), Math.Min(200, at));
            Assert.That(before, Does.Contain("try"), call + " is not inside a try");
        }
    }

    [Test]
    public void TheBoxOpensTheListOnFocusAndClosesItOnBlur() {
        var html = Html();
        Assert.Multiple(() => {
            Assert.That(html, Does.Contain(@"@focus=""skyRecentOpen = true"""));
            Assert.That(html, Does.Contain(@"@blur=""skyRecentBlur()"""));
            Assert.That(App(), Does.Contain("setTimeout(() => { this.skyRecentOpen = false; }, 180);"),
                "closing on blur has to outlast the click that caused it");
        });
    }

    /// <summary>Clicking a row must not blur the input first, or the blur
    /// handler closes the list before the click lands on it.</summary>
    [Test]
    public void TheRowsSuppressTheBlurBeforeTheClick() {
        var html = Html();
        Assert.That(Regex.Matches(html, @"@mousedown\.prevent @click=""skyRecent").Count,
            Is.EqualTo(2), "the rows and the Clear button both need it");
    }

    /// <summary>Results and history never share the screen, and the history
    /// only shows while the box is actually open.</summary>
    [Test]
    public void TheHistoryYieldsToTheResults() {
        Assert.That(Html(), Does.Contain(
            @"x-show=""skySearchOpen && skyRecentOpen && !skyShowResults && skyRecentShown().length"""));
    }

    [Test]
    public void TheNewLabelIsInEveryCatalogue() {
        foreach (var lang in new[] { "_source", "de", "es", "fr", "pt-BR" }) {
            var json = Read("src", "NINA.Polaris", "wwwroot", "data", "locales", lang + ".json");
            using var doc = JsonDocument.Parse(json);
            Assert.That(doc.RootElement.TryGetProperty("Recent searches", out var v), Is.True,
                lang + " has no entry for the new label");
            if (lang == "_source") continue;
            var text = v.GetString() ?? "";
            Assert.That(text, Is.Not.Empty, lang + " left it untranslated");
            Assert.That(text, Does.Not.Contain("—").And.Not.Contain("–"),
                lang + " uses a dash CONTRIBUTING forbids");
        }
    }
}
