using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using NINA.Headless.Services;
using NINA.Headless.Services.PlateSolving;

namespace NINA.Headless.Test;

[TestFixture]
public class PlateSolveServiceTests {
    private string _tempDir = null!;

    [SetUp]
    public void SetUp() {
        _tempDir = Path.Combine(Path.GetTempPath(), "NinaHeadlessTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private static AstapSolver CreateAstap(string? path = null) {
        var values = new Dictionary<string, string?>();
        if (path != null) values["PlateSolve:AstapPath"] = path;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AstapSolver(config, new Mock<ILogger<AstapSolver>>().Object);
    }

    private static PlateSolveService CreateService(string? astapPath = null) {
        var values = new Dictionary<string, string?>();
        if (astapPath != null) values["PlateSolve:AstapPath"] = astapPath;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new PlateSolveService(config, new Mock<ILogger<PlateSolveService>>().Object);
    }

    // --- AstapSolver.ParseIniResult ---

    [Test]
    public void ParseIniResult_Success_ExtractsValues() {
        var solver = CreateAstap("/nonexistent/astap");
        var fitsPath = Path.Combine(_tempDir, "test.fits");
        File.WriteAllText(Path.ChangeExtension(fitsPath, ".ini"),
            """
            PLTSOLVD=T
            CRVAL1=180.5
            CRVAL2=45.25
            CDELT1=-0.000556
            CROTA1=1.5
            """);

        var result = solver.ParseIniResult(fitsPath);

        Assert.That(result.Success, Is.True);
        Assert.That(result.SolverUsed, Is.EqualTo("astap"));
        Assert.That(result.RaDeg, Is.EqualTo(180.5).Within(0.001));
        Assert.That(result.RaHours, Is.EqualTo(180.5 / 15.0).Within(0.001));
        Assert.That(result.DecDeg, Is.EqualTo(45.25).Within(0.001));
        Assert.That(result.ScaleArcsecPerPixel, Is.EqualTo(0.000556 * 3600).Within(0.01));
        Assert.That(result.RotationDeg, Is.EqualTo(1.5).Within(0.001));
    }

    [Test]
    public void ParseIniResult_Failed_ReturnsFalse() {
        var solver = CreateAstap();
        var fitsPath = Path.Combine(_tempDir, "failed.fits");
        File.WriteAllText(Path.ChangeExtension(fitsPath, ".ini"), "PLTSOLVD=F");

        var result = solver.ParseIniResult(fitsPath);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("did not converge"));
    }

    [Test]
    public void ParseIniResult_MissingIniFile_ReturnsFailed() {
        var solver = CreateAstap();
        var result = solver.ParseIniResult(Path.Combine(_tempDir, "missing.fits"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("not found"));
    }

    [Test]
    public void ParseIniResult_PartialValues_ParsesAvailable() {
        var solver = CreateAstap();
        var fitsPath = Path.Combine(_tempDir, "partial.fits");
        File.WriteAllText(Path.ChangeExtension(fitsPath, ".ini"),
            """
            PLTSOLVD=T
            CRVAL1=90.0
            """);

        var result = solver.ParseIniResult(fitsPath);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RaDeg, Is.EqualTo(90.0).Within(0.001));
        Assert.That(result.DecDeg, Is.EqualTo(0.0));
        Assert.That(result.ScaleArcsecPerPixel, Is.EqualTo(0.0));
    }

    // --- AstapSolver.BuildArgs ---

    [Test]
    public void BuildArgs_WithHints_IncludesRaSpd() {
        var solver = CreateAstap();
        var args = solver.BuildArgs("/tmp/test.fits", new PlateSolveOptions {
            HintRa = 12.5, HintDec = 45.0, SearchRadiusDeg = 10
        });

        Assert.That(args, Does.Contain("-ra 12.5"));
        Assert.That(args, Does.Contain("-spd 135")); // Dec 45 + 90
        Assert.That(args, Does.Contain("-r 10"));
    }

    [Test]
    public void BuildArgs_WithFov_IncludesFov() {
        var solver = CreateAstap();
        var args = solver.BuildArgs("/tmp/test.fits",
            new PlateSolveOptions { FovDeg = 2.5, SearchRadiusDeg = 0 });
        Assert.That(args, Does.Contain("-fov 2.5"));
    }

    [Test]
    public void BuildArgs_WithDownsample_IncludesZ() {
        var solver = CreateAstap();
        var args = solver.BuildArgs("/tmp/test.fits",
            new PlateSolveOptions { Downsample = 4, SearchRadiusDeg = 0 });
        Assert.That(args, Does.Contain("-z 4"));
    }

    [Test]
    public void BuildArgs_AlwaysIncludesUpdate() {
        var solver = CreateAstap();
        var args = solver.BuildArgs("/tmp/test.fits", new PlateSolveOptions { SearchRadiusDeg = 0 });
        Assert.That(args, Does.Contain("-update"));
    }

    [Test]
    public void BuildArgs_WithoutHints_OmitsRaSpd() {
        var solver = CreateAstap();
        var args = solver.BuildArgs("/tmp/test.fits",
            new PlateSolveOptions { HintRa = null, HintDec = null, SearchRadiusDeg = 10 });

        Assert.That(args, Does.Not.Contain("-ra"));
        Assert.That(args, Does.Not.Contain("-spd"));
    }

    // --- AstapSolver.IsAvailable ---

    [Test]
    public void Astap_IsAvailable_WhenPathNotSet_ReturnsFalse() {
        Assert.That(CreateAstap("/definitely/does/not/exist/astap_cli").IsAvailable, Is.False);
    }

    [Test]
    public void Astap_IsAvailable_WhenPathIsEmptyString_ReturnsFalse() {
        Assert.That(CreateAstap("").IsAvailable, Is.False);
    }

    // --- SolveAsync guards (via dispatcher) ---

    [Test]
    public async Task SolveAsync_WhenNotAvailable_ReturnsFailed() {
        var service = CreateService("/not/a/real/path/astap_cli");
        var result = await service.SolveAsync("/tmp/test.fits", new PlateSolveOptions());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Match("(?i)(not available|not configured|not found)"));
    }

    // --- PlateSolveResult.Failed factory ---

    [Test]
    public void PlateSolveResult_Failed_SetsProperties() {
        var result = PlateSolveResult.Failed("something went wrong");
        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.EqualTo("something went wrong"));
    }

    // --- PlateSolveOptions defaults ---

    [Test]
    public void PlateSolveOptions_HasReasonableDefaults() {
        var options = new PlateSolveOptions();
        Assert.That(options.SearchRadiusDeg, Is.EqualTo(30));
        Assert.That(options.Downsample, Is.EqualTo(2));
        Assert.That(options.HintRa, Is.Null);
        Assert.That(options.HintDec, Is.Null);
        Assert.That(options.FovDeg, Is.EqualTo(0));
        Assert.That(options.ScaleArcsecPerPixel, Is.EqualTo(0));
    }

    // --- Dispatcher selection ---

    [Test]
    public void Dispatcher_DefaultPrimary_IsAstap() {
        var service = CreateService();
        Assert.That(service.PrimarySolver.Id, Is.EqualTo("astap"));
    }

    [Test]
    public void Dispatcher_CustomPrimary_IsHonoured() {
        var values = new Dictionary<string, string?> {
            ["PlateSolve:PrimarySolver"] = "platesolve3"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var service = new PlateSolveService(config, new Mock<ILogger<PlateSolveService>>().Object);
        Assert.That(service.PrimarySolver.Id, Is.EqualTo("platesolve3"));
    }

    [Test]
    public void Dispatcher_UnknownPrimary_FallsBackToAstap() {
        var values = new Dictionary<string, string?> {
            ["PlateSolve:PrimarySolver"] = "no-such-solver"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var service = new PlateSolveService(config, new Mock<ILogger<PlateSolveService>>().Object);
        Assert.That(service.PrimarySolver.Id, Is.EqualTo("astap"));
    }

    [Test]
    public void Dispatcher_BlindFallbackCanBeDisabled() {
        var values = new Dictionary<string, string?> {
            ["PlateSolve:UseBlindFallback"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var service = new PlateSolveService(config, new Mock<ILogger<PlateSolveService>>().Object);
        Assert.That(service.BlindSolver, Is.Null);
    }

    [Test]
    public void Dispatcher_AllSolvers_ContainsFour() {
        var service = CreateService();
        var ids = service.AllSolvers.Select(s => s.Id).ToHashSet();
        Assert.That(ids, Has.Count.EqualTo(4));
        Assert.That(ids, Does.Contain("astap"));
        Assert.That(ids, Does.Contain("platesolve3"));
        Assert.That(ids, Does.Contain("astrometry-net-online"));
        Assert.That(ids, Does.Contain("astrometry-net-local"));
    }

    // --- PlateSolve3 stdout parsing ---

    [Test]
    public void PlateSolve3_ParseStdout_HmsDmsFormat() {
        var config = new ConfigurationBuilder().Build();
        var solver = new PlateSolve3Solver(config, new Mock<ILogger<PlateSolve3Solver>>().Object);
        var stdout = """
            PlateSolve3.80
            Match Found
            RA: 12h 34m 56.78s
            Dec: +45d 30' 15.0"
            Pixel size: 1.234 arcsec
            Position angle: 12.5
            """;

        var r = solver.ParseStdout(stdout, "test.fits");

        Assert.That(r.Success, Is.True);
        Assert.That(r.SolverUsed, Is.EqualTo("platesolve3"));
        Assert.That(r.RaHours, Is.EqualTo(12 + 34 / 60.0 + 56.78 / 3600.0).Within(0.0001));
        Assert.That(r.DecDeg, Is.EqualTo(45 + 30 / 60.0 + 15.0 / 3600.0).Within(0.0001));
        Assert.That(r.ScaleArcsecPerPixel, Is.EqualTo(1.234).Within(0.001));
        Assert.That(r.RotationDeg, Is.EqualTo(12.5).Within(0.001));
    }

    [Test]
    public void PlateSolve3_ParseStdout_NoMatch_ReturnsFailed() {
        var config = new ConfigurationBuilder().Build();
        var solver = new PlateSolve3Solver(config, new Mock<ILogger<PlateSolve3Solver>>().Object);
        var r = solver.ParseStdout("PlateSolve3.80\nNo solution\n", "test.fits");
        Assert.That(r.Success, Is.False);
        Assert.That(r.Error, Does.Contain("did not find"));
    }

    // --- Astrometry.net local solve-field parsing ---

    [Test]
    public void SolveField_ParseStdout_ExtractsRaDecScale() {
        var config = new ConfigurationBuilder().Build();
        var solver = new AstrometryNetLocalSolver(config, new Mock<ILogger<AstrometryNetLocalSolver>>().Object);
        var stdout = """
            Reading input file 1 of 1: test.fits
            Field center: (RA,Dec) = (180.5432, +12.3456) deg.
            Field size: 1.23 x 0.82 degrees
            pixel scale 1.234 arcsec/pix
            Field rotation angle: up is 5.0 degrees E of N
            Solved
            """;

        var r = solver.ParseStdout(stdout);

        Assert.That(r.Success, Is.True);
        Assert.That(r.SolverUsed, Is.EqualTo("astrometry-net-local"));
        Assert.That(r.RaDeg, Is.EqualTo(180.5432).Within(0.0001));
        Assert.That(r.RaHours, Is.EqualTo(180.5432 / 15.0).Within(0.0001));
        Assert.That(r.DecDeg, Is.EqualTo(12.3456).Within(0.0001));
        Assert.That(r.ScaleArcsecPerPixel, Is.EqualTo(1.234).Within(0.001));
        Assert.That(r.RotationDeg, Is.EqualTo(5.0).Within(0.001));
    }

    [Test]
    public void SolveField_ParseStdout_DidNotSolve_ReturnsFailed() {
        var config = new ConfigurationBuilder().Build();
        var solver = new AstrometryNetLocalSolver(config, new Mock<ILogger<AstrometryNetLocalSolver>>().Object);
        var r = solver.ParseStdout("Did not solve (loglikelihood too low).\n");
        Assert.That(r.Success, Is.False);
    }

    [Test]
    public void SolveField_BuildArgs_WithHints_IncludesScaleWindow() {
        var config = new ConfigurationBuilder().Build();
        var solver = new AstrometryNetLocalSolver(config, new Mock<ILogger<AstrometryNetLocalSolver>>().Object);
        var args = solver.BuildArgs("/tmp/test.fits", new PlateSolveOptions {
            HintRa = 12.0, HintDec = -30.0, SearchRadiusDeg = 5, ScaleArcsecPerPixel = 1.5
        });
        Assert.That(args, Does.Contain("--ra 180"));     // 12h * 15 = 180°
        Assert.That(args, Does.Contain("--dec -30"));
        Assert.That(args, Does.Contain("--radius 5"));
        Assert.That(args, Does.Contain("--scale-low 1.200"));
        Assert.That(args, Does.Contain("--scale-high 1.800"));
    }

    // --- Solver capability flags ---

    [Test]
    public void PlateSolve3_DoesNotSupportBlindSolve() {
        var config = new ConfigurationBuilder().Build();
        var solver = new PlateSolve3Solver(config, new Mock<ILogger<PlateSolve3Solver>>().Object);
        Assert.That(solver.SupportsBlindSolve, Is.False);
    }

    [Test]
    public void AstapAndAstrometryNet_SupportBlindSolve() {
        var config = new ConfigurationBuilder().Build();
        Assert.That(new AstapSolver(config, new Mock<ILogger<AstapSolver>>().Object).SupportsBlindSolve, Is.True);
        Assert.That(new AstrometryNetOnlineSolver(config, new Mock<ILogger<AstrometryNetOnlineSolver>>().Object).SupportsBlindSolve, Is.True);
        Assert.That(new AstrometryNetLocalSolver(config, new Mock<ILogger<AstrometryNetLocalSolver>>().Object).SupportsBlindSolve, Is.True);
    }
}
