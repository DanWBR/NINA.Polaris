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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test;

/// <summary>
/// SKYTHUMB: pins the premise behind the SKY info card's offline thumbnail —
/// that a COMMON name the Stellarium engine reports ("Lagoon Nebula") resolves,
/// through the bundled catalogue, to a slug that has a JPEG in
/// wwwroot/sky/data/skydata/dso-thumbs/.
///
/// The field bug: the SKY card took only `thumbnailUrl` from /api/sky/image — a
/// Wikipedia/NASA CDN link — making it the ONE thumbnail path in the app that
/// needed internet. On a dark-site SBC with no connection it always showed the
/// icon, while Tonight's Best (bundled cutouts) and the AUTORUN cards
/// (localUrl-first) both worked. Forgotten pair: three paths, only one of them
/// offline-blind.
///
/// The card now tries the bundle first, resolving common names via
/// catalogue search. That only works if BOTH halves hold: the catalogue maps the
/// common name to a catalogue code, AND the bundle actually ships that slug.
/// Nothing else checks the two together, so a catalogue rebuild or a slimmed
/// bundle could silently take the thumbs away again.
/// </summary>
[TestFixture]
public class SkyCardThumbResolutionTests {
    private DsoCatalog _catalog = null!;
    private static string ThumbDir =>
        Path.Combine(RepoRoot(), "src", "NINA.Polaris", "wwwroot",
                     "sky", "data", "skydata", "dso-thumbs");

    /// <summary>Repo root from THIS file's compile-time path, not
    /// AppContext.BaseDirectory — the latter breaks under --artifacts-path.</summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [OneTimeSetUp]
    public void SetUp() {
        _catalog = new DsoCatalog(new TestEnv(), NullLogger<DsoCatalog>.Instance);
        if (!_catalog.IsAvailable)
            Assert.Ignore($"dso.db not present at {_catalog.DbPath}; skipping.");
        if (!Directory.Exists(ThumbDir))
            Assert.Ignore($"dso-thumbs bundle not present at {ThumbDir}; skipping.");
    }

    /// <summary>Mirror of app.js dsoThumbUrl()'s slug rule. Duplicated on purpose:
    /// the JS has no test runner here, and pinning the RULE against the real
    /// bundle is what catches "the bundle no longer has what the card asks for".
    /// Keep in sync with dsoThumbUrl if that regex changes.</summary>
    private static string SlugFor(string name) {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var raw = name.Trim();
        var sh = Regex.Match(raw, @"^sh\s*2\s*[-\s]?\s*0*(\d+)", RegexOptions.IgnoreCase);
        if (!sh.Success) sh = Regex.Match(raw, @"^sharpless\s*[-\s]?\s*0*(\d+)", RegexOptions.IgnoreCase);
        if (sh.Success) return "SH2" + sh.Groups[1].Value;
        var m = Regex.Match(raw, @"^([A-Za-z]+)\s*0*(\d+[A-Za-z]?)");
        return m.Success ? (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant() : "";
    }

    private static bool ThumbExists(string slug) =>
        !string.IsNullOrEmpty(slug) && File.Exists(Path.Combine(ThumbDir, slug + ".jpg"));

    /// <summary>THE bug, as the user hit it: click "Lagoon Nebula" / "Trifid
    /// Nebula" on the SKY map and get no photo. Both are common names with no
    /// digits, so the slug regex alone yields nothing — the card must go through
    /// the catalogue to reach a code whose JPEG is bundled.</summary>
    [Test]
    public async Task CommonNames_ResolveToABundledThumb() {
        foreach (var commonName in new[] {
            "Lagoon Nebula", "Trifid Nebula", "Orion Nebula",
            "Eagle Nebula", "Ring Nebula", "Andromeda Galaxy" }) {

            // The raw name can't slug on its own — that's the whole problem.
            Assert.That(SlugFor(commonName), Is.Empty,
                $"'{commonName}' should not slug directly (no digits); " +
                "if this fails the card's fallback is untested");

            var hits = await _catalog.SearchAsync(commonName, 4);
            Assert.That(hits, Is.Not.Empty, $"catalogue should know '{commonName}'");

            // Same candidate order the card uses: primary name, then aliases.
            var resolved = hits
                .SelectMany(h => new[] { h.Name }.Concat(h.Aliases ?? Array.Empty<string>()))
                .Select(SlugFor)
                .FirstOrDefault(ThumbExists);

            Assert.That(resolved, Is.Not.Null,
                $"'{commonName}' resolved to [{string.Join(", ", hits.Select(h => h.Name))}] " +
                $"but none of those (or their aliases) has a JPEG in the bundle");
        }
    }

    /// <summary>Catalogue names must keep working WITHOUT a search round-trip —
    /// that's the fast path the card tries first, and the one Tonight's Best has
    /// always used.</summary>
    [Test]
    public void CatalogueNames_SlugDirectlyToABundledThumb() {
        foreach (var (name, slug) in new[] {
            ("M 8", "M8"), ("M42", "M42"), ("NGC 6523", "NGC6523"),
            ("IC 1396", "IC1396"), ("Sh2 279", "SH2279") }) {
            Assert.That(SlugFor(name), Is.EqualTo(slug), $"slug rule broke for '{name}'");
            Assert.That(ThumbExists(slug), Is.True, $"bundle is missing {slug}.jpg");
        }
    }

    /// <summary>The two objects from the field report, end to end.</summary>
    [Test]
    public async Task LagoonAndTrifid_LandOnTheExpectedFiles() {
        foreach (var (commonName, expected) in new[] {
            ("Lagoon Nebula", "NGC6523"),   // M 8
            ("Trifid Nebula", "NGC6514") }) {  // M 20
            var hits = await _catalog.SearchAsync(commonName, 4);
            var slugs = hits
                .SelectMany(h => new[] { h.Name }.Concat(h.Aliases ?? Array.Empty<string>()))
                .Select(SlugFor)
                .Where(ThumbExists)
                .ToList();
            Assert.That(slugs, Does.Contain(expected),
                $"'{commonName}' should reach {expected}.jpg; got [{string.Join(", ", slugs)}]");
        }
    }

    private class TestEnv : IWebHostEnvironment {
        public string WebRootPath { get; set; } =
            Path.Combine(RepoRoot(), "src", "NINA.Polaris", "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }
}
