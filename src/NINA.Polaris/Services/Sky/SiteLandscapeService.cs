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

using System.Text.Json;
using SkiaSharp;

namespace NINA.Polaris.Services.Sky;

/// <summary>What is installed as the site landscape.</summary>
public sealed record SiteLandscapeInfo(
    string Name, long Version, int Width, int Height,
    double TopAltitudeDeg, double BottomAltitudeDeg, double LeftAzimuthDeg,
    int Order, bool MaskedFromHorizon, long Bytes, DateTime CreatedUtc);

/// <summary>
/// The SKY tab's own ground: a site panorama the user imports, converted to a
/// HiPS landscape (<see cref="LandscapeHipsBuilder"/>) and kept in the data
/// directory, outside wwwroot, so it survives updates. The SKY engine loads
/// it from <c>/api/sky/landscape/hips/{version}/</c> instead of the bundled
/// Guéreins field. GitHub issue #80.
/// </summary>
public sealed class SiteLandscapeService {
    /// <summary>Largest panorama accepted, in pixels across.</summary>
    public const int MaxWidth = 32768;
    public const int MinWidth = 1024;
    private const string InfoFile = "landscape.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;
    private readonly string _dir;
    private readonly ILogger<SiteLandscapeService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SiteLandscapeInfo? _info;
    private bool _loaded;

    public SiteLandscapeService(ProfileService profiles, ILogger<SiteLandscapeService> logger) {
        _logger = logger;
        _root = Path.Combine(profiles.DataDir, "sky");
        _dir = Path.Combine(_root, "landscape");
    }

    /// <summary>The installed landscape, or null when SKY uses the bundled one.</summary>
    public SiteLandscapeInfo? Info {
        get {
            if (_loaded) return _info;
            try {
                var path = Path.Combine(_dir, InfoFile);
                _info = File.Exists(path) && File.Exists(Path.Combine(_dir, "properties"))
                    ? JsonSerializer.Deserialize<SiteLandscapeInfo>(File.ReadAllText(path), Json)
                    : null;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Could not read the site landscape description");
                _info = null;
            }
            _loaded = true;
            return _info;
        }
    }

    /// <summary>
    /// Convert <paramref name="image"/> (PNG, WebP or JPEG) and install it,
    /// replacing any earlier one. An opaque image needs <paramref name="horizon"/>
    /// to cut its sky out; one with a transparent sky uses its own alpha.
    /// </summary>
    public async Task<SiteLandscapeInfo> ImportAsync(Stream image, LandscapeHipsBuilder.Options options,
            IReadOnlyList<HorizonPoint>? horizon, CancellationToken ct) {
        Validate(options);
        using var ms = new MemoryStream();
        await image.CopyToAsync(ms, ct);
        ms.Position = 0;

        await _gate.WaitAsync(ct);
        string? work = null;
        try {
            using var decoded = SKBitmap.Decode(ms)
                ?? throw new LandscapeImportException("The file is not an image Polaris can read (use PNG, WebP or JPEG).");
            if (decoded.Width < MinWidth || decoded.Width > MaxWidth)
                throw new LandscapeImportException($"The panorama is {decoded.Width} px wide; it must be between {MinWidth} and {MaxWidth} px.");
            if (decoded.Height < 16 || decoded.Height > decoded.Width)
                throw new LandscapeImportException("The panorama must be wider than it is tall, because its width covers 360 degrees of azimuth.");
            using var pano = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            // Drawn rather than copied: a copy of an opaque JPEG comes back as
            // an Opaque bitmap, and the builder samples premultiplied RGBA.
            using (var canvas = new SKCanvas(pano)) {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(decoded, 0, 0);
            }

            bool masked = false;
            if (!LandscapeHipsBuilder.HasTransparency(pano)) {
                if (horizon == null || horizon.Count == 0)
                    throw new LandscapeImportException("The panorama doesn't have a transparent sky. Import it as a PNG with the sky cut out, or import your horizon first so that Polaris can cut the sky along it.");
                LandscapeHipsBuilder.MaskAboveHorizon(pano, options, az => HorizonProfile.AltitudeAt(horizon, az));
                masked = true;
            }

            Directory.CreateDirectory(_root);
            work = Path.Combine(_root, "landscape.tmp-" + Guid.NewGuid().ToString("N")[..8]);
            var started = DateTime.UtcNow;
            int order = await Task.Run(() => LandscapeHipsBuilder.Build(pano, options, work, ct), ct);
            long bytes = new DirectoryInfo(work).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            var info = new SiteLandscapeInfo(
                string.IsNullOrWhiteSpace(options.Title) ? "Site landscape" : options.Title.Trim(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), pano.Width, pano.Height,
                options.TopAltitudeDeg, options.BottomAltitudeDeg, options.LeftAzimuthDeg,
                order, masked, bytes, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(work, InfoFile), JsonSerializer.Serialize(info, Json));

            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            Directory.Move(work, _dir);
            work = null;
            _info = info;
            _loaded = true;
            _logger.LogInformation("Site landscape installed: {Name}, {W}x{H} px, order {Order}, {Kb} kB, {Sec:F1} s{Masked}",
                info.Name, info.Width, info.Height, order, bytes / 1024, (DateTime.UtcNow - started).TotalSeconds,
                masked ? ", sky cut along the horizon" : "");
            return info;
        } finally {
            if (work != null) { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
            _gate.Release();
        }
    }

    /// <summary>Remove the site landscape, so SKY goes back to the bundled one.</summary>
    public async Task RemoveAsync() {
        await _gate.WaitAsync();
        try {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            _info = null;
            _loaded = true;
        } finally {
            _gate.Release();
        }
    }

    /// <summary>The file behind a HiPS request path (<c>properties</c>,
    /// <c>Norder1/Dir0/Npix5.webp</c>...), or null when there isn't one.</summary>
    public string? Resolve(string relativePath) {
        if (Info == null || string.IsNullOrEmpty(relativePath)) return null;
        // The engine asks for description.{lang}.utf8 in the UI language.
        if (relativePath.StartsWith("description.", StringComparison.Ordinal) && relativePath.EndsWith(".utf8", StringComparison.Ordinal))
            relativePath = LandscapeHipsBuilder.DescriptionFile;
        var root = Path.GetFullPath(_dir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.Ordinal)) return null;
        if (Path.GetFileName(full) == InfoFile) return null;
        return File.Exists(full) ? full : null;
    }

    internal static void Validate(LandscapeHipsBuilder.Options o) {
        if (!double.IsFinite(o.TopAltitudeDeg) || !double.IsFinite(o.BottomAltitudeDeg) || !double.IsFinite(o.LeftAzimuthDeg))
            throw new LandscapeImportException("Enter the altitude of the top and bottom edges and the azimuth of the left edge.");
        if (o.TopAltitudeDeg > 90 || o.BottomAltitudeDeg < -90)
            throw new LandscapeImportException("Altitudes run from -90 to 90 degrees.");
        if (o.TopAltitudeDeg - o.BottomAltitudeDeg < 1)
            throw new LandscapeImportException("The top edge must be higher than the bottom edge.");
    }
}

/// <summary>A panorama Polaris can't turn into a landscape; the message is for the user.</summary>
public sealed class LandscapeImportException(string message) : Exception(message);
