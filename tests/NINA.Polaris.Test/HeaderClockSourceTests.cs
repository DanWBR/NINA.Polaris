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
using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Whose clock the header shows.
///
/// <para>Every astronomical decision Polaris makes runs on the HOST clock:
/// meridian, flip point, altitude, twilight. The header showed the browsing
/// device's clock, so on a rig whose system time was wrong the operator saw a
/// time that matched their own tablet, believed time was fine, and got a
/// meridian hours out of place with nothing on screen connecting the two
/// (issue #33). The skew chip existed in the bottom bar and was easy to miss
/// next to a big confident clock saying the opposite.</para>
/// </summary>
[TestFixture]
public class HeaderClockSourceTests {

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

    [Test]
    public void TheHeaderClockRunsOnTheHostTime() {
        var js = App();
        Assert.Multiple(() => {
            Assert.That(js, Does.Contain(
                "this.currentTime = new Date(Date.now() + (this.clockSync.skewSeconds | 0) * 1000)"),
                "the header clock is back to showing this device's own time");
            Assert.That(js, Does.Not.Contain("this.currentTime = new Date().toLocaleTimeString('en-GB');"));
        });
    }

    /// <summary>Zero skew until the first status frame, which makes a
    /// still-connecting page read exactly as it did before rather than
    /// blank or wrong.</summary>
    [Test]
    public void BeforeTheFirstStatusFrameTheOffsetIsZero() {
        var js = App();
        var at = js.IndexOf("clockSync: {", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        var block = js.Substring(at, 400);
        Assert.That(block, Does.Match(@"skewSeconds:\s*0"));
    }

    /// <summary>A disagreement has to be visible on the clock itself, not only
    /// on a chip at the other end of the screen.</summary>
    [Test]
    public void TheClockTintsWhenItDisagreesWithThisDevice() {
        var html = Read("src", "NINA.Polaris", "wwwroot", "index.html");
        Assert.Multiple(() => {
            Assert.That(html, Does.Contain(@":class=""clockSkewClass()"" :title=""clockChipTitle()"""));
            Assert.That(html, Does.Not.Contain(@"x-text=""currentTime""
                  title=""Current time"""));
        });

        var css = Read("src", "NINA.Polaris", "wwwroot", "css", "parts", "01-base-statusbar.css");
        Assert.Multiple(() => {
            // Two classes, so the tint beats .status-clock's own colour
            // whatever order the stylesheets load in.
            Assert.That(css, Does.Contain(".status-clock.host-amber"));
            Assert.That(css, Does.Contain(".status-clock.host-red"));
        });
    }

    [Test]
    public void TheTooltipSaysWhoseClockItIs() {
        var js = App();
        Assert.Multiple(() => {
            Assert.That(js, Does.Contain("clockChipTitle()"));
            Assert.That(js, Does.Contain("this.$t('Host clock')"));
            Assert.That(js, Does.Contain("this.$t('Astronomical calculations use this clock.')"));
        });
    }

    [Test]
    public void TheNewLabelsAreInEveryCatalogue() {
        foreach (var lang in new[] { "_source", "de", "es", "fr", "pt-BR" }) {
            var json = Read("src", "NINA.Polaris", "wwwroot", "data", "locales", lang + ".json");
            using var doc = JsonDocument.Parse(json);
            foreach (var key in new[] {
                "Host clock", "of this device",
                "Astronomical calculations use this clock." }) {
                Assert.That(doc.RootElement.TryGetProperty(key, out var v), Is.True,
                    $"{lang} has no entry for \"{key}\"");
                if (lang == "_source") continue;
                var text = v.GetString() ?? "";
                Assert.That(text, Is.Not.Empty, $"{lang} left \"{key}\" untranslated");
                Assert.That(text, Does.Not.Contain("—").And.Not.Contain("–"),
                    $"{lang} uses a dash CONTRIBUTING forbids in \"{key}\"");
            }
        }
    }
}
