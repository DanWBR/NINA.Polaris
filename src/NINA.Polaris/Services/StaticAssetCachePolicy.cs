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

namespace NINA.Polaris.Services;

/// <summary>
/// Which Cache-Control a static asset gets. Its own type, rather than a lambda
/// inside Program, because the wrong answer here is not a performance
/// regression: it is a client that cannot load the app at all, and the last
/// time it was wrong it took days of field reports to find.
/// </summary>
public static class StaticAssetCachePolicy {
    /// <summary>Big, essentially immutable third-party payloads. Revalidating
    /// these means hundreds of conditional requests per page load (HiPS tiles
    /// especially) for content that changes on an upstream bump.</summary>
    private static readonly string[] LongLived = {
        "/sky/data/", "/js/lib/", "/css/lib/", "/catalogs/"
    };

    public const string Revalidate = "no-cache, must-revalidate";
    public const string SevenDays  = "public, max-age=604800";

    /// <summary>
    /// The header value for a request path.
    ///
    /// Note what is NOT long-lived: /js/wasm/. Those files are not independent
    /// assets. dotnet.boot.js carries a SHA-256 for every other file in the
    /// bundle and the runtime rejects any file whose hash disagrees, so the
    /// bundle is only usable as a consistent set. Browsers evict cache entries
    /// individually and by size, so a long max-age lets an 8 MB
    /// dotnet.native.wasm be dropped and re-fetched from a newer build while
    /// the 2 KB manifest is still served from the older one. The integrity
    /// check then fails on exactly the files a release changed, the runtime
    /// never boots, and the page never finishes loading.
    /// </summary>
    public static string For(string path) {
        foreach (var prefix in LongLived) {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return SevenDays;
        }
        return Revalidate;
    }
}
