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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;
using NUnit.Framework;
using Moq;
using NINA.Core.Enum;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

[TestFixture]
public class PlateSolveFallbackRegressionTests {
    private sealed class Solver(string id, Func<PlateSolveOptions, CancellationToken, PlateSolveResult> solve) : IPlateSolver {
        public string Id => id;
        public string DisplayName => id;
        public bool IsAvailable => true;
        public bool SupportsBlindSolve => true;
        public List<PlateSolveOptions> Calls { get; } = new();
        public Task<PlateSolveResult> SolveAsync(string path, PlateSolveOptions options,
                CancellationToken ct = default, Action<string>? onLog = null) {
            Calls.Add(options);
            return Task.FromResult(solve(options, ct));
        }
    }

    private static PlateSolveResult Solved() => new() {
        Success = true, SolverUsed = "regression", RaHours = 12, RaDeg = 180, DecDeg = 45
    };

    private static PlateSolveService Service(Solver primary, Solver blind, bool enabled = true) {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["PlateSolve:PrimarySolver"] = primary.Id,
            ["PlateSolve:BlindSolver"] = blind.Id,
            ["PlateSolve:UseBlindFallback"] = enabled.ToString()
        }).Build();
        var plugins = new PlateSolverRegistry();
        Assert.That(plugins.Add(primary, "regression"), Is.Null);
        if (!ReferenceEquals(primary, blind)) Assert.That(plugins.Add(blind, "regression"), Is.Null);
        return new(cfg, NullLogger<PlateSolveService>.Instance,
            new AstapSolver(cfg, NullLogger<AstapSolver>.Instance),
            new PlateSolve3Solver(cfg, NullLogger<PlateSolve3Solver>.Instance),
            new AstrometryNetOnlineSolver(cfg, NullLogger<AstrometryNetOnlineSolver>.Instance),
            new AstrometryNetLocalSolver(cfg, NullLogger<AstrometryNetLocalSolver>.Instance),
            null, plugins: plugins);
    }

    [Test]
    public async Task CrossBackendFallback_RemovesPositionOnly_WithoutMutatingCaller() {
        var primary = new Solver("failed-primary", (_, _) => PlateSolveResult.Failed("bad position"));
        var blind = new Solver("blind-backend", (o, _) => o.HintRa == null && o.HintDec == null
            ? Solved() : PlateSolveResult.Failed("position still constrained"));
        var service = Service(primary, blind);
        var context = new CaptureWcsContext(Mock.Of<ICamera>(), Mock.Of<ITelescope>(),
            "rig", 500, 3.76, 3.76, 40, 30, 40, 30, 1, 1, PierSide.pierEast, null, null, false);
        var options = new PlateSolveOptions {
            HintRa = 0, HintDec = 0, SearchRadiusDeg = 1,
            FovDeg = 2, ScaleArcsecPerPixel = 3.2, Downsample = 4, DownsampleIsExplicit = true,
            CaptureContext = context
        };
        var result = await service.SolveAsync("frame.fits", options);
        Assert.That(result.Success, Is.True, result.Error);
        var retry = blind.Calls.Single();
        Assert.Multiple(() => {
            Assert.That(retry, Is.Not.SameAs(options));
            Assert.That(retry.HintRa, Is.Null);
            Assert.That(retry.HintDec, Is.Null);
            Assert.That(retry.SearchRadiusDeg, Is.EqualTo(180));
            Assert.That(options.HintRa, Is.EqualTo(0));
            Assert.That(options.HintDec, Is.EqualTo(0));
            Assert.That(options.SearchRadiusDeg, Is.EqualTo(1));
            Assert.That(service.LastSuccessfulSolve?.RaHours, Is.EqualTo(12));
        });
        foreach (var p in typeof(PlateSolveOptions).GetProperties(
                     System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                     | System.Reflection.BindingFlags.NonPublic)) {
            if (p.Name is nameof(PlateSolveOptions.HintRa) or nameof(PlateSolveOptions.HintDec)
                    or nameof(PlateSolveOptions.SearchRadiusDeg)) continue;
            Assert.That(p.GetValue(retry), Is.EqualTo(p.GetValue(options)), p.Name + " must be retained");
        }
    }

    [Test]
    public async Task SameBackend_RetriesHintedFailureWithoutPositionHint() {
        var backend = new Solver("same-backend", (o, _) => o.HintRa.HasValue
            ? PlateSolveResult.Failed("wrong pointing") : Solved());
        var result = await Service(backend, backend).SolveAsync("frame.fits",
            new PlateSolveOptions { HintRa = 0, HintDec = 0, SearchRadiusDeg = 1 });
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(backend.Calls, Has.Count.EqualTo(2));
    }

    [TestCase(null, null)]
    [TestCase(0.0, null)]
    [TestCase(null, 0.0)]
    public async Task SameBackend_AlreadyBlind_DoesNotRepeatIdenticalAttempt(double? ra, double? dec) {
        var backend = new Solver("already-blind", (_, _) => PlateSolveResult.Failed("no match"));
        var result = await Service(backend, backend).SolveAsync("frame.fits", new PlateSolveOptions { HintRa = ra, HintDec = dec });
        Assert.That(result.Success, Is.False);
        Assert.That(backend.Calls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DisabledFallback_DoesNotRetry() {
        var primary = new Solver("failed-primary", (_, _) => PlateSolveResult.Failed("no match"));
        var blind = new Solver("blind-backend", (_, _) => Solved());
        var result = await Service(primary, blind, enabled: false).SolveAsync("frame.fits",
            new PlateSolveOptions { HintRa = 0, HintDec = 0 });
        Assert.That(result.Success, Is.False);
        Assert.That(blind.Calls, Is.Empty);
    }

    [Test]
    public void CancelAfterPrimary_DoesNotStartFallback() {
        using var cts = new CancellationTokenSource();
        var primary = new Solver("failed-primary", (_, _) => { cts.Cancel(); return PlateSolveResult.Failed("no match"); });
        var blind = new Solver("blind-backend", (_, _) => Solved());
        Assert.ThrowsAsync<OperationCanceledException>(async () => await Service(primary, blind)
            .SolveAsync("frame.fits", new PlateSolveOptions { HintRa = 0, HintDec = 0 }, cts.Token));
        Assert.That(blind.Calls, Is.Empty);
    }
    [Test]
    public async Task SameBackend_Astap_DoesNotRepeatItsInternalBlindRetry() {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Uses a POSIX shell script as a fake ASTAP executable.");
        var dir = Path.Combine(Path.GetTempPath(), "astap-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var path = Path.Combine(dir, "fake-astap");
            var calls = Path.Combine(dir, "calls");
            var fits = Path.Combine(dir, "frame.fits");
            File.WriteAllText(fits, "invalid FITS exercises the normal solve path");
            File.WriteAllText(path, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> '" + calls + "'\nexit 1\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["PlateSolve:AstapPath"] = path,
                ["PlateSolve:PrimarySolver"] = "astap",
                ["PlateSolve:BlindSolver"] = "astap"
            }).Build();
            var options = new PlateSolveOptions { HintRa = 0, HintDec = 0, SearchRadiusDeg = 1, FovDeg = 2 };
            var astap = new AstapSolver(cfg, NullLogger<AstapSolver>.Instance);
            Assert.That((await astap.SolveAsync(fits, options)).Success, Is.False);
            var ownAttempts = File.ReadAllLines(calls).Length;
            Assert.That(ownAttempts, Is.GreaterThan(1), "the backend must have tried its own blind recovery");
            File.WriteAllText(calls, "");
            var service = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance);
            Assert.That((await service.SolveAsync(fits, options)).Success, Is.False);
            Assert.That(File.ReadAllLines(calls), Has.Length.EqualTo(ownAttempts));
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>ASTAP as the blind fallback keeps the hint: it tries the hint
    /// first and the whole sky after, so the fallback still gets the fast
    /// hinted pass when the primary failed for another reason.</summary>
    [Test]
    public async Task CrossBackendFallback_ToAstap_KeepsTheHint() {
        var dir = Path.Combine(Path.GetTempPath(), "astap-blind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var calls = Path.Combine(dir, "calls");
            var fits = Path.Combine(dir, "frame.fits");
            File.WriteAllText(fits, "invalid FITS exercises the normal solve path");
            string path;
            if (OperatingSystem.IsWindows()) {
                path = Path.Combine(dir, "fake-astap.cmd");
                File.WriteAllText(path, "@echo %* >> \"" + calls + "\"\r\n@exit /b 1\r\n");
            } else {
                path = Path.Combine(dir, "fake-astap");
                File.WriteAllText(path, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> '" + calls + "'\nexit 1\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var primary = new Solver("failed-primary", (_, _) => PlateSolveResult.Failed("no index for this scale"));
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["PlateSolve:AstapPath"] = path,
                ["PlateSolve:PrimarySolver"] = primary.Id,
                ["PlateSolve:BlindSolver"] = "astap",
                ["PlateSolve:UseBlindFallback"] = "true"
            }).Build();
            var plugins = new PlateSolverRegistry();
            Assert.That(plugins.Add(primary, "regression"), Is.Null);
            var service = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance,
                new AstapSolver(cfg, NullLogger<AstapSolver>.Instance),
                new PlateSolve3Solver(cfg, NullLogger<PlateSolve3Solver>.Instance),
                new AstrometryNetOnlineSolver(cfg, NullLogger<AstrometryNetOnlineSolver>.Instance),
                new AstrometryNetLocalSolver(cfg, NullLogger<AstrometryNetLocalSolver>.Instance),
                null, plugins: plugins);

            var options = new PlateSolveOptions { HintRa = 5.5, HintDec = -5, SearchRadiusDeg = 10, FovDeg = 2 };
            Assert.That((await service.SolveAsync(fits, options)).Success, Is.False);

            var first = File.ReadAllLines(calls).First();
            Assert.That(first, Does.Contain("-ra 5.5"), "the first ASTAP pass must use the hint");
            Assert.That(first, Does.Contain("-r 10"));
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

}
