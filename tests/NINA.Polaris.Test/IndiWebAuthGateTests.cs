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

/// <summary>With the login turned off no session is authenticated, so the
/// INDI Web Manager panel must not wait for one (#76: a blank panel).</summary>
[TestFixture]
public class IndiWebAuthGateTests {
    private static string Here([CallerFilePath] string p = "") => p;

    [Test]
    public void ThePanelLoadsWithTheLoginTurnedOff() {
        var js = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!,
            "..", "..", "src", "NINA.Polaris", "wwwroot", "js", "app.js"));
        var at = js.IndexOf("async indiWebEnsureIframe(force) {", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        var gate = js.Substring(at, js.IndexOf("this.indiWeb.iframeSrc = 'about:blank'", at, StringComparison.Ordinal) - at);
        Assert.That(gate, Does.Contain("this.auth.authenticated || !this.auth.enabled"));
    }
}
