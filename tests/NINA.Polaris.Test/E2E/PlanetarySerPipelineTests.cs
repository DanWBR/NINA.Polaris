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
using NUnit.Framework;
using System.Diagnostics;
using NINA.Polaris.Services;
using NINA.Polaris.Services.External;
using NINA.Polaris.Services.Planetary;
using NINA.Polaris.Services.Timelapse;

namespace NINA.Polaris.Test.E2E;

/// <summary>
/// Drives the real SER->MP4 conversion and the planetary stacker against a
/// folder of SER captures, in save order. Explicit + env-driven so it never
/// runs in a normal suite:
///   POLARIS_SER_DIR = folder of .ser files (required)
///   POLARIS_SER_MAX = how many, in name order, to process (default 1)
/// Outputs land in {POLARIS_SER_DIR}/polaris-test-out.
/// </summary>
[TestFixture, Category("E2E"),
 Explicit("Runs against POLARIS_SER_DIR, minutes per file")]
public class PlanetarySerPipelineTests {

    [Test]
    public async Task ConvertThenStack_InSaveOrder() {
        var dir = Environment.GetEnvironmentVariable("POLARIS_SER_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            Assert.Ignore("Set POLARIS_SER_DIR to a folder of .ser files");
        int max = int.TryParse(Environment.GetEnvironmentVariable("POLARIS_SER_MAX"), out var m) ? m : 1;
        bool stackOnly = Environment.GetEnvironmentVariable("POLARIS_SER_STACK_ONLY") is { Length: > 0 } so && so != "0";

        var files = Directory.GetFiles(dir!, "*.ser")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        Log($"{files.Count} SER files under {dir}; processing first {Math.Min(max, files.Count)} in save order");

        var outRoot = Path.Combine(dir!, "polaris-test-out");
        Directory.CreateDirectory(outRoot);

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Studio:Directory"] = Path.Combine(Path.GetTempPath(), "polaris-ser-" + Guid.NewGuid().ToString("N")[..8])
        }).Build();
        var profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        profile.Active.ImageOutputDir = outRoot;
        profile.ActiveEquipmentProfile.Name = "SER_Test";

        var ffmpeg = new FfmpegService(NullLogger<FfmpegService>.Instance);
        Log($"ffmpeg available: {ffmpeg.IsAvailable} ({ffmpeg.BinaryPath ?? "-"})");
        var encoder = new MediaEncodeService(ffmpeg, NullLogger<MediaEncodeService>.Instance);
        var stacker = new PlanetaryStackerService(profile, NullLogger<PlanetaryStackerService>.Instance);

        int n = Math.Min(max, files.Count);
        for (int i = 0; i < n; i++) {
            var ser = files[i];
            var name = Path.GetFileNameWithoutExtension(ser);
            Log($"");
            Log($"=== [{i + 1}/{n}] {Path.GetFileName(ser)} ({new FileInfo(ser).Length / 1e6:0} MB) ===");

            var sw = Stopwatch.StartNew();
            // 1) SER -> video (MP4 when ffmpeg is present, else GIF). Skippable.
            if (!stackOnly) {
                var fmt = ffmpeg.IsAvailable ? EncodeFormat.Mp4 : EncodeFormat.Gif;
                var encJob = encoder.StartJob(new SerFrameSource(new SerFileReader(ser)),
                    new EncodeConfig(OutputDir: outRoot, OutputName: name, Fps: 20, MaxDim: 1280, Format: fmt));
                var enc = await WaitDone(() => encoder.GetJob(encJob.Id),
                    j => j?.CompletedAt != null, TimeSpan.FromMinutes(20), "convert",
                    j => j == null ? "" : $"{j.Phase} {j.FramesRendered}/{j.TotalFrames}");
                Assert.That(enc!.Phase, Is.EqualTo(EncodePhase.Ok), $"convert failed: {enc.Error}");
                var vid = enc.OutputPathMp4 ?? enc.OutputPathGif;
                Assert.That(vid, Is.Not.Null.And.Not.Empty);
                Assert.That(File.Exists(vid!), Is.True);
                Log($"  convert OK: {enc.FramesRendered} frames -> {Path.GetFileName(vid)} " +
                    $"({new FileInfo(vid!).Length / 1e6:0.0} MB) in {sw.Elapsed.TotalSeconds:0.0}s");
            }

            // 2) Planetary stack (align + integrate the best 50%).
            sw.Restart();
            var stkJob = stacker.StartJob(new StackConfig(
                SerPath: ser, OutputDir: outRoot, KeepPercent: 50, OutputName: name + "_stack"));
            var stk = await WaitDone(() => stacker.GetJob(stkJob.Id),
                j => j?.CompletedAt != null, TimeSpan.FromMinutes(20), "stack",
                j => j == null ? "" : $"{j.Phase} pick={j.FramesPicked} align={j.FramesAligned} stack={j.FramesStacked}");
            Assert.That(stk!.Phase, Is.EqualTo(StackPhase.Ok), $"stack failed: {stk.Error}");
            Assert.That(stk.OutputPath, Is.Not.Null);
            Assert.That(File.Exists(stk.OutputPath!), Is.True);
            Log($"  stack OK: {stk.FramesStacked}/{stk.TotalFrames} frames kept -> {Path.GetFileName(stk.OutputPath)} " +
                $"({new FileInfo(stk.OutputPath!).Length / 1e6:0.0} MB) in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        Log($"\nOutputs in: {outRoot}");
    }

    /// <summary>Builds a time-lapse (GIF + MP4) from the per-clip stacked FITS
    /// already sitting in {POLARIS_SER_DIR}/polaris-test-out, one frame per clip,
    /// in save order. Centering is on unless POLARIS_TL_CENTER=0.</summary>
    [Test]
    public async Task Timelapse_OfStacks() {
        var dir = Environment.GetEnvironmentVariable("POLARIS_SER_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            Assert.Ignore("Set POLARIS_SER_DIR (its polaris-test-out must hold *_stack_*.fits)");
        var outRoot = Path.Combine(dir!, "polaris-test-out");
        // POLARIS_TL_ALIGN = off | auto | center | stabilize (default center).
        var alignMode = Environment.GetEnvironmentVariable("POLARIS_TL_ALIGN") is { Length: > 0 } a
            ? a : (Environment.GetEnvironmentVariable("POLARIS_TL_CENTER") is "0" ? "off" : "center");

        // One stack per clip (dedupe re-runs), in save order.
        var stacks = Directory.GetFiles(outRoot, "*_stack_*.fits")
            .GroupBy(p => Path.GetFileName(p).Split("_stack_")[0])
            .Select(g => g.OrderBy(x => x, StringComparer.Ordinal).First())
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();
        Log($"{stacks.Count} stacks -> time-lapse (align={alignMode})");
        Assert.That(stacks.Count, Is.GreaterThan(1), "need the stacked FITS first");

        var ffmpeg = new FfmpegService(NullLogger<FfmpegService>.Instance);
        var encoder = new MediaEncodeService(ffmpeg, NullLogger<MediaEncodeService>.Instance);
        var sw = Stopwatch.StartNew();
        var job = encoder.StartJob(new FolderFrameSource(stacks, 1),
            new EncodeConfig(OutputDir: outRoot, OutputName: "moon_timelapse_" + alignMode,
                Fps: 6, MaxDim: 900,
                Format: ffmpeg.IsAvailable ? EncodeFormat.Both : EncodeFormat.Gif, Loop: true,
                AlignMode: alignMode));
        var enc = await WaitDone(() => encoder.GetJob(job.Id),
            j => j?.CompletedAt != null, TimeSpan.FromMinutes(10), "timelapse",
            j => j == null ? "" : $"{j.Phase} {j.FramesRendered}/{j.TotalFrames}");
        Assert.That(enc!.Phase, Is.EqualTo(EncodePhase.Ok), $"timelapse failed: {enc.Error}");
        if (enc.OutputPathGif != null)
            Log($"  GIF -> {enc.OutputPathGif} ({new FileInfo(enc.OutputPathGif).Length / 1e6:0.0} MB)");
        if (enc.OutputPathMp4 != null)
            Log($"  MP4 -> {enc.OutputPathMp4} ({new FileInfo(enc.OutputPathMp4).Length / 1e6:0.0} MB)");
        Log($"  done in {sw.Elapsed.TotalSeconds:0.0}s");
    }

    private static async Task<T?> WaitDone<T>(Func<T?> poll, Func<T?, bool> done,
            TimeSpan timeout, string label, Func<T?, string> desc) where T : class {
        var deadline = DateTime.UtcNow + timeout;
        string last = "";
        while (DateTime.UtcNow < deadline) {
            var s = poll();
            var d = desc(s);
            if (d != last) { Log($"    {label}: {d}"); last = d; }
            if (done(s)) return s;
            await Task.Delay(500);
        }
        throw new TimeoutException($"{label} did not finish in {timeout}");
    }

    private static void Log(string msg) => TestContext.Progress.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}
