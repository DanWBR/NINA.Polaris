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
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>The SKY object card: its mount and sequence buttons act on the
/// card's own object, not on the map centre, and the card refreshes its
/// time-dependent rows while it stays open.</summary>
[TestFixture]
public class SkyInfoCardTests {
    private static string Here([CallerFilePath] string p = "") => p;
    private static string AppJs() =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris", "wwwroot", "js", "app.js"));

    private static string Body(string js, string signature, int length = 400) {
        var at = js.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), signature);
        return js.Substring(at, Math.Min(length, js.Length - at));
    }

    [Test]
    public void SlewButtons_PassTheCardObject() {
        var js = AppJs();
        Assert.That(Body(js, "async skyInfoSlewCenter() {"), Does.Contain("this.slewAndCenter(t)"));
        Assert.That(Body(js, "async skyInfoSlewOnly() {"), Does.Contain("this.slewToCurrent(t)"));
        Assert.That(Body(js, "async skyInfoAddToSequence() {"), Does.Contain("this.skyTarget = t"));
    }

    [Test]
    public void SlewHandlers_PreferTheGivenTarget() {
        var js = AppJs();
        Assert.That(Body(js, "async slewAndCenter(targetOverride) {", 600), Does.Contain("? targetOverride : await this._resolveSkyTarget()"));
        Assert.That(Body(js, "async slewToCurrent(targetOverride) {"), Does.Contain("? targetOverride : await this._resolveSkyTarget()"));
    }

    [Test]
    public void CardTarget_IsTheCardCoordinates() {
        var body = Body(AppJs(), "async _skyInfoTarget() {", 900);
        Assert.That(body, Does.Contain("ra: this.skyInfo.raDeg / 15, dec: this.skyInfo.decDeg"));
        Assert.That(body, Does.Contain("_skyInfoReadPosition()"));
    }

    [Test]
    public void OpeningTheCard_StartsTheRefresh() {
        var js = AppJs();
        Assert.That(Body(js, "async _populateSkyInfo(obj) {", 2600), Does.Contain("this._skyInfoStartRefresh();"));
        Assert.That(Body(js, "_skyInfoStartRefresh() {"), Does.Contain("setInterval(() => this._skyInfoTick(), 60000)"));
    }
}
