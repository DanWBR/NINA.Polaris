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
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>The floating Mount control is one panel shared by SKY, FOCUS,
/// PREVIEW and LIVE. It used to live inside the SKY tab, so it vanished on
/// every other tab; each of the four now has a Mount button that toggles
/// the same panel through toggleMountPanel().</summary>
[TestFixture]
public class MountFloatPanelTests {
    private static readonly string[] Tabs = { "sky", "focus", "preview", "live" };
    private const string Toggle = "@click=\"toggleMountPanel($event)\"";

    private static string Here([CallerFilePath] string p = "") => p;
    private static string Wwwroot() =>
        Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris", "wwwroot");
    private static string Html() => File.ReadAllText(Path.Combine(Wwwroot(), "index.html"));
    private static string Js() => File.ReadAllText(Path.Combine(Wwwroot(), "js", "app.js"));

    // index.html with every comment blanked out (same length, so offsets
    // still line up). The markup is heavily commented and the comments
    // name the very things these tests look for.
    private static string Uncommented(string html) =>
        Regex.Replace(html, "<!--.*?-->", m => new string(' ', m.Length), RegexOptions.Singleline);

    // [start, end) of the <div> that opens at `start`, found by counting
    // nested <div> / </div> pairs.
    private static (int Start, int End) DivAt(string html, int start) {
        var depth = 0;
        foreach (Match m in Regex.Matches(html[start..], @"<div\b|</div>")) {
            depth += m.Value == "</div>" ? -1 : 1;
            if (depth == 0) return (start, start + m.Index + m.Length);
        }
        Assert.Fail($"unclosed <div> at offset {start}");
        return default;
    }

    private static (int Start, int End) Tab(string html, string tab) {
        var at = html.IndexOf($"<div x-show=\"tab === '{tab}'\"", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), $"{tab} tab panel not found");
        return DivAt(html, at);
    }

    /// <summary>What: app.js's <c>mountPanelTabs</c> names exactly SKY,
    /// FOCUS, PREVIEW and LIVE.
    /// Why: that one array decides which tabs can show the panel. Dropping a
    /// tab leaves its Mount button doing nothing visible; adding one shows
    /// the panel on a tab with no button to close it again, short of Esc.</summary>
    [Test]
    public void TheJsListsExactlyTheTabsThatShowThePanel() {
        var m = Regex.Match(Js(), @"mountPanelTabs:\s*\[([^\]]*)\]");
        Assert.That(m.Success, "mountPanelTabs not found in app.js");
        var listed = Regex.Matches(m.Groups[1].Value, "'([a-z]+)'").Select(x => x.Groups[1].Value);
        Assert.That(listed, Is.EquivalentTo(Tabs));
    }

    /// <summary>What: index.html holds exactly one Mount panel, it is not
    /// nested inside any of the four tab panels, and its hide condition
    /// checks both the tab list and a connected mount.
    /// Why: the panel used to live inside the SKY tab, so the tab's own
    /// <c>x-show</c> hid it everywhere else. Moving it back, or copying it
    /// into another tab, would bring that back or leave two panels fighting
    /// over the same position and drag state. Without the tab check the
    /// panel would sit over every other tab too.</summary>
    [Test]
    public void OnePanelSitsOutsideEveryTabAndHidesOnTheOthers() {
        var html = Uncommented(Html());
        var panels = Regex.Matches(html, "<div class=\"mount-float-panel\"");
        Assert.That(panels.Count, Is.EqualTo(1), "exactly one shared panel");
        var at = panels[0].Index;
        Assert.Multiple(() => {
            foreach (var tab in Tabs) {
                var (s, e) = Tab(html, tab);
                Assert.That(at < s || at >= e, $"panel is nested inside the {tab} tab");
            }
            var tag = html.Substring(at, html.IndexOf('>', at) - at);
            Assert.That(tag, Does.Contain("mountPanelTabs.includes(tab)"),
                "panel must hide on tabs that have no Mount button");
            Assert.That(tag, Does.Contain("!mount.connected"));
        });
    }

    /// <summary>What: each of the four tabs has exactly one button that
    /// calls <c>toggleMountPanel($event)</c>, and no other part of the page
    /// has one.
    /// Why: the panel can only be opened from a tab that has a button. A
    /// button lost in a markup edit leaves the panel unreachable on that
    /// tab, and a duplicate or stray one on a tab that never shows the
    /// panel is a button that appears to do nothing.</summary>
    [Test]
    public void EachTabHasOneMountButton() {
        var html = Uncommented(Html());
        Assert.Multiple(() => {
            foreach (var tab in Tabs) {
                var (s, e) = Tab(html, tab);
                Assert.That(Regex.Matches(html[s..e], Regex.Escape(Toggle)).Count, Is.EqualTo(1), tab);
            }
            Assert.That(Regex.Matches(html, Regex.Escape(Toggle)).Count, Is.EqualTo(Tabs.Length),
                "no Mount button outside the four tabs");
        });
    }

    /// <summary>What: <c>toggleMountPanel</c> resets the panel's position on
    /// shift+click and saves the state, and all four Mount buttons also
    /// reset it on right-click.
    /// Why: the panel can be dragged until its header is off-screen, and
    /// then it cannot be grabbed again. The old SKY pill had this rescue; it
    /// now has to work from every tab, since the panel position is shared.
    /// Saving keeps the open/closed choice and position across reloads.</summary>
    [Test]
    public void EveryMountButtonOffersThePositionRescue() {
        var html = Uncommented(Html());
        var js = Js();
        var body = js.Substring(js.IndexOf("toggleMountPanel(ev) {", StringComparison.Ordinal), 400);
        Assert.Multiple(() => {
            // Shift+click and right-click both bring a panel that was dragged
            // off-screen back to its default spot.
            Assert.That(body, Does.Contain("ev.shiftKey"));
            Assert.That(body, Does.Contain("this.resetMountPanelPosition()"));
            Assert.That(body, Does.Contain("this.persistMountPanel()"));
            Assert.That(Regex.Matches(html,
                Regex.Escape(Toggle) + @"\s*@contextmenu\.prevent=""resetMountPanelPosition\(\); mountPanel\.visible = true""").Count,
                Is.EqualTo(Tabs.Length));
        });
    }

    /// <summary>What: every Mount button (SKY, FOCUS, PREVIEW and LIVE)
    /// shows the "Connect a mount in RIGS first" tooltip while greyed out,
    /// every catalogue has the key, and every translation keeps the word
    /// RIGS.
    /// Why: a greyed-out Mount button should say where the mount is
    /// connected, and say it the same way on every tab, so one edited button
    /// drifting back to other wording is caught. CONTRIBUTING requires each
    /// new string translated in the same change; a missing key shows English
    /// to every non-English user. RIGS is the tab name, so a translation that
    /// drops it points the user at nothing.</summary>
    [Test]
    public void TheRigsHintIsTranslated() {
        const string key = "Connect a mount in RIGS first";
        var html = Uncommented(Html());
        var buttons = Regex.Matches(html, Regex.Escape(Toggle));
        Assert.That(buttons.Count, Is.EqualTo(Tabs.Length));
        var dir = Path.Combine(Wwwroot(), "data", "locales");
        Assert.Multiple(() => {
            foreach (Match b in buttons) {
                var end = html.IndexOf("</button>", b.Index, StringComparison.Ordinal);
                Assert.That(html[b.Index..end], Does.Contain($"!mount.connected ? '{key}'"),
                    $"Mount button at offset {b.Index} does not use the RIGS tooltip");
            }
            foreach (var lang in new[] { "_source", "pt-BR", "es", "fr", "de", "ru" }) {
                var cat = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(Path.Combine(dir, lang + ".json")))!;
                Assert.That(cat.ContainsKey(key), $"{lang} has no entry");
                if (lang == "_source") continue;
                // RIGS is the tab's name and stays in English, as in the
                // existing "Connect a camera in RIGS first" translations.
                Assert.That(cat.GetValueOrDefault(key), Does.Contain("RIGS"), lang);
            }
        });
    }

    /// <summary>What: Esc closes the Mount panel only on a tab that shows it.
    /// Why: on the other tabs the panel is hidden, so an Esc there (closing
    /// a dialog, say) would close it without the user seeing it, and it
    /// would be gone on the way back to SKY.</summary>
    [Test]
    public void EscClosesThePanelOnlyWhereItShows() {
        var js = Js();
        var at = js.IndexOf("if (this.mountPanel?.visible", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), "Esc handler for the Mount panel not found");
        var cond = js.Substring(at, js.IndexOf('{', at) - at);
        Assert.That(cond, Does.Contain("this.mountPanelTabs.includes(this.tab)"));
    }

    /// <summary>What: the stylesheet does not style the old SKY Mount pill.
    /// Why: the pill was replaced by the Mount buttons; its rules were dead
    /// and pushed the Camera pill up to leave room for it.</summary>
    [Test]
    public void TheOldMountPillIsGone() {
        var css = File.ReadAllText(Path.Combine(Wwwroot(), "css", "parts", "03-equipment-mount.css"));
        Assert.That(Uncommented(Html()), Does.Not.Contain("mount-float-reopen"));
        Assert.That(css, Does.Not.Contain("mount-float-reopen"));
    }
}
