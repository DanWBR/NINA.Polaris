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
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The wasm case here is a regression guard, not a style preference. Serving
/// /js/wasm/ with a 7-day max-age shipped, and browsers evicting the 8 MB
/// runtime while keeping the 2 KB manifest left clients validating new files
/// against an old set of hashes. The runtime then refused to boot and the page
/// never finished loading, which read as "the app stopped working" and survived
/// hard refreshes for days.
/// </summary>
[TestFixture]
public class StaticAssetCachePolicyTests {

    [TestCase("/js/wasm/main.js")]
    [TestCase("/js/wasm/_framework/dotnet.boot.js")]
    [TestCase("/js/wasm/_framework/dotnet.native.wasm")]
    [TestCase("/js/wasm/_framework/NINA.Image.Portable.wasm")]
    public void WasmBundleAlwaysRevalidates(string path) {
        Assert.That(StaticAssetCachePolicy.For(path),
            Is.EqualTo(StaticAssetCachePolicy.Revalidate),
            "The wasm bundle is only usable as a consistent set: dotnet.boot.js "
            + "hashes every other file in it. Caching any part of it without "
            + "revalidation lets the parts come from different builds.");
    }

    [TestCase("/index.html")]
    [TestCase("/js/app.js")]
    [TestCase("/css/parts/02-imaging-panels.css")]
    [TestCase("/data/locales/pt-BR.json")]
    public void AppCodeRevalidates(string path) {
        Assert.That(StaticAssetCachePolicy.For(path),
            Is.EqualTo(StaticAssetCachePolicy.Revalidate),
            "An operator who updates the .deb and refreshes must not keep seeing the old UI.");
    }

    [TestCase("/sky/data/surveys/dss/Norder3/Dir0/Npix1.jpg")]
    [TestCase("/js/lib/chart.umd.min.js")]
    [TestCase("/css/lib/some-vendor.css")]
    [TestCase("/catalogs/dso/dso.db")]
    public void BulkImmutableAssetsStayCached(string path) {
        Assert.That(StaticAssetCachePolicy.For(path),
            Is.EqualTo(StaticAssetCachePolicy.SevenDays),
            "These are independent of each other and huge; revalidating each one "
            + "costs hundreds of round trips per page load.");
    }

    [Test]
    public void WasmUnderAVendorPathIsStillNotLongLived() {
        // The old rule matched "/wasm/" anywhere in the path, so a vendored
        // bundle would have inherited the long cache too. Prefix matching keeps
        // the long-lived list to paths that were actually reasoned about.
        Assert.That(StaticAssetCachePolicy.For("/js/some-vendor/wasm/thing.wasm"),
            Is.EqualTo(StaticAssetCachePolicy.Revalidate));
    }
}
