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

using System.Globalization;
using System.Text;
using SkiaSharp;

namespace NINA.Polaris.Services.Sky;

/// <summary>
/// Turns an equirectangular site panorama into the HiPS landscape the SKY
/// engine (stellarium-web-engine) draws under the sky: 512 px webp tiles for
/// HEALPix orders 0 to N, an Allsky.webp and a <c>properties</c> file.
///
/// <para>The panorama spans 360 degrees of azimuth across its width, with
/// <see cref="Options.LeftAzimuthDeg"/> at the left edge and azimuth growing
/// to the right (north, east, south, west), and runs from
/// <see cref="Options.TopAltitudeDeg"/> on the top row to
/// <see cref="Options.BottomAltitudeDeg"/> on the bottom row. Above the top
/// edge the tiles are transparent; below the bottom edge they take the mean
/// colour of the panorama's lowest rows.</para>
///
/// <para>Orientation follows the engine's own code, not the HiPS papers.
/// hips.c paints a tile through <c>uv_map_init_healpix</c> with the texture
/// coordinates swapped, so a tile's image row runs along HEALPix x and its
/// column along HEALPix y; and landscape.c renders it through a matrix that
/// negates y, so the azimuth is minus the HEALPix longitude.</para>
/// </summary>
public static class LandscapeHipsBuilder {
    public const int TileSize = 512;
    public const int AllskyTile = 64;
    /// <summary>Highest HEALPix order built (order 2 is about 0.03 deg per pixel).</summary>
    public const int MaxOrder = 2;
    /// <summary>Equator circumference in pixels of an order-0 HiPS with 512 px
    /// tiles: 4 faces around, a pixel of about 58.6/nside degrees.</summary>
    private const double Order0CircumferencePx = 3144;

    public sealed record Options(double TopAltitudeDeg, double BottomAltitudeDeg, double LeftAzimuthDeg,
        string Title, int? Order = null, int WebpQuality = 85);

    /// <summary>The HEALPix order whose resolution matches a panorama of this width.</summary>
    public static int OrderFor(int panoramaWidth) {
        double ratio = panoramaWidth / Order0CircumferencePx;
        int order = ratio <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(ratio) - 0.05);
        return Math.Clamp(order, 0, MaxOrder);
    }

    /// <summary>Write the HiPS landscape for <paramref name="panorama"/> into
    /// <paramref name="dir"/> (created, must be empty). Returns the order built.</summary>
    public static int Build(SKBitmap panorama, Options o, string dir, CancellationToken ct = default) {
        if (o.TopAltitudeDeg <= o.BottomAltitudeDeg)
            throw new ArgumentException("The top edge must be higher than the bottom edge.");
        int order = o.Order ?? OrderFor(panorama.Width);
        Directory.CreateDirectory(dir);

        var ground = GroundColour(panorama);
        SKBitmap? allsky = null;
        try {
            for (int ord = order; ord >= 0; ord--) {
                // Sample each order from a copy at about its own resolution,
                // so the coarse orders don't alias.
                int nside = TileSize << ord;
                int wantWidth = (int)Math.Ceiling(Order0CircumferencePx * (1 << ord));
                using var src = panorama.Width > wantWidth * 1.2 ? Resize(panorama, wantWidth) : null;
                var source = Pixels.From(src ?? panorama);
                string orderDir = Path.Combine(dir, "Norder" + ord, "Dir0");
                Directory.CreateDirectory(orderDir);
                int tiles = 12 << (2 * ord);
                if (ord == 0) allsky = new SKBitmap(new SKImageInfo(3 * AllskyTile, 4 * AllskyTile, SKColorType.Rgba8888, SKAlphaType.Premul));
                var allskyRef = allsky;
                Parallel.For(0, tiles, new ParallelOptions { CancellationToken = ct }, pix => {
                    using var tile = RenderTile(source, o, ground, nside, pix);
                    using (var data = tile.Encode(SKEncodedImageFormat.Webp, o.WebpQuality))
                    using (var fs = File.Create(Path.Combine(orderDir, $"Npix{pix}.webp")))
                        data.SaveTo(fs);
                    if (allskyRef != null) {
                        using var small = Resize(tile, AllskyTile);
                        lock (allskyRef) {
                            using var canvas = new SKCanvas(allskyRef);
                            canvas.DrawBitmap(small, (pix % 3) * AllskyTile, (pix / 3) * AllskyTile);
                        }
                    }
                });
            }
            if (allsky != null) {
                using var data = allsky.Encode(SKEncodedImageFormat.Webp, o.WebpQuality);
                using var fs = File.Create(Path.Combine(dir, "Norder0", "Allsky.webp"));
                data.SaveTo(fs);
            }
        } finally {
            allsky?.Dispose();
        }
        File.WriteAllText(Path.Combine(dir, "properties"), Properties(o.Title, order), new UTF8Encoding(false));
        // landscape.c asks for description.{lang}.utf8 next to the properties.
        // A 404 there goes through an emscripten error path that throws
        // (withStackSave is not defined) and the tiles are never requested.
        File.WriteAllText(Path.Combine(dir, DescriptionFile), Title(o.Title) + "\n", new UTF8Encoding(false));
        return order;
    }

    /// <summary>The description file the engine fetches; served for every language.</summary>
    public const string DescriptionFile = "description.en.utf8";

    private static string Title(string title) =>
        string.IsNullOrWhiteSpace(title) ? "Site landscape" : title.Replace('\n', ' ').Replace('\r', ' ').Trim();

    internal static string Properties(string title, int order) {
        var t = Title(title);
        return string.Join("\n",
            $"hips_order            = {order}",
            "hips_order_min        = 0",
            $"hips_tile_width       = {TileSize}",
            "hips_tile_format      = webp",
            "dataproduct_type      = image",
            $"obs_title             = {t}",
            $"hips_release_date     = {DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture)}",
            "type                  = landscape",
            "");
    }

    private static SKBitmap RenderTile(Pixels src, Options o, SKColor ground, int nside, int pix) {
        var tile = new SKBitmap(new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        var buf = new byte[TileSize * TileSize * 4];
        int tileNside = nside / TileSize;
        Healpix.Nest2Xyf(tileNside, pix, out int ix, out int iy, out int face);
        double span = o.TopAltitudeDeg - o.BottomAltitudeDeg;
        for (int r = 0; r < TileSize; r++) {
            double a = (r + 0.5) / TileSize;
            for (int c = 0; c < TileSize; c++) {
                double b = (c + 0.5) / TileSize;
                Healpix.TileUvToAzAlt(tileNside, ix, iy, face, a, b, out double az, out double alt);
                int k = (r * TileSize + c) * 4;
                if (alt > o.TopAltitudeDeg) continue;   // transparent
                if (alt < o.BottomAltitudeDeg) {
                    buf[k] = ground.Red; buf[k + 1] = ground.Green; buf[k + 2] = ground.Blue; buf[k + 3] = 255;
                    continue;
                }
                double fx = Mod(az - o.LeftAzimuthDeg, 360) / 360 * src.Width - 0.5;
                double fy = (o.TopAltitudeDeg - alt) / span * src.Height - 0.5;
                src.Bilinear(fx, fy, buf, k);
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(buf, 0, tile.GetPixels(), buf.Length);
        tile.NotifyPixelsChanged();
        return tile;
    }

    /// <summary>The mean colour of the lowest 3 % of rows (opaque pixels only),
    /// used for the ground below the panorama's bottom edge.</summary>
    internal static SKColor GroundColour(SKBitmap pano) {
        var px = Pixels.From(pano);
        int rows = Math.Max(1, pano.Height * 3 / 100);
        double r = 0, g = 0, b = 0; long n = 0;
        for (int y = pano.Height - rows; y < pano.Height; y++)
            for (int x = 0; x < pano.Width; x += 2) {
                int k = (y * pano.Width + x) * 4;
                if (px.Data[k + 3] < 250) continue;
                r += px.Data[k]; g += px.Data[k + 1]; b += px.Data[k + 2]; n++;
            }
        if (n == 0) return new SKColor(26, 32, 24);
        return new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>True when some pixel is mostly transparent (a sky cut out).</summary>
    public static bool HasTransparency(SKBitmap pano) {
        var px = Pixels.From(pano);
        for (int k = 3; k < px.Data.Length; k += 4) if (px.Data[k] < 128) return true;
        return false;
    }

    /// <summary>Cut the sky out of an opaque panorama along a horizon profile:
    /// pixels above <paramref name="altitudeAt"/>(azimuth) turn transparent,
    /// with a 0.3 degree soft edge.</summary>
    public static void MaskAboveHorizon(SKBitmap pano, Options o, Func<double, double> altitudeAt) {
        var data = new byte[pano.Width * pano.Height * 4];
        var px = Pixels.From(pano);
        Buffer.BlockCopy(px.Data, 0, data, 0, data.Length);
        double span = o.TopAltitudeDeg - o.BottomAltitudeDeg;
        for (int x = 0; x < pano.Width; x++) {
            double az = Mod(o.LeftAzimuthDeg + (x + 0.5) / pano.Width * 360, 360);
            double h = altitudeAt(az);
            for (int y = 0; y < pano.Height; y++) {
                double alt = o.TopAltitudeDeg - (y + 0.5) / pano.Height * span;
                double keep = Math.Clamp((h + 0.15 - alt) / 0.3, 0, 1);
                if (keep >= 1) continue;
                int k = (y * pano.Width + x) * 4;
                for (int i = 0; i < 4; i++) data[k + i] = (byte)Math.Round(data[k + i] * keep);
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(data, 0, pano.GetPixels(), data.Length);
        pano.NotifyPixelsChanged();
    }

    private static SKBitmap Resize(SKBitmap src, int width) {
        int height = Math.Max(1, (int)Math.Round((double)src.Height * width / src.Width));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return src.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Could not resize the panorama.");
    }

    private static double Mod(double v, double m) => ((v % m) + m) % m;

    /// <summary>Premultiplied RGBA bytes of a bitmap, for bilinear sampling
    /// that wraps around in azimuth.</summary>
    private sealed class Pixels {
        public required byte[] Data;
        public int Width, Height;

        public static Pixels From(SKBitmap bmp) {
            if (bmp.ColorType != SKColorType.Rgba8888 || bmp.AlphaType != SKAlphaType.Premul)
                throw new ArgumentException("Expected an Rgba8888 premultiplied bitmap.");
            var data = new byte[bmp.Width * bmp.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), data, 0, data.Length);
            return new Pixels { Data = data, Width = bmp.Width, Height = bmp.Height };
        }

        public void Bilinear(double fx, double fy, byte[] dst, int k) {
            fy = Math.Clamp(fy, 0, Height - 1);
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int y1 = Math.Min(y0 + 1, Height - 1);
            x0 = ((x0 % Width) + Width) % Width;
            int x1 = (x0 + 1) % Width;
            int p00 = (y0 * Width + x0) * 4, p10 = (y0 * Width + x1) * 4;
            int p01 = (y1 * Width + x0) * 4, p11 = (y1 * Width + x1) * 4;
            for (int i = 0; i < 4; i++) {
                double top = Data[p00 + i] + (Data[p10 + i] - Data[p00 + i]) * tx;
                double bot = Data[p01 + i] + (Data[p11 + i] - Data[p01 + i]) * tx;
                dst[k + i] = (byte)Math.Round(top + (bot - top) * ty);
            }
        }
    }

    /// <summary>The engine's HEALPix helpers (algos/healpix.c), ported.</summary>
    internal static class Healpix {
        private static readonly int[,] Faces = {
            { 1, 0 }, { 3, 0 }, { 5, 0 }, { 7, 0 },
            { 0, -1 }, { 2, -1 }, { 4, -1 }, { 6, -1 },
            { 1, -2 }, { 3, -2 }, { 5, -2 }, { 7, -2 },
        };

        public static void Nest2Xyf(int nside, int pix, out int ix, out int iy, out int face) {
            int npface = nside * nside;
            face = pix / npface;
            int p = pix & (npface - 1);
            ix = Compact(p);
            iy = Compact(p >> 1);
        }

        /// <summary>Keep the even bits of <paramref name="v"/> and pack them.</summary>
        private static int Compact(int v) {
            v &= 0x55555555;
            v = (v | (v >> 1)) & 0x33333333;
            v = (v | (v >> 2)) & 0x0F0F0F0F;
            v = (v | (v >> 4)) & 0x00FF00FF;
            v = (v | (v >> 8)) & 0x0000FFFF;
            return v;
        }

        /// <summary>Where the engine paints texture point (row fraction
        /// <paramref name="a"/>, column fraction <paramref name="b"/>) of tile
        /// (nside, ix, iy, face): azimuth (N through E) and altitude, degrees.</summary>
        public static void TileUvToAzAlt(int nside, int ix, int iy, int face, double a, double b,
                out double azDeg, out double altDeg) {
            // healpix_get_mat3 applied to (a, b, 1): rows are (k, k), (-k, k)
            // and the face offset.
            double k = Math.PI / 4 / nside;
            double x = (Faces[face, 0] + (double)(ix - iy) / nside) * Math.PI / 4 + k * a - k * b;
            double y = (Faces[face, 1] + (double)(ix + iy) / nside) * Math.PI / 4 + k * a + k * b;
            XyToZPhi(x, y, out double z, out double phi);
            altDeg = Math.Asin(Math.Clamp(z, -1, 1)) * 180 / Math.PI;
            azDeg = Mod(-phi * 180 / Math.PI, 360);
        }

        /// <summary>healpix_xy2_z_phi.</summary>
        public static void XyToZPhi(double x, double y, out double z, out double phi) {
            if (Math.Abs(y) > Math.PI / 4) {
                double sigma = 2 - Math.Abs(y * 4) / Math.PI;
                z = (y > 0 ? 1 : -1) * (1 - sigma * sigma / 3);
                double xc = -Math.PI + (2 * Math.Floor((x + Math.PI) * 4 / (2 * Math.PI)) + 1) * Math.PI / 4;
                phi = sigma != 0 ? xc + (x - xc) / sigma : x;
            } else {
                phi = x;
                z = y * 8 / (Math.PI * 3);
            }
        }
    }
}
