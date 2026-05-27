using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Test.E2E;

/// <summary>
/// Standalone PlateSolveService probe: feeds a single-channel
/// pre-stacked master from <c>test_data/</c> through the real
/// AstapSolver to confirm the binary + CLI wiring + .ini parse all
/// work end-to-end. Useful as a faster localisation step when
/// Step09 in the full E2E fixture fails — runs in seconds instead
/// of minutes.
///
/// Marked <c>[Explicit]</c> so it stays out of the default sweep.
/// </summary>
[TestFixture, Category("E2E"),
 Explicit("Probes ASTAP install; needs test_data + astap_cli.exe")]
public class AstapWiringProbe {

    [Test]
    public async Task AstapSolver_OnPreStackedMonoMaster_ReturnsRealCoords() {
        var testData = FindTestDataRoot();
        var src = Path.Combine(testData, "mono", "M 16", "H", "result_H_3060s.fit");
        if (!File.Exists(src)) {
            Assert.Inconclusive($"Missing fixture: {src}");
        }

        // Copy to a temp location, ASTAP runs with -update and writes
        // WCS back into the source; we don't want to mutate the
        // committed test data.
        var tmp = Path.Combine(Path.GetTempPath(),
            "astap-probe-" + Guid.NewGuid().ToString("N")[..8] + ".fit");
        File.Copy(src, tmp, overwrite: true);

        try {
            var cfg = new ConfigurationBuilder().Build();
            var astap = new AstapSolver(cfg, NullLogger<AstapSolver>.Instance);
            Assume.That(astap.IsAvailable, Is.True,
                $"ASTAP binary not at {astap.SolverPath}");

            var result = await astap.SolveAsync(tmp, new PlateSolveOptions {
                HintRa = 18.31,
                HintDec = -13.78,
                SearchRadiusDeg = 5,
            });

            TestContext.WriteLine($"success={result.Success} solver={result.SolverUsed}");
            TestContext.WriteLine($"RA={result.RaHours}h Dec={result.DecDeg}° " +
                $"scale={result.ScaleArcsecPerPixel}\"/px");
            if (!result.Success) TestContext.WriteLine($"error={result.Error}");

            Assert.That(result.Success, Is.True,
                $"ASTAP failed on a known-good single-channel FITS: {result.Error}");
            // M 16 is at roughly RA 18h 19m, Dec -13.8°. Allow some
            // wiggle so a slightly different hint or frame center
            // doesn't flake the test.
            Assert.That(result.RaHours, Is.EqualTo(18.31).Within(0.2));
            Assert.That(result.DecDeg, Is.EqualTo(-13.78).Within(0.5));
        } finally {
            try { File.Delete(tmp); } catch { }
            // ASTAP also drops a .ini next to the FITS even though
            // AstapSolver tries to clean it up; sweep just in case.
            try { File.Delete(Path.ChangeExtension(tmp, ".ini")); } catch { }
            try { File.Delete(Path.ChangeExtension(tmp, ".wcs")); } catch { }
        }
    }

    [Test]
    public async Task AstapSolver_OnSynthetic3ChannelFits_ViaProxyPath() {
        // Same Ha master as the mono probe, but folded into a NAXIS=3
        // RGB cube so the proxy code path in AstapSolver fires. The
        // pixel data is identical across all three planes, so the
        // resulting WCS must match the single-channel solve to a few
        // arcseconds.
        var testData = FindTestDataRoot();
        var src = Path.Combine(testData, "mono", "M 16", "H", "result_H_3060s.fit");
        if (!File.Exists(src)) {
            Assert.Inconclusive($"Missing fixture: {src}");
        }

        // Load the mono FITS, replicate the plane into R/G/B.
        BaseImageData mono;
        using (var fs = File.OpenRead(src)) mono = FITSReader.Read(fs);
        int w = mono.Properties.Width;
        int h = mono.Properties.Height;
        var rgb = new ushort[w * h * 3];
        Array.Copy(mono.Data, 0, rgb, 0,             w * h);
        Array.Copy(mono.Data, 0, rgb, w * h,         w * h);
        Array.Copy(mono.Data, 0, rgb, w * h * 2,     w * h);

        var triProps = mono.Properties with { Channels = 3 };
        var triMeta  = mono.MetaData;
        var triImg   = new BaseImageData(rgb, triProps, triMeta);

        var tmp = Path.Combine(Path.GetTempPath(),
            "astap-probe-rgb-" + Guid.NewGuid().ToString("N")[..8] + ".fits");
        FITSWriter.Write(triImg, tmp);
        try {
            var cfg = new ConfigurationBuilder().Build();
            var astap = new AstapSolver(cfg, NullLogger<AstapSolver>.Instance);
            Assume.That(astap.IsAvailable, Is.True,
                $"ASTAP binary not at {astap.SolverPath}");

            var result = await astap.SolveAsync(tmp, new PlateSolveOptions {
                HintRa = 18.31,
                HintDec = -13.78,
                SearchRadiusDeg = 5,
            });

            TestContext.WriteLine($"success={result.Success}");
            TestContext.WriteLine($"RA={result.RaHours}h Dec={result.DecDeg}°");
            if (!result.Success) TestContext.WriteLine($"error={result.Error}");

            Assert.That(result.Success, Is.True,
                $"Proxy path failed on 3-channel FITS: {result.Error}");
            Assert.That(result.RaHours, Is.EqualTo(18.31).Within(0.2));
            Assert.That(result.DecDeg, Is.EqualTo(-13.78).Within(0.5));

            // Confirm the WCS landed back in the ORIGINAL 3-channel
            // FITS, not just the proxy temp. PCC reads from the
            // source the caller solved.
            BaseImageData reread;
            using (var fs = File.OpenRead(tmp)) reread = FITSReader.Read(fs);
            Assert.That(reread.Properties.Channels, Is.EqualTo(3),
                "WCS stamp lost the 3-channel layout");
            Assert.That(reread.Properties.Wcs, Is.Not.Null,
                "WCS keywords were not stamped into the original FITS");
        } finally {
            try { File.Delete(tmp); } catch { }
            try { File.Delete(Path.ChangeExtension(tmp, ".ini")); } catch { }
            try { File.Delete(Path.ChangeExtension(tmp, ".wcs")); } catch { }
        }
    }

    private static string FindTestDataRoot() {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null) {
            var candidate = Path.Combine(dir.FullName, "test_data");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        Assert.Inconclusive("test_data not found");
        return null!;
    }
}
