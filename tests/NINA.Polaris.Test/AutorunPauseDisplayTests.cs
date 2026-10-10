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

using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// What AUTORUN shows between pressing Pause and the pause taking effect.
///
/// <para>The engine pauses between frames: the exposure already open runs to
/// the end and is saved. The panel dropped that exposure's progress the moment
/// the button was pressed, so it looked discarded, and on Resume the same
/// countdown came back, which looked like the exposure itself had been paused
/// and continued (issue #32).</para>
/// </summary>
[TestFixture]
public class AutorunPauseDisplayTests {

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Read(params string[] parts) {
        var all = new List<string> { Path.GetDirectoryName(Here())!, "..", ".." };
        all.AddRange(parts);
        var path = Path.GetFullPath(Path.Combine(all.ToArray()));
        Assert.That(File.Exists(path), $"missing {path}");
        return File.ReadAllText(path);
    }

    private static string App() => Read("src", "NINA.Polaris", "wwwroot", "js", "app.js");
    private static string Html() => Read("src", "NINA.Polaris", "wwwroot", "index.html");

    [Test]
    public void PausingMeansPausedWithAnAutorunExposureStillOpen() {
        var js = App();
        var at = js.IndexOf("autorunPausing(state = this.seqState) {", System.StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), "autorunPausing is gone");
        var body = js.Substring(at, 300);
        Assert.Multiple(() => {
            Assert.That(body, Does.Contain("state !== 'paused'"));
            Assert.That(body, Does.Contain("sc.active"));
            Assert.That(body, Does.Contain("'autorun'"));
        });
    }

    /// <summary>The frame clock, the exposure ring, the card countdown and the
    /// big countdown all keep going while pausing.</summary>
    [Test]
    public void TheExposureKeepsCountingWhilePausing() {
        var js = App();
        Assert.Multiple(() => {
            Assert.That(js, Does.Contain("seq.state !== 'running' && !this.autorunPausing(seq.state)"),
                "the frame start is cleared on the press again");
            Assert.That(Regex.Matches(js,
                    @"\(this\.seqState !== 'running' && !this\.autorunPausing\(\)\) \|\| !this\.autorunFrameStart").Count,
                Is.EqualTo(2), "exposure ring and card countdown");
            Assert.That(js, Does.Contain("this.seqState === 'paused' && !this.autorunPausing()) return 'paused';"));
        });
    }

    [Test]
    public void ThePanelSaysItIsPausing() {
        var html = Html();
        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("autorunPausing() ? 'PAUSING' : seqState.toUpperCase()"));
            Assert.That(html, Does.Contain(@"x-show=""autorunPausing()"">Pausing once this exposure is saved<"));
        });
        foreach (var lang in new[] { "_source", "de", "es", "fr", "pt-BR" })
            Assert.That(Read("src", "NINA.Polaris", "wwwroot", "data", "locales", lang + ".json"),
                Does.Contain("\"Pausing once this exposure is saved\""), lang);
    }
}
