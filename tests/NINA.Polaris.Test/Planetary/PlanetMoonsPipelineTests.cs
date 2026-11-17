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

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Planetary;

namespace NINA.Polaris.Test.Planetary;

/// <summary>
/// The planet + moons pipeline after the recordings: two synthetic colour
/// (RGGB) SER clips with seeing jitter, one exposed for the disc and one for
/// the moons, stacked with the service's own stack settings and merged.
/// </summary>
[TestFixture]
public class PlanetMoonsPipelineTests {
    private const int W = 320, H = 240;
    private const double Cx = 160, Cy = 120, R = 30;
    private static readonly (double X, double Y, double Rel)[] Moons = {
        (60, 110, 0.020), (250, 128, 0.012), (290, 116, 0.006),
    };
    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "pm-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>One RGGB frame: the scene at (dx, dy), colour factors per
    /// CFA site, sky plus noise, clipped at full scale.</summary>
    private static ushort[] Frame(double exposureMs, double dx, double dy, Random rnd) {
        var f = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) {
                bool evenRow = (y & 1) == 0, evenCol = (x & 1) == 0;
                double colour = evenRow && evenCol ? 1.0 : (!evenRow && !evenCol ? 0.7 : 0.9);
                double v = 0;
                double px = x - (Cx + dx), py = y - (Cy + dy);
                double r = Math.Sqrt(px * px + py * py);
                if (r < R) {
                    double band = Math.Abs(py) < 6 ? 0.7 : 1.0;
                    v += 10000 * exposureMs * band * (Math.Sqrt(1 - (r / R) * (r / R)) * 0.4 + 0.6);
                }
                foreach (var m in Moons) {
                    double mx = x - (m.X + dx), my = y - (m.Y + dy);
                    v += 10000 * exposureMs * m.Rel * 40 * Math.Exp(-(mx * mx + my * my) / (2 * 1.6 * 1.6));
                }
                f[y * W + x] = (ushort)Math.Clamp(Math.Round(800 + rnd.NextDouble() * 60 + v * colour), 0, 65535);
            }
        return f;
    }

    private string WriteSer(string name, double exposureMs, int frames, int seed) {
        var path = Path.Combine(_dir, name + ".ser");
        var rnd = new Random(seed);
        using var w = new SerFileWriter(path, W, H, 16, SerColorMode.BayerRGGB);
        for (int i = 0; i < frames; i++) {
            // Seeing jitter, even pixel steps so the CFA phase stays put.
            double jx = 2 * rnd.Next(-1, 2), jy = 2 * rnd.Next(-1, 2);
            w.WriteFrame(Frame(exposureMs, jx, jy, rnd), DateTime.UtcNow);
        }
        return path;
    }

    private static async Task<StackJob> Stack(PlanetaryStackerService svc, StackConfig cfg) {
        var job = svc.StartJob(cfg);
        await job.Task!;
        Assert.That(job.Phase, Is.EqualTo(StackPhase.Ok), job.Error);
        return job;
    }

    [Test]
    public async Task RecordingsToComposite() {
        var planetSer = WriteSer("planet", 4.5, 40, 1);
        var moonSer = WriteSer("moons", 150, 30, 2);

        var profiles = new ProfileService(new ConfigurationBuilder().Build(), NullLogger<ProfileService>.Instance);
        var stacker = new PlanetaryStackerService(profiles, NullLogger<PlanetaryStackerService>.Instance);
        var stackDir = Path.Combine(_dir, "stacked");
        var planet = await Stack(stacker, PlanetMoonsService.PlanetStackConfig(planetSer, stackDir, "Jupiter", 25));
        var moons = await Stack(stacker, PlanetMoonsService.MoonStackConfig(moonSer, stackDir, "Jupiter", planet.WhiteBalanceApplied));
        Assert.That(moons.Config.WhiteBalance, Is.EqualTo("manual"), "moons reuse the planet's balance");

        var (fits, png, result) = PlanetMoonsService.WriteCompositeFiles(
            planet.OutputPath!, moons.OutputPath!, Path.Combine(_dir, "composite"), "Jupiter");
        Assert.That(File.Exists(fits) && File.Exists(png));
        Assert.That(result.Channels, Is.EqualTo(3));

        var out1 = Environment.GetEnvironmentVariable("POLARIS_PM_PNG_OUT");
        if (!string.IsNullOrEmpty(out1)) File.Copy(png, out1, overwrite: true);

        int n = W * H;
        float G(double x, double y) => result.Rgb[n + (int)Math.Round(y) * W + (int)Math.Round(x)];
        Assert.That(G(Cx, Cy), Is.LessThan(G(Cx, Cy - 15) * 0.85), "the belt shows: disc from the short stack");
        Assert.That(G(Cx, Cy - 15), Is.InRange(0.4, 1.0));
        foreach (var m in Moons)
            Assert.That(G(m.X, m.Y), Is.GreaterThan(0.08), $"moon at {m.X},{m.Y}");
        Assert.That(G(20, 220), Is.LessThan(0.05), "sky stays dark");

        var back = FITSReader.Read(File.ReadAllBytes(fits));
        Assert.That(back.Properties.Width, Is.EqualTo(W));
        Assert.That(back.Properties.Channels, Is.EqualTo(3));
    }
}
