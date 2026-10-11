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

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services.Broadcast;
using NINA.Polaris.Services.External;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The live runner against a real ffmpeg, recording to disk with no network
/// involved. Skipped where ffmpeg is not installed, which is a supported state
/// for a Polaris host: the .deb only recommends it.
///
/// <para>These exist because the part that breaks silently cannot be checked by
/// reading the arguments. An MP4 whose writer was killed instead of being let
/// finish has no index at the end of it and no player will open it, and the
/// process that produced it exited looking perfectly healthy.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class FfmpegLiveRunTests {

    private FfmpegService _ffmpeg = null!;
    private string _dir = null!;

    [SetUp]
    public void SetUp() {
        _ffmpeg = new FfmpegService(NullLogger<FfmpegService>.Instance);
        if (!_ffmpeg.IsAvailable) Assert.Ignore("ffmpeg is not installed on this machine.");
        _dir = Path.Combine(Path.GetTempPath(), "polaris-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { if (_dir != null) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static BroadcastPlan RecordOnly(string outPath, string? banner = null) => new() {
        Quality = BroadcastQuality.Parse("low"),
        Encoder = "libx264",
        InputFps = 5,
        OutputFps = 10,
        RecordPath = outPath,
        BannerTextPath = banner,
        FontFile = OperatingSystem.IsWindows() ? @"C:\Windows\Fonts\arial.ttf" : null
    };

    /// <summary>A flat grey frame, which is all the encoder needs to be given.</summary>
    private static byte[] Frame(BroadcastQuality q, byte value) =>
        Enumerable.Repeat(value, q.Width * q.Height * 3).ToArray();

    [Test]
    [CancelAfter(60_000)]
    public async Task ARecordingThatStopsOnItsOwnIsAPlayableFile() {
        var q = BroadcastQuality.Parse("low");
        var outPath = Path.Combine(_dir, "night.mp4");
        var bannerPath = Path.Combine(_dir, "banner.txt");
        await File.WriteAllTextAsync(bannerPath, "M42 · 120s · 14 frames");

        var plan = RecordOnly(outPath, bannerPath);
        var args = BroadcastArgs.Build(plan);
        var readings = new List<FfmpegProgress>();

        var result = await _ffmpeg.RunLiveAsync(args,
            pumpFrames: async (stdin, ct) => {
                for (var i = 0; i < 15 && !ct.IsCancellationRequested; i++) {
                    await stdin.WriteAsync(Frame(q, (byte)(40 + i * 8)), ct);
                    await stdin.FlushAsync(ct);
                }
            },
            onProgress: p => { lock (readings) readings.Add(p); });

        Assert.That(result.Killed, Is.False,
            "closing stdin has to be enough; a kill here is what leaves an MP4 without its index");
        Assert.That(result.ExitCode, Is.EqualTo(0), result.StderrTail);
        Assert.That(File.Exists(outPath), Is.True);
        Assert.That(new FileInfo(outPath).Length, Is.GreaterThan(1024));

        lock (readings) {
            Assert.That(readings, Is.Not.Empty, "the progress stream is what the watchdog runs on");
            Assert.That(readings[^1].Frame, Is.GreaterThan(0));
        }

        // The real check: read it back. A file with no moov atom exists, has a
        // plausible size, and opens nowhere.
        Assert.That(await Decodes(outPath), Is.True, "the recording is not playable");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task AnAbortReturnsPromptlyRatherThanWaitingOutTheDrain() {
        var q = BroadcastQuality.Parse("low");
        var outPath = Path.Combine(_dir, "aborted.mp4");
        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var sw = Stopwatch.StartNew();
        var result = await _ffmpeg.RunLiveAsync(BroadcastArgs.Build(RecordOnly(outPath)),
            pumpFrames: async (stdin, ct) => {
                // A broadcast writing frames until something stops it.
                while (!ct.IsCancellationRequested) {
                    await stdin.WriteAsync(Frame(q, 90), ct);
                    await stdin.FlushAsync(ct);
                    await Task.Delay(100, ct);
                }
            },
            drainTimeout: TimeSpan.FromSeconds(30),
            ct: abort.Token);
        sw.Stop();

        // The generous drain above is for the graceful path. An abort is the
        // operator wanting it to stop now, so the long wait must not apply.
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task AnEncoderThatIsListedIsNotNecessarilyOneThatRuns() {
        // The gap that cost a restart loop: this machine lists h264_qsv, has
        // no usable Quick Sync, and answers "Could not open encoder before
        // EOF" on the first frame. Nothing in the encoder table shows that,
        // and on Windows there is no device node to check either, so the only
        // honest test is to encode something.
        var caps = await _ffmpeg.GetCapabilitiesAsync();
        Assert.That(caps, Is.Not.Null);

        Assert.That(await _ffmpeg.CanEncodeAsync("libx264"), Is.True,
            "software encoding is the floor the broadcast falls back to");
        Assert.That(await _ffmpeg.CanEncodeAsync("h264_thereisnosuchencoder"), Is.False);

        // Cached: asking twice must not spawn twice.
        var again = await _ffmpeg.CanEncodeAsync("libx264");
        Assert.That(again, Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task TheBinaryIsAskedWhatItCanDo() {
        var caps = await _ffmpeg.GetCapabilitiesAsync();
        Assert.That(caps, Is.Not.Null);
        var encoders = FfmpegEncoders.ParseEncoders(caps!.EncodersOutput);
        Assert.That(encoders, Does.Contain("libx264"),
            "every build Polaris ships next to has it; without it there is no broadcast");
        Assert.That(FfmpegEncoders.HasFilter(caps.FiltersOutput, "drawtext"), Is.True);
        Assert.That(FfmpegEncoders.HasFilter(caps.FiltersOutput, "overlay"), Is.True);
        Assert.That(caps.Version, Is.Not.Null.And.Not.Empty);

        // Cached: the same instance, not a second pair of processes.
        Assert.That(await _ffmpeg.GetCapabilitiesAsync(), Is.SameAs(caps));
    }

    private static async Task<bool> Decodes(string path) {
        var psi = new ProcessStartInfo {
            FileName = new FfmpegService(NullLogger<FfmpegService>.Instance).BinaryPath!,
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var a in new[] { "-v", "error", "-i", path, "-f", "null", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode == 0 && string.IsNullOrWhiteSpace(err);
    }
}
