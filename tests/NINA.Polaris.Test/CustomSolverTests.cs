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

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Test;

[TestFixture]
public class CustomSolverTests {
    private string _dir = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-custom-solver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static IConfiguration Config(Dictionary<string, string?>? extra = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(extra ?? new()).Build();

    private ProfileService Profiles() => new(Config(new() { ["Profiles:Directory"] = _dir }),
        NullLogger<ProfileService>.Instance);

    private static Dictionary<string, string?> Values(PlateSolveOptions o) =>
        CustomCommandSolver.Values("/frames/my frame.fits", "/tmp/out.wcs", o, 180);

    // --- argument template ---

    [Test]
    public void PathsWithSpacesStayOneArgument() {
        var args = CustomCommandSolver.BuildArguments("solve {image} --wcs {wcs}",
            Values(new PlateSolveOptions()), out var error);
        Assert.That(error, Is.Null);
        Assert.That(args, Is.EqualTo(new[] { "solve", "/frames/my frame.fits", "--wcs", "/tmp/out.wcs" }));
    }

    [Test]
    public void HintGroupDropsOutOfABlindSolve() {
        const string t = "solve {image} [--ra {ra} --dec {dec} --radius {radius}] --wcs {wcs}";
        var blind = CustomCommandSolver.BuildArguments(t, Values(new PlateSolveOptions()), out _);
        Assert.That(blind, Is.EqualTo(new[] { "solve", "/frames/my frame.fits", "--wcs", "/tmp/out.wcs" }));

        var hinted = CustomCommandSolver.BuildArguments(t,
            Values(new PlateSolveOptions { HintRa = 5.5, HintDec = -5.25, SearchRadiusDeg = 10 }), out _);
        Assert.That(hinted, Is.EqualTo(new[] {
            "solve", "/frames/my frame.fits", "--ra", "82.5", "--dec", "-5.25", "--radius", "10", "--wcs", "/tmp/out.wcs" }));
    }

    [Test]
    public void QuotedTextIsOneArgument() {
        var args = CustomCommandSolver.BuildArguments("solve {image} --index \"/opt/sky shot/index\" --wcs {wcs}",
            Values(new PlateSolveOptions()), out _);
        Assert.That(args![3], Is.EqualTo("/opt/sky shot/index"));
    }

    [Test]
    public void AHintOutsideAGroupFailsWithItsName() {
        var args = CustomCommandSolver.BuildArguments("solve {image} --fov {fov} --wcs {wcs}",
            Values(new PlateSolveOptions()), out var error);
        Assert.That(args, Is.Null);
        Assert.That(error, Does.Contain("{fov}"));
    }

    [Test]
    public void FovRangeComesFromTheEstimate() {
        var args = CustomCommandSolver.BuildArguments("solve {image} [--min-fov {fov_min} --max-fov {fov_max}] --wcs {wcs}",
            Values(new PlateSolveOptions { FovDeg = 2 }), out _);
        Assert.That(args, Is.EqualTo(new[] {
            "solve", "/frames/my frame.fits", "--min-fov", "1.4", "--max-fov", "2.6", "--wcs", "/tmp/out.wcs" }));
    }

    // --- validation ---

    [Test]
    public void ValidationNamesWhatIsWrong() {
        var exe = Environment.ProcessPath!;
        Assert.Multiple(() => {
            Assert.That(CustomCommandSolver.Validate(exe, "solve {image} --wcs {wcs}"), Is.Null);
            Assert.That(CustomCommandSolver.Validate("", "solve {image} --wcs {wcs}"), Does.Contain("No executable"));
            Assert.That(CustomCommandSolver.Validate(Path.Combine(_dir, "missing"), "{image} {wcs}"), Does.Contain("does not exist"));
            Assert.That(CustomCommandSolver.Validate("no-such-solver-on-path", "{image} {wcs}"), Does.Contain("PATH"));
            Assert.That(CustomCommandSolver.Validate(exe, "solve {image}"), Does.Contain("{wcs}"));
            Assert.That(CustomCommandSolver.Validate(exe, "solve [{image}] {wcs}"), Does.Contain("{image}"));
            Assert.That(CustomCommandSolver.Validate(exe, "{image} {wcs} --x {ra_deg}"), Does.Contain("{ra_deg}"));
            Assert.That(CustomCommandSolver.Validate(exe, "{image} {wcs} [--ra {ra}"), Does.Contain("matching ]"));
        });
    }

    [Test]
    public void UnconfiguredSolverIsNotAvailable() {
        var solver = new CustomCommandSolver(Config(), NullLogger<CustomCommandSolver>.Instance, Profiles());
        Assert.That(solver.IsAvailable, Is.False);
        Assert.That(solver.DisplayName, Is.EqualTo("Custom solver"));
        Assert.That(solver.SupportsBlindSolve, Is.False);
    }

    // --- WCS output ---

    private static string Cards(params string[] cards) {
        var sb = new StringBuilder();
        foreach (var c in cards.Append("END")) sb.Append(c.PadRight(80));
        return sb.ToString();
    }

    [Test]
    public void FitsCardsWithCentredReferenceGiveCrvalAsCentre() {
        var text = Cards("CRVAL1  =         83.8221 / RA", "CRVAL2  =         -5.3911",
            "CRPIX1  =            50.5", "CRPIX2  =            40.5",
            "CD1_1   =  -0.000277777778", "CD1_2   =               0.0",
            "CD2_1   =               0.0", "CD2_2   =   0.000277777778");
        var r = CustomCommandSolver.ParseWcs(text, 100, 80)!;
        Assert.Multiple(() => {
            Assert.That(r.Success, Is.True);
            Assert.That(r.RaDeg, Is.EqualTo(83.8221).Within(1e-9));
            Assert.That(r.RaHours, Is.EqualTo(83.8221 / 15).Within(1e-9));
            Assert.That(r.DecDeg, Is.EqualTo(-5.3911).Within(1e-9));
            Assert.That(r.ScaleArcsecPerPixel, Is.EqualTo(1.0).Within(1e-6));
            Assert.That(r.HasCdMatrix, Is.True);
            Assert.That(r.CrPix1, Is.EqualTo(50.5));
        });
    }

    [Test]
    public void TextHeaderWithNewlinesIsRead() {
        var text = "CRVAL1  = 10.0\nCRVAL2  = 20.0\nCDELT1  = -0.001\nCDELT2  = 0.001\nCROTA2  = 0.0\nEND\n";
        var r = CustomCommandSolver.ParseWcs(text, 0, 0)!;
        Assert.That(r.RaDeg, Is.EqualTo(10.0).Within(1e-9));
        Assert.That(r.ScaleArcsecPerPixel, Is.EqualTo(3.6).Within(1e-6));
        Assert.That(r.CD11, Is.EqualTo(-0.001).Within(1e-12));
    }

    [Test]
    public void JsonWithTheSameKeywordsIsRead() {
        var json = """{"solved":true,"wcs":{"CRVAL1":200.5,"CRVAL2":"45.25","CRPIX1":50.5,"CRPIX2":40.5,"CD1_1":-0.0005,"CD1_2":0,"CD2_1":0,"CD2_2":0.0005}}""";
        var r = CustomCommandSolver.ParseWcs(json, 100, 80)!;
        Assert.That(r.RaDeg, Is.EqualTo(200.5).Within(1e-9));
        Assert.That(r.DecDeg, Is.EqualTo(45.25).Within(1e-9));
        Assert.That(r.ScaleArcsecPerPixel, Is.EqualTo(1.8).Within(1e-6));
    }

    [Test]
    public void OffCentreReferencePixelIsProjectedToTheFieldCentre() {
        // Reference at the corner pixel; the centre is 500 px away on both
        // axes at 3.6"/px, so 0.5 deg west and 0.5 deg north of CRVAL.
        var text = Cards("CRVAL1  = 10.0", "CRVAL2  = 0.0", "CRPIX1  = 1.0", "CRPIX2  = 1.0",
            "CD1_1   = -0.001", "CD1_2   = 0.0", "CD2_1   = 0.0", "CD2_2   = 0.001");
        var r = CustomCommandSolver.ParseWcs(text, 1001, 1001)!;
        var (ra, dec) = CustomCommandSolver.TanToSky(10, 0, -0.5, 0.5);
        Assert.Multiple(() => {
            Assert.That(r.RaDeg, Is.EqualTo(ra).Within(1e-9));
            Assert.That(r.DecDeg, Is.EqualTo(dec).Within(1e-9));
            Assert.That(r.RaDeg, Is.EqualTo(9.5).Within(0.01));
            Assert.That(r.DecDeg, Is.EqualTo(0.5).Within(0.01));
            Assert.That(r.CrPix1, Is.EqualTo(501));
            Assert.That(r.CrPix2, Is.EqualTo(501));
        });
    }

    [Test]
    public void NoSolutionInTheFileIsNull() {
        Assert.That(CustomCommandSolver.ParseWcs(Cards("SIMPLE  = T"), 100, 80), Is.Null);
        Assert.That(CustomCommandSolver.ParseWcs(Cards("CRVAL1  = 1.0", "CRVAL2  = 2.0"), 100, 80), Is.Null, "no scale");
        Assert.That(CustomCommandSolver.ParseWcs("{\"solved\":false}", 100, 80), Is.Null);
    }

    // --- running a solver ---

    private string WriteFits() {
        var path = Path.Combine(_dir, "frame.fits");
        var header = Cards("SIMPLE  =                    T", "BITPIX  =                   16",
            "NAXIS   =                    2", "NAXIS1  =                  100", "NAXIS2  =                   80");
        File.WriteAllText(path, header.PadRight(2880), Encoding.ASCII);
        return path;
    }

    /// <summary>A stand-in solver: copies a prepared WCS file to the path in
    /// its second argument, or exits 1 when <paramref name="fail"/>.</summary>
    private string WriteScript(bool fail) {
        var wcs = Path.Combine(_dir, "solution.wcs");
        File.WriteAllText(wcs, Cards("CRVAL1  = 83.8221", "CRVAL2  = -5.3911", "CRPIX1  = 50.5", "CRPIX2  = 40.5",
            "CD1_1   = -0.000277777778", "CD1_2   = 0.0", "CD2_1   = 0.0", "CD2_2   = 0.000277777778"));
        if (OperatingSystem.IsWindows()) {
            var script = Path.Combine(_dir, "solver.cmd");
            File.WriteAllText(script, fail ? "@echo no match 1>&2\r\n@exit /b 1\r\n" : $"@copy /y \"{wcs}\" %2 >nul\r\n");
            return script;
        } else {
            var script = Path.Combine(_dir, "solver.sh");
            File.WriteAllText(script, fail ? "#!/bin/sh\necho no match >&2\nexit 1\n" : $"#!/bin/sh\ncp \"{wcs}\" \"$2\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return script;
        }
    }

    private CustomCommandSolver Solver(string exe, out ProfileService profiles) {
        profiles = Profiles();
        profiles.UpdateSettings(p => {
            p.CustomSolverName = "Test solver";
            p.CustomSolverPath = exe;
            p.CustomSolverArgs = "{image} {wcs}";
        });
        return new CustomCommandSolver(Config(), NullLogger<CustomCommandSolver>.Instance, profiles);
    }

    [Test]
    public async Task ASolverThatWritesAWcsSolves() {
        var solver = Solver(WriteScript(fail: false), out _);
        Assert.That(solver.Validate(), Is.Null);
        var r = await solver.SolveAsync(WriteFits(), new PlateSolveOptions());
        Assert.That(r.Success, Is.True, r.Error + "\n" + r.Output);
        Assert.That(r.RaDeg, Is.EqualTo(83.8221).Within(1e-9));
        Assert.That(r.SolverUsed, Is.EqualTo("custom"));
        Assert.That(Directory.GetFiles(_dir, "*.custom-*.wcs"), Is.Empty, "the solution file is cleaned up");
    }

    [Test]
    public async Task ANonZeroExitFailsWithTheLastErrorLine() {
        var solver = Solver(WriteScript(fail: true), out _);
        var r = await solver.SolveAsync(WriteFits(), new PlateSolveOptions());
        Assert.That(r.Success, Is.False);
        Assert.That(r.Error, Does.Contain("Test solver").And.Contain("exit 1").And.Contain("no match"));
    }

    // --- plugin solvers ---

    private sealed class FakeSolver(string id, bool blind = true) : IPlateSolver {
        public string Id => id;
        public string DisplayName => "Fake " + id;
        public bool IsAvailable => true;
        public bool SupportsBlindSolve => blind;
        public Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options,
            CancellationToken ct = default, Action<string>? onLog = null) =>
            Task.FromResult(new PlateSolveResult { Success = true, SolverUsed = id, RaHours = 1, RaDeg = 15 });
    }

    [Test]
    public void RegistryRefusesBuiltInAndDuplicateIds() {
        var reg = new PlateSolverRegistry();
        Assert.Multiple(() => {
            Assert.That(reg.Add(new FakeSolver("astap"), "plugin A"), Does.Contain("built-in"));
            Assert.That(reg.Add(new FakeSolver("custom"), "plugin A"), Does.Contain("built-in"));
            Assert.That(reg.Add(new FakeSolver("skyshot"), "plugin A"), Is.Null);
            Assert.That(reg.Add(new FakeSolver("SkyShot"), "plugin B"), Does.Contain("plugin A"));
            Assert.That(reg.Solvers.Select(s => s.Id), Is.EqualTo(new[] { "skyshot" }));
        });
    }

    private static PlateSolveService Dispatcher(IConfiguration cfg, PlateSolverRegistry reg) => new(cfg,
        NullLogger<PlateSolveService>.Instance,
        new AstapSolver(cfg, NullLogger<AstapSolver>.Instance),
        new PlateSolve3Solver(cfg, NullLogger<PlateSolve3Solver>.Instance),
        new AstrometryNetOnlineSolver(cfg, NullLogger<AstrometryNetOnlineSolver>.Instance),
        new AstrometryNetLocalSolver(cfg, NullLogger<AstrometryNetLocalSolver>.Instance),
        null, null, null, reg);

    [Test]
    public async Task APluginSolverCanBeThePrimaryAndTheBlindFallback() {
        var reg = new PlateSolverRegistry();
        reg.Add(new FakeSolver("skyshot"), "plugin A");
        var cfg = Config(new() { ["PlateSolve:PrimarySolver"] = "skyshot", ["PlateSolve:BlindSolver"] = "skyshot" });
        var svc = Dispatcher(cfg, reg);
        Assert.That(svc.AllSolvers.Select(s => s.Id), Does.Contain("skyshot"));
        Assert.That(svc.PrimarySolver.Id, Is.EqualTo("skyshot"));
        Assert.That(svc.BlindSolver?.Id, Is.EqualTo("skyshot"));
        var fits = Path.Combine(_dir, "x.fits");
        File.WriteAllText(fits, "");
        var r = await svc.SolveAsync(fits, new PlateSolveOptions());
        Assert.That(r.SolverUsed, Is.EqualTo("skyshot"));
    }

    [Test]
    public void AnUnknownPrimaryFallsBackToAstap() {
        var svc = Dispatcher(Config(new() { ["PlateSolve:PrimarySolver"] = "gone" }), new PlateSolverRegistry());
        Assert.That(svc.PrimarySolver.Id, Is.EqualTo("astap"));
    }

    [Test]
    public void TheBlindSolverChoiceIsStoredInTheProfile() {
        var reg = new PlateSolverRegistry();
        reg.Add(new FakeSolver("skyshot"), "plugin A");
        reg.Add(new FakeSolver("hinted-only", blind: false), "plugin A");
        var cfg = Config();
        var profiles = Profiles();
        var svc = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance,
            new AstapSolver(cfg, NullLogger<AstapSolver>.Instance),
            new PlateSolve3Solver(cfg, NullLogger<PlateSolve3Solver>.Instance),
            new AstrometryNetOnlineSolver(cfg, NullLogger<AstrometryNetOnlineSolver>.Instance),
            new AstrometryNetLocalSolver(cfg, NullLogger<AstrometryNetLocalSolver>.Instance),
            null, profiles, null, reg);
        Assert.That(svc.BlindSolver?.Id, Is.EqualTo("astrometry-net-online"), "default");
        profiles.UpdateSettings(p => p.PlateSolveBlindSolver = "skyshot");
        Assert.That(svc.BlindSolver?.Id, Is.EqualTo("skyshot"));
        profiles.UpdateSettings(p => p.PlateSolveBlindSolver = "hinted-only");
        Assert.That(svc.BlindSolver, Is.Null, "a solver that needs a hint is never the blind fallback");
    }
}
