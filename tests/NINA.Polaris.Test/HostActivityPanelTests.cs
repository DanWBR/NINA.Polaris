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
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>A client that opens mid-session starts on the tab of what is
/// running (hostActivity.panel).</summary>
[TestFixture]
public class HostActivityPanelTests {
    private static string? Pick(bool video = false, bool mosaic = false, bool plan = false,
            bool autorun = false, bool advanced = false, bool liveStack = false,
            bool liveCapture = false, bool focus = false) =>
        HostActivityService.PickPanel(video, mosaic, plan, autorun, advanced, liveStack, liveCapture, focus);

    [Test]
    public void IdleHostKeepsTheDefaultScreen() {
        Assert.That(Pick(), Is.Null);
    }

    [Test]
    public void EachActivityOpensItsTab() {
        Assert.Multiple(() => {
            Assert.That(Pick(liveStack: true), Is.EqualTo("live"));
            Assert.That(Pick(liveCapture: true), Is.EqualTo("live"));
            Assert.That(Pick(autorun: true), Is.EqualTo("sequence"));
            Assert.That(Pick(advanced: true), Is.EqualTo("seqadv"));
            Assert.That(Pick(video: true), Is.EqualTo("video"));
            Assert.That(Pick(mosaic: true), Is.EqualTo("mosaic"));
            Assert.That(Pick(focus: true), Is.EqualTo("focus"));
        });
    }

    [Test]
    public void TheRunThatOwnsTheSessionWins() {
        Assert.Multiple(() => {
            // Auto-focus between AUTORUN items is part of the AUTORUN run.
            Assert.That(Pick(autorun: true, focus: true), Is.EqualTo("sequence"));
            // A live stack runs under an AUTORUN run's frames.
            Assert.That(Pick(autorun: true, liveStack: true), Is.EqualTo("sequence"));
            // PLAN runs on the advanced engine.
            Assert.That(Pick(plan: true, advanced: true), Is.EqualTo("plan"));
        });
    }

    private static string Here([CallerFilePath] string p = "") => p;

    [Test]
    public void BothUisOpenOnThePanelOnce() {
        var js = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..",
            "src", "NINA.Polaris", "wwwroot", "js", "app.js"));
        Assert.Multiple(() => {
            Assert.That(js, Does.Contain("this._openOnActivePanel(msg.hostActivity.panel || null)"));
            Assert.That(js, Does.Contain("if (this._openedOnActivity) return;"));
        });
    }
}
