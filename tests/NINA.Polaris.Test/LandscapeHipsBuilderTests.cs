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
using NINA.Polaris.Services.Sky;
using NUnit.Framework;
using SkiaSharp;
using H = NINA.Polaris.Services.Sky.LandscapeHipsBuilder.Healpix;

namespace NINA.Polaris.Test;

/// <summary>Site landscape import (issue #80): the panorama to HiPS conversion.</summary>
[TestFixture]
public class LandscapeHipsBuilderTests {
    private string _dir = "";

    [SetUp]
    public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "polaris-landscape-" + Guid.NewGuid().ToString("N")[..8]);

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Test]
    public void TileCentresMatchHealpix() {
        // Order 0, pixel 0: the centre is at z = 2/3, phi = 45 deg, so
        // altitude asin(2/3) and azimuth -45 deg.
        H.Nest2Xyf(1, 0, out int ix, out int iy, out int face);
        H.TileUvToAzAlt(1, ix, iy, face, 0.5, 0.5, out double az, out double alt);
        Assert.That(alt, Is.EqualTo(Math.Asin(2.0 / 3) * 180 / Math.PI).Within(1e-9));
        Assert.That(az, Is.EqualTo(315).Within(1e-9));
        // Pixel 4 (equatorial face 4) is centred on the horizon at phi = 0.
        H.Nest2Xyf(1, 4, out ix, out iy, out face);
        H.TileUvToAzAlt(1, ix, iy, face, 0.5, 0.5, out az, out alt);
        Assert.That(alt, Is.EqualTo(0).Within(1e-9));
        Assert.That(az, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void NestedIndexRoundTrips() {
        int nside = 8;
        for (int pix = 0; pix < 12 * nside * nside; pix++) {
            H.Nest2Xyf(nside, pix, out int ix, out int iy, out int face);
            int back = face * nside * nside + Spread(ix) | (Spread(iy) << 1);
            Assert.That(back, Is.EqualTo(pix));
        }
        static int Spread(int v) { int r = 0; for (int b = 0; b < 16; b++) r |= ((v >> b) & 1) << (2 * b); return r; }
    }

    /// <summary>
    /// The bundled Guéreins tiles are what the engine renders seamlessly.
    /// Reprojected into an equirectangular picture with the builder's mapping,
    /// neighbouring pixels that come from two different tiles differ about as
    /// much as neighbours inside one tile. With rows and columns swapped (the
    /// obvious HiPS layout, which the engine doesn't use) the tile edges show.
    /// </summary>
    [Test]
    public void TheMappingReprojectsTheBundledLandscapeWithoutSeams() {
        var tilesDir = FindGuereins();
        if (tilesDir == null) Assert.Ignore("bundled Guéreins tiles not found");
        double right = SeamRatio(tilesDir!, transposed: false);
        double swapped = SeamRatio(tilesDir!, transposed: true);
        TestContext.Out.WriteLine($"seam/interior difference: builder {right:F2}, swapped {swapped:F2}");
        Assert.That(right, Is.LessThan(1.6));
        Assert.That(swapped, Is.GreaterThan(right * 1.5));
    }

    [Test]
    public void APanoramaRoundTripsThroughTheTiles() {
        // A synthetic site: transparent sky, a ground whose colour encodes the
        // azimuth (red) and the altitude (green), a skyline at 10 deg.
        const int w = 3600, h = 600;   // 0.1 deg/px, top 50 deg, bottom -10 deg
        using var pano = Synthetic(w, h, top: 50, bottom: -10, skyline: 10);
        var opt = new LandscapeHipsBuilder.Options(50, -10, 0, "Test site", Order: 0);
        int order = LandscapeHipsBuilder.Build(pano, opt, _dir);
        Assert.That(order, Is.EqualTo(0));
        Assert.That(File.Exists(Path.Combine(_dir, "properties")));
        Assert.That(File.Exists(Path.Combine(_dir, "Norder0", "Allsky.webp")));
        Assert.That(Directory.GetFiles(Path.Combine(_dir, "Norder0", "Dir0"), "*.webp"), Has.Length.EqualTo(12));
        Assert.That(File.ReadAllText(Path.Combine(_dir, "properties")), Does.Contain("hips_order            = 0"));

        // Read every tile back: each opaque pixel's colour gives back the
        // azimuth and altitude it was painted at; the sky above 10 deg is clear.
        int checkedPx = 0;
        double worstAz = 0, worstAlt = 0;
        for (int pix = 0; pix < 12; pix++) {
            using var tile = SKBitmap.Decode(Path.Combine(_dir, "Norder0", "Dir0", $"Npix{pix}.webp"));
            H.Nest2Xyf(1, pix, out int ix, out int iy, out int face);
            for (int r = 8; r < 512; r += 16)
                for (int c = 8; c < 512; c += 16) {
                    H.TileUvToAzAlt(1, ix, iy, face, (r + 0.5) / 512, (c + 0.5) / 512, out double az, out double alt);
                    var px = tile.GetPixel(c, r);
                    if (alt > 11) { Assert.That(px.Alpha, Is.LessThan(40), $"sky at az {az:F1} alt {alt:F1}"); continue; }
                    if (alt < 9 && alt > -9) {
                        Assert.That(px.Alpha, Is.GreaterThan(200), $"ground at az {az:F1} alt {alt:F1}");
                        double gotAz = px.Red / 255.0 * 360;
                        double dAz = Math.Abs(((gotAz - az) % 360 + 540) % 360 - 180);
                        if (az > 5 && az < 355) worstAz = Math.Max(worstAz, dAz);
                        double gotAlt = px.Green / 255.0 * 60 - 10;
                        worstAlt = Math.Max(worstAlt, Math.Abs(gotAlt - alt));
                        checkedPx++;
                    }
                }
        }
        TestContext.Out.WriteLine($"{checkedPx} px checked, worst azimuth error {worstAz:F2} deg, altitude {worstAlt:F2} deg");
        Assert.That(checkedPx, Is.GreaterThan(300));
        Assert.That(worstAz, Is.LessThan(4));   // 8-bit colour steps of 1.4 deg plus webp
        Assert.That(worstAlt, Is.LessThan(2));
    }

    [Test]
    public void TheLeftAzimuthRotatesThePanorama() {
        using var pano = Synthetic(3600, 600, 50, -10, 10);
        var opt = new LandscapeHipsBuilder.Options(50, -10, 90, "Rotated", Order: 0);
        LandscapeHipsBuilder.Build(pano, opt, _dir);
        // Equatorial tile 4 is centred on azimuth 0: with the left edge at
        // 90 deg, that direction shows the panorama's column for 270 deg.
        using var tile = SKBitmap.Decode(Path.Combine(_dir, "Norder0", "Dir0", "Npix4.webp"));
        var px = tile.GetPixel(256, 256);
        Assert.That(px.Red / 255.0 * 360, Is.EqualTo(270).Within(4));
    }

    [Test]
    public void AnOpaquePhotoIsCutAlongTheHorizon() {
        const int w = 3600, h = 600;
        using var pano = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(pano)) canvas.Clear(new SKColor(90, 120, 200));
        Assert.That(LandscapeHipsBuilder.HasTransparency(pano), Is.False);
        var opt = new LandscapeHipsBuilder.Options(50, -10, 0, "Photo");
        var horizon = new List<HorizonPoint> { new() { Azimuth = 0, Altitude = 20 }, new() { Azimuth = 180, Altitude = 5 } };
        LandscapeHipsBuilder.MaskAboveHorizon(pano, opt, az => HorizonProfile.AltitudeAt(horizon, az));
        Assert.That(LandscapeHipsBuilder.HasTransparency(pano), Is.True);
        // Column for az 0: horizon at 20 deg, so the row at 25 deg is clear and 15 deg kept.
        int RowAt(double alt) => (int)((50 - alt) / 60 * h);
        Assert.That(pano.GetPixel(0, RowAt(25)).Alpha, Is.EqualTo(0));
        Assert.That(pano.GetPixel(0, RowAt(15)).Alpha, Is.EqualTo(255));
        // Column for az 180: horizon at 5 deg.
        Assert.That(pano.GetPixel(w / 2, RowAt(10)).Alpha, Is.EqualTo(0));
        Assert.That(pano.GetPixel(w / 2, RowAt(0)).Alpha, Is.EqualTo(255));
    }

    [TestCase(2048, 0)]
    [TestCase(3144, 0)]
    [TestCase(6144, 1)]
    [TestCase(8000, 2)]
    [TestCase(20000, 2)]
    public void TheOrderFollowsThePanoramaWidth(int width, int order)
        => Assert.That(LandscapeHipsBuilder.OrderFor(width), Is.EqualTo(order));

    [Test]
    public void BadEdgesAreRefused() {
        Assert.Throws<LandscapeImportException>(() => SiteLandscapeService.Validate(new(10, 20, 0, "x")));
        Assert.Throws<LandscapeImportException>(() => SiteLandscapeService.Validate(new(95, 0, 0, "x")));
        Assert.Throws<LandscapeImportException>(() => SiteLandscapeService.Validate(new(double.NaN, 0, 0, "x")));
        Assert.DoesNotThrow(() => SiteLandscapeService.Validate(new(90, -90, 0, "x")));
    }

    private static SKBitmap Synthetic(int w, int h, double top, double bottom, double skyline) {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++) {
            double alt = top - (y + 0.5) / h * (top - bottom);
            if (alt > skyline) continue;
            for (int x = 0; x < w; x++) {
                double az = (x + 0.5) / w * 360;
                int k = (y * w + x) * 4;
                data[k] = (byte)Math.Round(az / 360 * 255);
                data[k + 1] = (byte)Math.Round((alt + 10) / 60 * 255);
                data[k + 2] = 60;
                data[k + 3] = 255;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(data, 0, bmp.GetPixels(), data.Length);
        return bmp;
    }

    private static string? FindGuereins([System.Runtime.CompilerServices.CallerFilePath] string source = "") {
        // From this source file up to the repo root (the tests may run from a
        // build directory elsewhere).
        var d = new DirectoryInfo(Path.GetDirectoryName(source) ?? TestContext.CurrentContext.TestDirectory);
        while (d != null) {
            var p = Path.Combine(d.FullName, "src", "NINA.Polaris", "wwwroot", "sky", "data", "skydata",
                "landscapes", "guereins", "Norder0", "Dir0");
            if (Directory.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }

    /// <summary>Mean colour difference between horizontal neighbours of the
    /// reprojected picture that come from different tiles, over the same for
    /// neighbours from one tile.</summary>
    private static double SeamRatio(string tilesDir, bool transposed) {
        const int W = 1440, Hh = 720;
        var col = new int[W * Hh];
        var src = new short[W * Hh];
        Array.Fill(src, (short)-1);
        for (int pix = 0; pix < 12; pix++) {
            using var tile = SKBitmap.Decode(Path.Combine(tilesDir, $"Npix{pix}.webp"));
            H.Nest2Xyf(1, pix, out int ix, out int iy, out int face);
            for (int r = 0; r < tile.Height; r++)
                for (int c = 0; c < tile.Width; c++) {
                    double a = (r + 0.5) / tile.Height, b = (c + 0.5) / tile.Width;
                    if (transposed) (a, b) = (b, a);
                    H.TileUvToAzAlt(1, ix, iy, face, a, b, out double az, out double alt);
                    int x = Math.Clamp((int)(az / 360 * W), 0, W - 1);
                    int y = Math.Clamp((int)((90 - alt) / 180 * Hh), 0, Hh - 1);
                    var p = tile.GetPixel(c, r);
                    if (p.Alpha < 250) continue;
                    col[y * W + x] = p.Red + p.Green + p.Blue;
                    src[y * W + x] = (short)pix;
                }
        }
        double seam = 0, inner = 0; long ns = 0, ni = 0;
        for (int y = 0; y < Hh; y++)
            for (int x = 0; x + 1 < W; x++) {
                int i = y * W + x;
                if (src[i] < 0 || src[i + 1] < 0) continue;
                double d = Math.Abs(col[i] - col[i + 1]);
                if (src[i] != src[i + 1]) { seam += d; ns++; } else { inner += d; ni++; }
            }
        return ns == 0 || ni == 0 ? double.NaN : (seam / ns) / (inner / ni);
    }
}
