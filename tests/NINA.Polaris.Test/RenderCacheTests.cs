using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using NINA.Polaris.Services.Studio;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Part A: the on-disk render cache must run the expensive render exactly
/// once per (file, params) key and serve the physical file thereafter
/// (so ASP.NET can answer conditional GETs with 304). The ETag / 304
/// wire behaviour itself is provided by Results.File and exercised at the
/// framework level; here we pin the render-once + headers invariant.
/// </summary>
[TestFixture]
public class RenderCacheTests {

    private readonly List<string> _keysToClean = new();

    [TearDown]
    public void Cleanup() {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA.Polaris", "files", "render-cache");
        foreach (var key in _keysToClean) {
            foreach (var ext in new[] { "bin" }) {
                var p = Path.Combine(dir, Sha1(key) + "." + ext);
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }
    }

    [Test]
    public void ServeCached_RendersOnce_ThenServesFromDisk() {
        var key = "polaris-rendercache-test-" + Guid.NewGuid().ToString("N");
        _keysToClean.Add(key);

        int renders = 0;
        byte[] Render() { renders++; return new byte[] { 1, 2, 3, 4, 5 }; }

        var ctx1 = new DefaultHttpContext();
        var r1 = RenderCache.ServeCached(ctx1, key, "bin", "application/octet-stream", Render);
        Assert.That(r1, Is.Not.Null);
        Assert.That(renders, Is.EqualTo(1), "first call renders");

        // Second call with the same key must hit the on-disk file, NOT
        // re-render.
        var ctx2 = new DefaultHttpContext();
        var r2 = RenderCache.ServeCached(ctx2, key, "bin", "application/octet-stream", Render);
        Assert.That(r2, Is.Not.Null);
        Assert.That(renders, Is.EqualTo(1),
            "second call must serve the cached file, not re-render");

        // URLs carry a per-session token, so responses are private.
        Assert.That(ctx2.Response.Headers.CacheControl.ToString(), Does.Contain("private"));
    }

    [Test]
    public void KeyForFile_ChangesWhenParamsChange() {
        var tmp = Path.GetTempFileName();
        try {
            var a = RenderCache.KeyForFile(tmp, "fits", 1600, null, null);
            var b = RenderCache.KeyForFile(tmp, "fits", 2400, null, null);
            Assert.That(a, Is.Not.EqualTo(b), "different maxDim -> different key");

            var c = RenderCache.KeyForFile(tmp, "fits", 1600, "ref.fits", null);
            Assert.That(a, Is.Not.EqualTo(c), "different stretchFrom -> different key");
        } finally {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static string Sha1(string s) {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
