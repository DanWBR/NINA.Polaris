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

using NUnit.Framework;
using NINA.Polaris.Endpoints;

namespace NINA.Polaris.Test;

/// <summary>In the iOS and Android apps the embedded panels (PHD2 GUI,
/// INDI Web Manager) cannot send the session cookie, so the token travels in
/// the path. Without it the INDI Drivers panel was a blank 401 on iOS.</summary>
[TestFixture]
public class PathTokenTests {
    [TestCase("/phd2-gui/t/abc123/index.html", "abc123")]
    [TestCase("/indi-web/t/abc123/", "abc123")]
    [TestCase("/indi-web/t/abc123/static/js/indi.js", "abc123")]
    [TestCase("/indi-web/t/a%2Bb/api/server/status", "a+b")]
    public void TheTokenIsReadFromTheEmbeddedPanelsPath(string path, string token) {
        Assert.That(AuthEndpoints.ExtractPathToken(path), Is.EqualTo(token));
    }

    [TestCase("/indi-web/")]
    [TestCase("/indi-web/static/js/indi.js")]
    [TestCase("/api/system/status")]
    [TestCase("/t/abc123/")]
    public void OtherPathsCarryNoToken(string path) {
        Assert.That(AuthEndpoints.ExtractPathToken(path), Is.Null);
    }
}
