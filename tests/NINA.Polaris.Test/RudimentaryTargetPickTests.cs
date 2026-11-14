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

using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>Picking a catalog result in the Rudimentary polar alignment tab
/// fills the RA and Dec fields (#73). The search endpoint returns `ra` and
/// `dec`; the picker read `raHours` and `decDeg`, which the endpoint does
/// not send, so both fields stayed empty.</summary>
[TestFixture]
public class RudimentaryTargetPickTests {
    private static string Here([CallerFilePath] string p = "") => p;
    private static string Src(params string[] parts) =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris", Path.Combine(parts)));

    [Test]
    public void TheSearchEndpointSendsRaAndDec() {
        var sky = Src("Endpoints", "SkyEndpoints.cs");
        var search = sky.Substring(sky.IndexOf("\"/catalog/search\"", System.StringComparison.Ordinal), 600);
        Assert.That(search, Does.Contain("ra = o.Ra"));
        Assert.That(search, Does.Contain("dec = o.Dec"));
    }

    [Test]
    public void ThePickerReadsRaAndDec() {
        var js = Src("wwwroot", "js", "app.js");
        var body = js.Substring(js.IndexOf("rudimentaryPickResult(r) {", System.StringComparison.Ordinal), 500);
        Assert.That(body, Does.Contain("targetRaHours = r.ra ??"));
        Assert.That(body, Does.Contain("targetDecDeg = r.dec ??"));
    }
}
