using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using NINA.Headless.Services;

namespace NINA.Headless.Test;

[TestFixture]
public class PlateSolveServiceTests {
    private Mock<ILogger<PlateSolveService>> _loggerMock = null!;
    private string _tempDir = null!;

    [SetUp]
    public void SetUp() {
        _loggerMock = new Mock<ILogger<PlateSolveService>>();
        _tempDir = Path.Combine(Path.GetTempPath(), "NinaHeadlessTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() {
        try {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        } catch { }
    }

    private PlateSolveService CreateService(string? astapPath = null) {
        var configValues = new Dictionary<string, string?>();
        if (astapPath != null)
            configValues["PlateSolve:AstapPath"] = astapPath;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new PlateSolveService(config, _loggerMock.Object);
    }

    // --- ParseIniResult tests (via reflection since it's private) ---

    private PlateSolveResult InvokeParseIniResult(PlateSolveService service, string fitsPath) {
        var method = typeof(PlateSolveService).GetMethod("ParseIniResult",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, "ParseIniResult method should exist");
        return (PlateSolveResult)method!.Invoke(service, [fitsPath])!;
    }

    [Test]
    public void ParseIniResult_Success_ExtractsValues() {
        var service = CreateService("/nonexistent/astap");

        var fitsPath = Path.Combine(_tempDir, "test.fits");
        var iniPath = Path.ChangeExtension(fitsPath, ".ini");

        File.WriteAllText(iniPath,
            """
            PLTSOLVD=T
            CRVAL1=180.5
            CRVAL2=45.25
            CDELT1=-0.000556
            CROTA1=1.5
            """);

        var result = InvokeParseIniResult(service, fitsPath);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RaDeg, Is.EqualTo(180.5).Within(0.001));
        Assert.That(result.RaHours, Is.EqualTo(180.5 / 15.0).Within(0.001));
        Assert.That(result.DecDeg, Is.EqualTo(45.25).Within(0.001));
        Assert.That(result.ScaleArcsecPerPixel, Is.EqualTo(0.000556 * 3600).Within(0.01));
        Assert.That(result.RotationDeg, Is.EqualTo(1.5).Within(0.001));
    }

    [Test]
    public void ParseIniResult_Failed_ReturnsFalse() {
        var service = CreateService("/nonexistent/astap");

        var fitsPath = Path.Combine(_tempDir, "failed.fits");
        var iniPath = Path.ChangeExtension(fitsPath, ".ini");

        File.WriteAllText(iniPath,
            """
            PLTSOLVD=F
            """);

        var result = InvokeParseIniResult(service, fitsPath);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("did not converge"));
    }

    [Test]
    public void ParseIniResult_MissingIniFile_ReturnsFailed() {
        var service = CreateService("/nonexistent/astap");

        var fitsPath = Path.Combine(_tempDir, "missing.fits");
        // Do not create the .ini file

        var result = InvokeParseIniResult(service, fitsPath);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("not found"));
    }

    [Test]
    public void ParseIniResult_PartialValues_ParsesAvailable() {
        var service = CreateService("/nonexistent/astap");

        var fitsPath = Path.Combine(_tempDir, "partial.fits");
        var iniPath = Path.ChangeExtension(fitsPath, ".ini");

        // Only RA, no Dec, no scale, no rotation
        File.WriteAllText(iniPath,
            """
            PLTSOLVD=T
            CRVAL1=90.0
            """);

        var result = InvokeParseIniResult(service, fitsPath);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RaDeg, Is.EqualTo(90.0).Within(0.001));
        Assert.That(result.DecDeg, Is.EqualTo(0.0), "Missing Dec should default to 0");
        Assert.That(result.ScaleArcsecPerPixel, Is.EqualTo(0.0), "Missing scale should default to 0");
    }

    // --- BuildArgs tests (via reflection since it's private) ---

    private string InvokeBuildArgs(PlateSolveService service, string fitsPath, PlateSolveOptions options) {
        var method = typeof(PlateSolveService).GetMethod("BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, "BuildArgs method should exist");
        return (string)method!.Invoke(service, [fitsPath, options])!;
    }

    [Test]
    public void BuildArgs_WithHints_IncludesRaSpd() {
        var service = CreateService("/nonexistent/astap");
        var options = new PlateSolveOptions {
            HintRa = 12.5,
            HintDec = 45.0,
            SearchRadiusDeg = 10
        };

        var args = InvokeBuildArgs(service, "/tmp/test.fits", options);

        Assert.That(args, Does.Contain("-ra 12.5"));
        Assert.That(args, Does.Contain("-spd 135")); // Dec 45 + 90 = 135
        Assert.That(args, Does.Contain("-r 10"));
    }

    [Test]
    public void BuildArgs_WithFov_IncludesFov() {
        var service = CreateService("/nonexistent/astap");
        var options = new PlateSolveOptions {
            FovDeg = 2.5,
            SearchRadiusDeg = 0 // No hint coordinates
        };

        var args = InvokeBuildArgs(service, "/tmp/test.fits", options);

        Assert.That(args, Does.Contain("-fov 2.5"));
    }

    [Test]
    public void BuildArgs_WithDownsample_IncludesZ() {
        var service = CreateService("/nonexistent/astap");
        var options = new PlateSolveOptions {
            Downsample = 4,
            SearchRadiusDeg = 0
        };

        var args = InvokeBuildArgs(service, "/tmp/test.fits", options);

        Assert.That(args, Does.Contain("-z 4"));
    }

    [Test]
    public void BuildArgs_AlwaysIncludesUpdate() {
        var service = CreateService("/nonexistent/astap");
        var options = new PlateSolveOptions { SearchRadiusDeg = 0 };

        var args = InvokeBuildArgs(service, "/tmp/test.fits", options);

        Assert.That(args, Does.Contain("-update"));
    }

    [Test]
    public void BuildArgs_WithoutHints_OmitsRaSpd() {
        var service = CreateService("/nonexistent/astap");
        var options = new PlateSolveOptions {
            HintRa = null,
            HintDec = null,
            SearchRadiusDeg = 10
        };

        var args = InvokeBuildArgs(service, "/tmp/test.fits", options);

        Assert.That(args, Does.Not.Contain("-ra"));
        Assert.That(args, Does.Not.Contain("-spd"));
    }

    // --- IsAvailable ---

    [Test]
    public void IsAvailable_WhenPathNotSet_ReturnsFalse() {
        // Default path unlikely to exist on CI/test machines
        var service = CreateService("/definitely/does/not/exist/astap_cli");

        Assert.That(service.IsAvailable, Is.False);
    }

    [Test]
    public void IsAvailable_WhenPathIsEmptyString_ReturnsFalse() {
        var service = CreateService("");

        Assert.That(service.IsAvailable, Is.False);
    }

    // --- SolveAsync guards ---

    [Test]
    public async Task SolveAsync_WhenNotAvailable_ReturnsFailed() {
        var service = CreateService("/not/a/real/path/astap_cli");
        var options = new PlateSolveOptions();

        var result = await service.SolveAsync("/tmp/test.fits", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("not found"));
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
    }
}
