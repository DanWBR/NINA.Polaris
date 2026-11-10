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

using NINA.Polaris.Services;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using S = NINA.Polaris.Services.MountSiteSyncService;

namespace NINA.Polaris.Test;

[TestFixture]
public class MountSiteSyncTests {
    private static string Here([CallerFilePath] string p = "") => p;

    [Test]
    public void ANightRunsFromNoonToNoon() {
        Assert.Multiple(() => {
            Assert.That(S.NightKey(new DateTime(2026, 11, 9, 19, 0, 0)), Is.EqualTo("2026-11-09"));
            Assert.That(S.NightKey(new DateTime(2026, 11, 10, 2, 30, 0)), Is.EqualTo("2026-11-09"),
                "after midnight still belongs to the evening before");
            Assert.That(S.NightKey(new DateTime(2026, 11, 10, 11, 59, 0)), Is.EqualTo("2026-11-09"));
            Assert.That(S.NightKey(new DateTime(2026, 11, 10, 12, 0, 0)), Is.EqualTo("2026-11-10"));
        });
    }

    [Test]
    public void EachChoiceSendsWhatItSays() {
        Assert.Multiple(() => {
            Assert.That(S.SendsTime("both") && S.SendsLocation("both"));
            Assert.That(S.SendsTime("time") && !S.SendsLocation("time"));
            Assert.That(!S.SendsTime("location") && S.SendsLocation("location"));
            Assert.That(!S.SendsTime("none") && !S.SendsLocation("none"));
        });
    }

    [Test]
    public void ThePromptIsAnsweredFromAnyClientThroughTheStatusStream() {
        var src = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris", "wwwroot");
        var js = File.ReadAllText(Path.Combine(src, "js", "app.js"));
        var html = File.ReadAllText(Path.Combine(src, "index.html"));
        Assert.Multiple(() => {
            Assert.That(js, Does.Contain("this.mountSiteSync = msg.mountSiteSync"));
            Assert.That(js, Does.Contain("'/api/telescope/site-sync'"));
            foreach (var c in S.Choices)
                Assert.That(html, Does.Contain($"answerMountSiteSync('{c}')"), c);
        });
    }
}
