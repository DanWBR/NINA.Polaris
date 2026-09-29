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

using NINA.Polaris.Services.External;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Reading ffmpeg's progress off stderr while it runs. During a broadcast these
/// numbers are the only evidence the thing is alive, so both of the formats
/// ffmpeg speaks are pinned here against real output.
/// </summary>
[TestFixture]
public class FfmpegProgressParserTests {

    // One group of `-progress pipe:2` output, verbatim.
    private static readonly string[] ProgressGroup = {
        "frame=50",
        "fps=25.0",
        "stream_0_0_q=28.0",
        "bitrate=2497.4kbits/s",
        "total_size=624384",
        "out_time_us=2000000",
        "out_time_ms=2000000",
        "out_time=00:00:02.000000",
        "dup_frames=45",
        "drop_frames=0",
        "speed=1.01x",
        "progress=continue"
    };

    private static FfmpegProgress FeedAll(FfmpegProgressParser p, IEnumerable<string> lines) {
        FfmpegProgress? last = null;
        foreach (var l in lines) last = p.Feed(l) ?? last;
        return last!;
    }

    [Test]
    public void AGroupOfKeyValueLinesBecomesOneReading() {
        var p = new FfmpegProgressParser();
        // Nothing may be reported until the group is complete; half a reading
        // would have the watchdog acting on a bitrate that is not yet set.
        foreach (var l in ProgressGroup[..^1]) Assert.That(p.Feed(l), Is.Null);

        var r = p.Feed(ProgressGroup[^1]);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Frame, Is.EqualTo(50));
        Assert.That(r.Fps, Is.EqualTo(25.0));
        Assert.That(r.BitrateKbps, Is.EqualTo(2497.4).Within(0.01));
        Assert.That(r.OutTime, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(r.DuplicatedFrames, Is.EqualTo(45));
        Assert.That(r.DroppedFrames, Is.EqualTo(0));
        Assert.That(r.Speed, Is.EqualTo(1.01).Within(0.001));
        Assert.That(r.TotalSizeBytes, Is.EqualTo(624384));
    }

    [Test]
    public void TheOneLineStatsFormIsAWholeReadingByItself() {
        var p = new FfmpegProgressParser();
        var r = p.Feed("frame=  247 fps= 25 q=28.0 size=    3421kB time=00:00:09.88 "
                     + "bitrate=2836.1kbits/s dup=222 drop=3 speed=1.01x");
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Frame, Is.EqualTo(247));
        Assert.That(r.Fps, Is.EqualTo(25));
        Assert.That(r.DroppedFrames, Is.EqualTo(3));
        Assert.That(r.OutTime.TotalSeconds, Is.EqualTo(9.88).Within(0.01));
        Assert.That(r.TotalSizeBytes, Is.EqualTo(3421 * 1024));
    }

    [Test]
    public void TheMillisecondKeyIsMicroseconds() {
        // ffmpeg has always written microseconds into out_time_ms. Reading the
        // name instead of the value puts the broadcast clock a thousand times
        // fast, which is the sort of thing that only shows up on air.
        var p = new FfmpegProgressParser();
        var r = FeedAll(p, new[] { "out_time_ms=90000000", "progress=continue" });
        Assert.That(r.OutTime, Is.EqualTo(TimeSpan.FromSeconds(90)));
    }

    [Test]
    public void TheFirstSecondReportsNothingUsableAndThatIsNotZero() {
        // "N/A" until the encoder has something to average over. Turning that
        // into 0.0 would show a broadcast at zero bitrate on startup.
        var p = new FfmpegProgressParser();
        var r = FeedAll(p, new[] { "frame=1", "bitrate=N/A", "speed=N/A", "out_time=N/A", "progress=continue" });
        Assert.That(r.Frame, Is.EqualTo(1));
        Assert.That(r.BitrateKbps, Is.EqualTo(0), "no reading yet, and nothing earlier to carry forward");

        var second = FeedAll(p, new[] { "frame=2", "bitrate=2500.0kbits/s", "progress=continue" });
        Assert.That(second.BitrateKbps, Is.EqualTo(2500.0).Within(0.01));

        // The group after that says nothing about the bitrate, and the reading
        // must not fall back to zero.
        var third = FeedAll(p, new[] { "frame=3", "progress=continue" });
        Assert.That(third.BitrateKbps, Is.EqualTo(2500.0).Within(0.01));
        Assert.That(third.Frame, Is.EqualTo(3));
    }

    [Test]
    public void EveryProgressLineIsClaimed_NotJustTheOneThatCompletesAReading() {
        // The bug this exists for: the caller keeps whatever the parser did
        // not claim as the error text. In the -progress form a reading is a
        // dozen lines and only the last completes it, so eleven lines of
        // frame= and bitrate= were being filed as diagnostics, and a
        // broadcast whose encoder failed to open reported its last error as
        // "speed=   2x" instead of the message that said so.
        var p = new FfmpegProgressParser();
        foreach (var line in ProgressGroup) {
            Assert.That(p.TryFeed(line, out _), Is.True, line);
        }

        Assert.That(p.TryFeed("[vost#0:0/h264_qsv] Could not open encoder before EOF", out var none), Is.False);
        Assert.That(none, Is.Null);
        // A diagnostic that happens to carry an equals sign is still a
        // diagnostic, and ffmpeg emits plenty of them.
        Assert.That(p.TryFeed("  Stream #0:0: Video: rawvideo, rgb24, 1280x720, q=2-31, 25 tbr", out _), Is.False);
        Assert.That(p.TryFeed("Task finished with error code: -22 (Invalid argument)", out _), Is.False);
    }

    [Test]
    public void TheRealProgressOutputPadsItsValues() {
        // Taken from ffmpeg 9.0 on Windows: the -progress form pads the same
        // way -stats does, which a split on whitespace would not survive.
        var p = new FfmpegProgressParser();
        var r = FeedAll(p, new[] {
            "frame=0", "fps=0.00", "bitrate=   0.0kbits/s", "total_size=0",
            "out_time=00:00:00.998458", "dup_frames=11", "drop_frames=0",
            "speed=   2x", "progress=end"
        });
        Assert.That(r.Speed, Is.EqualTo(2).Within(0.001));
        Assert.That(r.BitrateKbps, Is.EqualTo(0));
        Assert.That(r.DuplicatedFrames, Is.EqualTo(11));
    }

    [Test]
    public void DiagnosticsAreNotReadings() {
        // Everything that is not progress has to come back null so the caller
        // can keep it for the error message.
        var p = new FfmpegProgressParser();
        Assert.That(p.Feed("[flv @ 0x5599] Failed to update header with correct duration."), Is.Null);
        Assert.That(p.Feed("Connection to tcp://a.rtmp.youtube.com:1935 failed: Connection refused"), Is.Null);
        Assert.That(p.Feed("  Stream #0:0: Video: rawvideo, rgb24, 1280x720, q=2-31, 25 tbr, 25 tbn"), Is.Null);
        Assert.That(p.Feed(""), Is.Null);
        Assert.That(p.Feed(null), Is.Null);
    }

    [Test]
    public void ARisingDropCountSurvivesTheGroupsBetween() {
        var p = new FfmpegProgressParser();
        FeedAll(p, new[] { "frame=100", "drop_frames=0", "progress=continue" });
        FeedAll(p, new[] { "frame=150", "progress=continue" });
        var r = FeedAll(p, new[] { "frame=200", "drop_frames=12", "progress=continue" });
        Assert.That(r.DroppedFrames, Is.EqualTo(12));
        Assert.That(p.Last.DroppedFrames, Is.EqualTo(12));
        Assert.That(p.Last.Frame, Is.EqualTo(200));
    }

    [Test]
    public void TheEndOfTheRunIsAReadingLikeAnyOther() {
        var p = new FfmpegProgressParser();
        var r = FeedAll(p, new[] { "frame=900", "out_time=00:06:00.000000", "progress=end" });
        Assert.That(r, Is.Not.Null);
        Assert.That(r.OutTime, Is.EqualTo(TimeSpan.FromMinutes(6)));
    }
}
