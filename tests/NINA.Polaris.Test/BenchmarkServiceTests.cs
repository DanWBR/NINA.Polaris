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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Unit tests for the synthetic benchmark workloads + the results store.
/// The workload methods are internal static so they can run on a small
/// frame with a tiny time budget here without spinning up the full
/// service graph. Determinism of the synthetic frame generator is the
/// load-bearing property: it is what makes the score comparable across
/// machines.
/// </summary>
[TestFixture]
public class BenchmarkServiceTests {

    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void Cleanup() {
        foreach (var d in _tempDirs) {
            try { Directory.Delete(d, true); } catch { }
        }
        _tempDirs.Clear();
    }

    // ----- synthetic frame generator -----

    [Test]
    public void GenerateStarField_SameSeed_IsDeterministic() {
        var a = BenchmarkService.GenerateStarField(256, 256, 0x5EED, 0, 0);
        var b = BenchmarkService.GenerateStarField(256, 256, 0x5EED, 0, 0);
        Assert.That(a.Length, Is.EqualTo(256 * 256));
        Assert.That(a, Is.EqualTo(b), "same seed must yield identical pixels");
    }

    [Test]
    public void GenerateStarField_DifferentShift_DiffersButHasStars() {
        var a = BenchmarkService.GenerateStarField(256, 256, 0x5EED, 0, 0);
        var b = BenchmarkService.GenerateStarField(256, 256, 0x5EED, 7, -5);
        Assert.That(a, Is.Not.EqualTo(b), "a shifted field must differ");
        // The field must actually contain bright stars above the
        // background, otherwise the stacking workload has nothing to do.
        ushort max = 0;
        foreach (var v in a) if (v > max) max = v;
        Assert.That(max, Is.GreaterThan(2000));
    }

    // ----- workloads return positive, finite metrics -----

    [Test]
    public void StackingWorkload_ProducesPositiveThroughput() {
        var r = BenchmarkService.RunStackingWorkload(
            512, 512, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(r.Iterations, Is.GreaterThanOrEqualTo(2));
        Assert.That(r.Fps, Is.GreaterThan(0));
        Assert.That(r.MpxPerSec, Is.GreaterThan(0));
        Assert.That(r.TotalMs, Is.GreaterThan(0));
        Assert.That(r.StarCount, Is.GreaterThan(0), "synthetic field must yield detectable stars");
        Assert.That(double.IsFinite(r.Fps));
    }

    [Test]
    public void EncodeWorkload_ProducesPositiveThroughput() {
        var r = BenchmarkService.RunEncodeWorkload(
            512, 512, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(r.Iterations, Is.GreaterThanOrEqualTo(2));
        Assert.That(r.Fps, Is.GreaterThan(0));
        Assert.That(r.MpxPerSec, Is.GreaterThan(0));
        Assert.That(r.Lz4MBps, Is.GreaterThan(0));
        Assert.That(double.IsFinite(r.Fps));
    }

    [Test]
    public void CpuWorkload_ProducesPositiveScores() {
        var r = BenchmarkService.RunCpuWorkload(CancellationToken.None);
        Assert.That(r.Cores, Is.GreaterThanOrEqualTo(1));
        Assert.That(r.SingleThreadMflops, Is.GreaterThan(0));
        Assert.That(r.MultiThreadMflops, Is.GreaterThan(0));
        Assert.That(r.MemBandwidthGBps, Is.GreaterThan(0));
        Assert.That(r.CoreScaling, Is.GreaterThan(0));
    }

    // ----- GPU overall speedup (geometric mean) -----

    [Test]
    public void GpuOverallSpeedup_uses_geometric_mean_not_arithmetic() {
        // The measured RTX 5070 numbers: two ops slower (<1x), one big win.
        var perOp = new[] { 0.47, 0.40, 16.17 };
        var geo = BenchmarkService.GpuOverallSpeedup(perOp);
        // Geometric mean = (0.47*0.40*16.17)^(1/3) ≈ 1.45 — honest, vs the old
        // arithmetic mean ≈ 5.68 which the single blur win inflated.
        Assert.That(geo, Is.EqualTo(1.45).Within(0.01));
        Assert.That(geo, Is.LessThan(perOp.Average()),
            "geometric mean must not be dominated by the one large win");
    }

    [Test]
    public void GpuOverallSpeedup_ignores_ops_that_did_not_run() {
        // Zeros mean "declined / not measured" and must not drag the figure to 0.
        var geo = BenchmarkService.GpuOverallSpeedup(new[] { 0.0, 4.0, 9.0 });
        Assert.That(geo, Is.EqualTo(6.0).Within(0.01)); // sqrt(4*9)
    }

    [Test]
    public void GpuOverallSpeedup_is_zero_when_nothing_ran() {
        Assert.That(BenchmarkService.GpuOverallSpeedup(new[] { 0.0, 0.0 }), Is.EqualTo(0));
        Assert.That(BenchmarkService.GpuOverallSpeedup(System.Array.Empty<double>()), Is.EqualTo(0));
    }

    [Test]
    public void GpuOverallSpeedup_of_equal_ratios_returns_that_ratio() {
        Assert.That(BenchmarkService.GpuOverallSpeedup(new[] { 2.0, 2.0, 2.0 }),
            Is.EqualTo(2.0).Within(0.001));
    }

    // ----- results store round-trip -----

    private BenchmarkResultsStore NewStore() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-bench-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = dir
            })
            .Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        return new BenchmarkResultsStore(profiles, NullLogger<BenchmarkResultsStore>.Instance);
    }

    private static BenchmarkResult SampleResult(double score) => new(
        Timestamp: DateTime.UtcNow.ToString("o"),
        Device: new BenchmarkDevice("raspberry-pi", "Raspberry Pi 5", "Linux", "arm64", 4, "Raspberry Pi 5", "ARM Cortex-A76", null),
        FrameWidth: 4096, FrameHeight: 4096, Megapixels: 16.78,
        Stacking: new StackingResult(10, 1, 20, 5, 36, 27.7, 465, 6, 400),
        Encode: new EncodeResult(15, 25, 8, 48, 20.8, 349, 1000, 6),
        Cpu: new CpuResult(1200, 4200, 3.5, 5.2, 4),
        CompositeScore: score,
        Camera: null);

    [Test]
    public async Task Store_SaveLoad_RoundTrips() {
        var store = NewStore();
        await store.SaveResultAsync(SampleResult(101.5));
        var history = store.LoadHistory();
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.That(history[0].CompositeScore, Is.EqualTo(101.5));
        Assert.That(history[0].Device.Model, Is.EqualTo("Raspberry Pi 5"));
        Assert.That(history[0].Stacking.Fps, Is.EqualTo(27.7));
    }

    [Test]
    public async Task Store_Export_IsValidJsonArray() {
        var store = NewStore();
        await store.SaveResultAsync(SampleResult(50));
        await store.SaveResultAsync(SampleResult(60));
        var bytes = store.ExportAllJson();
        var parsed = JsonSerializer.Deserialize<List<BenchmarkResult>>(bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Store_Clear_RemovesAll() {
        var store = NewStore();
        await store.SaveResultAsync(SampleResult(1));
        await store.SaveResultAsync(SampleResult(2));
        var cleared = store.ClearHistory();
        Assert.That(cleared, Is.EqualTo(2));
        Assert.That(store.LoadHistory(), Is.Empty);
    }
}