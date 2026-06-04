using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Cache-management surface for the Settings page. Reports the size of the
/// on-disk preview caches (render cache + thumbnail cache) and lets the
/// operator purge them. Purging is always safe -- every entry is
/// regenerated on demand the next time a file is viewed.
/// </summary>
public static class CacheEndpoints {
    public static void MapCacheEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/cache");

        g.MapGet("/stats", () => {
            var render = DirStats(RenderCache.DirectoryPath);
            var thumbs = DirStats(ThumbsDir);
            return Results.Ok(new {
                render = new { files = render.files, bytes = render.bytes },
                thumbs = new { files = thumbs.files, bytes = thumbs.bytes },
                totalFiles = render.files + thumbs.files,
                totalBytes = render.bytes + thumbs.bytes
            });
        });

        g.MapPost("/clear", () => {
            var render = ClearDir(RenderCache.DirectoryPath);
            var thumbs = ClearDir(ThumbsDir);
            return Results.Ok(new {
                cleared = render + thumbs,
                renderCleared = render,
                thumbsCleared = thumbs
            });
        });
    }

    // Thumbnail cache dir, mirrors the path used in FilesEndpoints./thumb.
    private static string ThumbsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA.Polaris", "files", "thumbs");

    private static (int files, long bytes) DirStats(string dir) {
        try {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return (0, 0);
            int files = 0;
            long bytes = 0;
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories)) {
                files++;
                bytes += f.Length;
            }
            return (files, bytes);
        } catch {
            return (0, 0);
        }
    }

    private static int ClearDir(string dir) {
        int n = 0;
        try {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return 0;
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories)) {
                try { f.Delete(); n++; }
                catch { /* a file held open by an in-flight render: skip */ }
            }
        } catch { /* dir vanished mid-scan: best-effort */ }
        return n;
    }
}
