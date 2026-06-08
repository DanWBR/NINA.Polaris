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

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// On-disk render cache for expensive image previews (FITS → stretched
/// JPEG, TIFF → PNG, ...). The Pi otherwise re-runs the whole
/// read → debayer → autostretch → encode pipeline every time a file is
/// opened, even though the rendered bytes are identical.
///
/// <para>The win is twofold:</para>
/// <list type="bullet">
/// <item>The render runs once per (file, params) tuple, then is read
///   straight from disk.</item>
/// <item>Serving the physical file via <see cref="Results.File(string,
///   string, string, DateTimeOffset?, Microsoft.Net.Http.Headers.EntityTagHeaderValue,
///   bool)"/> makes ASP.NET set <c>Last-Modified</c> + <c>ETag</c> and
///   answer conditional GETs (<c>If-None-Match</c> / <c>If-Modified-Since</c>)
///   with <c>304 Not Modified</c> automatically — so an unchanged file is
///   never re-transferred either.</item>
/// </list>
///
/// The cache key encodes everything that affects the rendered bytes
/// (source path + mtime + size + render params); a source overwrite
/// changes the mtime/size and therefore the key, so a stale render is
/// never served.
/// </summary>
public static class RenderCache {
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA.Polaris", "files", "render-cache");

    /// <summary>On-disk location of the render cache, so the cache-management
    /// endpoints can report size + purge it.</summary>
    public static string DirectoryPath => CacheDir;

    /// <summary>Soft cap on the cache directory. When exceeded the
    /// opportunistic prune deletes least-recently-accessed files down
    /// to ~80% of the cap.</summary>
    private const long MaxCacheBytes = 512L * 1024 * 1024;

    // Prune is checked roughly 1 call in N to keep the steady-state cost
    // near zero (a full DirectoryInfo enumeration per request would be
    // wasteful on a Pi serving a 200-thumbnail grid).
    private const int PruneEveryNCalls = 64;
    private static int _callCounter;

    /// <summary>
    /// Serve <paramref name="cacheKey"/>'s render from disk, producing it
    /// via <paramref name="render"/> on a cache miss. Sets
    /// <c>Cache-Control: private</c> (URLs carry a per-session
    /// <c>?token=</c>, so the response must not be stored by shared
    /// proxies). Returns a physical-file result so ASP.NET handles ETag /
    /// 304 revalidation for free.
    /// </summary>
    public static IResult ServeCached(HttpContext ctx, string cacheKey, string ext,
                                      string mime, Func<byte[]> render) {
        Directory.CreateDirectory(CacheDir);
        var cachePath = Path.Combine(CacheDir, Sha1(cacheKey) + "." + ext);

        ctx.Response.Headers.CacheControl = "private";

        if (File.Exists(cachePath)) {
            // Bump last-access so the LRU prune keeps hot files around.
            try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); } catch { /* best-effort */ }
            return Results.File(cachePath, mime, enableRangeProcessing: true);
        }

        var bytes = render();

        try {
            // Atomic publish: write to a unique temp file then move into
            // place so a concurrent reader never sees a half-written file.
            var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, cachePath, overwrite: true);
        } catch {
            // Disk full / permission issue: still serve the freshly
            // rendered bytes so the user sees the image, just without
            // the cache benefit this time.
            return Results.File(bytes, mime);
        }

        MaybePrune();
        return Results.File(cachePath, mime, enableRangeProcessing: true);
    }

    /// <summary>Build a cache key from a source file plus render params.
    /// Includes mtime + size so an overwrite of the source invalidates
    /// the entry without a manual purge.</summary>
    public static string KeyForFile(string sourcePath, params object?[] renderParams) {
        var sb = new StringBuilder(sourcePath);
        try {
            var fi = new FileInfo(sourcePath);
            sb.Append('|').Append(fi.LastWriteTimeUtc.Ticks)
              .Append('|').Append(fi.Length);
        } catch {
            // If we can't stat the file the render itself will fail
            // anyway; fall back to the path alone.
        }
        foreach (var p in renderParams) sb.Append('|').Append(p ?? "");
        return sb.ToString();
    }

    private static void MaybePrune() {
        if (Interlocked.Increment(ref _callCounter) % PruneEveryNCalls != 0) return;
        try {
            var dir = new DirectoryInfo(CacheDir);
            if (!dir.Exists) return;
            var files = dir.GetFiles("*.*");
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= MaxCacheBytes) return;

            long target = MaxCacheBytes * 8 / 10;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc)) {
                if (total <= target) break;
                try { var len = f.Length; f.Delete(); total -= len; }
                catch { /* skip files held open by a concurrent read */ }
            }
        } catch { /* prune is strictly best-effort */ }
    }

    private static string Sha1(string s) {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}