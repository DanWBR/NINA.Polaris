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

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Polaris.Services.PostProcess;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// DUST-1: DustRemovalService detects the soft circular shadow a dust speck
/// casts and divides it out with a local synthetic flat. The tests seed a flat
/// sky with a KNOWN multiplicative dip (and a star sitting on it, itself dimmed
/// by the dust), then assert:
///   • the corrected sky under the mote matches the surrounding sky;
///   • the star is RESTORED to its true brightness, not erased (the whole point
///     of dividing rather than inpainting);
///   • the settings land in the FITS header;
///   • a clean sky detects nothing (no false positives);
///   • a missing file fails loudly.
/// The correction is smooth and low-frequency, so a modest tolerance absorbs the
/// working-scale resample + 16-bit requantise round trip.
/// </summary>
[TestFixture]
public class DustRemovalServiceTests {

    private string _tmpRoot = null!;
    private DustRemovalService _svc = null!;

    [SetUp]
    public void Setup() {
        _tmpRoot = Path.Combine(Path.GetTempPath(),
            "polaris-dust-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpRoot);
        _svc = new DustRemovalService(NullLogger<DustRemovalService>.Instance);
    }

    [TearDown]
    public void Teardown() {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    [Test]
    public void Remove_CircularMote_RestoresSkyAndStar() {
        int w = 400, h = 400, cx = 150, cy = 150, radius = 70;
        double depth = 0.06;
        ushort skyLevel = 800, starPeak = 30000;
        var path = SeedMoteFrame(w, h, skyLevel, cx, cy, radius, depth, starPeak);

        var res = _svc.Remove(path, new DustMoteRemoval.Params());
        Assert.That(res.Count, Is.GreaterThanOrEqualTo(1), "should detect the seeded mote");
        Assert.That(File.Exists(res.OutputPath), Is.True);
        Assert.That(Path.GetFileName(res.OutputPath), Does.Contain("_dustfix"));

        var outImg = ReadFits(res.OutputPath);
        int plane = w * h;
        var r = new ushort[plane];
        Array.Copy(outImg.Data, 0, r, 0, plane); // R plane

        // Sky far from the mote, and the mote core excluding the star.
        double sky = MeanBlock(r, w, 320, 360, 320, 360);
        double core = MeanRing(r, w, cx, cy, 10, 22);
        Assert.That(core / sky, Is.EqualTo(1.0).Within(0.02),
            $"mote core ({core:F1}) should match the surrounding sky ({sky:F1})");

        // The star was stored dimmed by the dust (starPeak * (1-depth)); after
        // dividing it should climb back toward its true brightness.
        double stored = starPeak * (1 - depth);
        ushort restored = r[cy * w + cx];
        Assert.That(restored, Is.GreaterThan(stored + 200),
            "the star on the mote must be restored, not left dimmed or erased");
    }

    [Test]
    public void Remove_StampsHeaderKeywords() {
        var path = SeedMoteFrame(400, 400, 800, 150, 150, 70, 0.06, 30000);
        var res = _svc.Remove(path, new DustMoteRemoval.Params(SensitivityPct: 1.2, FeatherPct: 3.0));

        using var fs = File.OpenRead(res.OutputPath);
        var headers = FITSReader.ReadHeadersOnly(fs);
        Assert.That(headers.ContainsKey("DUSTMOT"), Is.True);
        Assert.That(headers["DUSTMOT"].Value, Does.Contain("T"));
        Assert.That(headers.ContainsKey("DMCOUNT"), Is.True);
        Assert.That(int.Parse(headers["DMCOUNT"].Value), Is.GreaterThanOrEqualTo(1));
        Assert.That(headers.ContainsKey("DMSENS"), Is.True);
        Assert.That(headers.ContainsKey("DMFEATH"), Is.True);
    }

    [Test]
    public void Remove_FlatSky_DetectsNothing() {
        // A clean sky with a few stars, no mote: nothing to correct.
        var path = SeedMoteFrame(400, 400, 800, 0, 0, 0, 0.0, 30000, starOnly: true);
        var res = _svc.Remove(path, new DustMoteRemoval.Params());
        Assert.That(res.Count, Is.EqualTo(0), "a flat sky must not yield false motes");
    }

    [Test]
    public void Remove_MissingFile_Throws() {
        Assert.Throws<FileNotFoundException>(() =>
            _svc.Remove(Path.Combine(_tmpRoot, "nope.fits"), new DustMoteRemoval.Params()));
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    /// <summary>Seed a 3-channel FITS: flat sky + mild noise, a smooth circular
    /// multiplicative dip (the mote), and a bright star at the centre that is
    /// itself dimmed by the dust it sits behind.</summary>
    private string SeedMoteFrame(int w, int h, ushort sky, int cx, int cy, int radius,
                                 double depth, ushort starPeak, bool starOnly = false) {
        int plane = w * h;
        var pix = new ushort[plane * 3];
        uint seed = 0x9E3779B9;
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                // Deterministic mild noise so medians/MAD aren't degenerate.
                seed = seed * 1664525u + 1013904223u;
                double noise = (seed >> 24) / 255.0 * 8.0 - 4.0;
                double t = 1.0;
                if (!starOnly && radius > 0) {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d < radius) {
                        double f = 0.5 * (1 + Math.Cos(Math.PI * (d / radius))); // 1 centre → 0 edge
                        t = 1.0 - depth * f;
                    }
                }
                ushort v = (ushort)Math.Clamp(Math.Round(sky * t + noise), 0, 65535);
                pix[y * w + x] = v;
                pix[plane + y * w + x] = v;
                pix[2 * plane + y * w + x] = v;
            }
        }
        // Stars: a bright 5×5, dimmed by the local transmission.
        void Star(int sx, int sy) {
            double d = Math.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
            double t = (!starOnly && radius > 0 && d < radius)
                ? 1.0 - depth * 0.5 * (1 + Math.Cos(Math.PI * (d / radius))) : 1.0;
            ushort val = (ushort)Math.Clamp(Math.Round(starPeak * t), 0, 65535);
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++) {
                    int x = sx + dx, y = sy + dy;
                    if (x < 0 || x >= w || y < 0 || y >= h) continue;
                    for (int c = 0; c < 3; c++) pix[c * plane + y * w + x] = val;
                }
        }
        if (starOnly) { Star(w / 4, h / 4); Star(3 * w / 4, h / 4); Star(w / 2, 3 * h / 4); }
        else Star(cx, cy);

        var img = new BaseImageData(pix,
            new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 3 },
            new ImageMetaData {
                Target = new ImageMetaData.TargetInfo { Name = "DustTest" },
                Exposure = new ImageMetaData.ExposureInfo { ImageType = "MASTERLIGHT", ExposureTime = 180 },
            });
        var path = Path.Combine(_tmpRoot, $"frame_{Guid.NewGuid().ToString("N")[..6]}.fits");
        FITSWriter.Write(img, path);
        return path;
    }

    private static BaseImageData ReadFits(string path) {
        using var fs = File.OpenRead(path);
        return FITSReader.Read(fs);
    }

    private static double MeanBlock(ushort[] p, int w, int x0, int x1, int y0, int y1) {
        double s = 0; int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) { s += p[y * w + x]; n++; }
        return n > 0 ? s / n : 0;
    }

    private static double MeanRing(ushort[] p, int w, int cx, int cy, double rLo, double rHi) {
        double s = 0; int n = 0;
        int r = (int)Math.Ceiling(rHi);
        for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++) {
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d >= rLo && d <= rHi) { s += p[y * w + x]; n++; }
            }
        return n > 0 ? s / n : 0;
    }
}
