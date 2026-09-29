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

using NINA.Polaris.Services.Broadcast;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The ffmpeg command line for a live broadcast. Everything here is a rule a
/// streaming platform imposes or a credential that must not escape, which is
/// why it is pinned in tests rather than left to a reviewer's memory.
/// </summary>
[TestFixture]
public class BroadcastArgsTests {

    private static BroadcastPlan Plan(string quality = "medium", string encoder = "libx264",
                                      string? url = "rtmp://a.rtmp.youtube.com/live2",
                                      string? key = "abcd-1234-efgh",
                                      string? record = null, string? card = null,
                                      string? banner = null) =>
        new() {
            Quality = BroadcastQuality.Parse(quality),
            Encoder = encoder,
            RtmpUrl = url,
            StreamKey = key,
            RecordPath = record,
            CardPngPath = card,
            BannerTextPath = banner
        };

    private static string Flat(IEnumerable<string> args) => string.Join(" ", args);

    [Test]
    public void TheStreamGoesToTheUrlWithTheKeyOnTheEnd() {
        var args = BroadcastArgs.Build(Plan());
        Assert.That(Flat(args), Does.Contain("rtmp://a.rtmp.youtube.com/live2/abcd-1234-efgh"));
        Assert.That(args, Does.Contain("flv"));
    }

    [Test]
    public void ThereIsAlwaysAnAudioTrack() {
        // YouTube refuses a video-only RTMP stream. Silence satisfies it and
        // costs 128 kbps.
        var args = BroadcastArgs.Build(Plan());
        Assert.That(Flat(args), Does.Contain("anullsrc"));
        Assert.That(args, Does.Contain("aac"));
    }

    [Test]
    public void TheKeyframeIntervalIsTwoSeconds() {
        // The platforms want one at least every four seconds; two is the
        // recommendation, and -g counts frames, so it tracks the output rate.
        var args = BroadcastArgs.Build(Plan() with { OutputFps = 30 });
        var g = args.IndexOf("-g");
        Assert.That(g, Is.GreaterThan(-1));
        Assert.That(args[g + 1], Is.EqualTo("60"));

        var slower = BroadcastArgs.Build(Plan() with { OutputFps = 25 });
        Assert.That(slower[slower.IndexOf("-g") + 1], Is.EqualTo("50"));
    }

    [TestCase("low", "854x480", "1000k")]
    [TestCase("medium", "1280x720", "2500k")]
    [TestCase("high", "1920x1080", "4500k")]
    public void QualityPicksResolutionAndBitrateTogether(string id, string size, string bitrate) {
        var args = BroadcastArgs.Build(Plan(id));
        Assert.That(args, Does.Contain(size));
        Assert.That(args, Does.Contain(bitrate));
    }

    [Test]
    public void AnUnknownQualityFallsBackToTheMiddleOne() {
        // The id reaches here from the wire and becomes process arguments.
        Assert.That(BroadcastQuality.IsValid("ultra"), Is.False);
        Assert.That(BroadcastQuality.Parse("ultra").Id, Is.EqualTo("medium"));
        Assert.That(BroadcastQuality.Parse(null).Id, Is.EqualTo("medium"));
        Assert.That(BroadcastQuality.Parse("; rm -rf /").Id, Is.EqualTo("medium"));
    }

    [Test]
    public void TheKeyNeverAppearsInTheLoggableForm() {
        var plan = Plan(key: "super-secret-key");
        var args = BroadcastArgs.Build(plan);
        var shown = BroadcastArgs.Redact(args, plan.StreamKey);
        Assert.That(shown, Does.Not.Contain("super-secret-key"));
        Assert.That(shown, Does.Contain("<stream-key>"));
        Assert.That(shown, Does.Contain("rtmp://a.rtmp.youtube.com/live2"),
            "the destination is not the secret, and hiding it would make a support "
            + "question unanswerable");
    }

    [Test]
    public void SoftwareEncodingAsksForSpeed_HardwareDoesNot() {
        // -preset is an x264 option; the hardware encoders reject it, which
        // ends the broadcast before the first frame.
        Assert.That(BroadcastArgs.Build(Plan(encoder: "libx264")), Does.Contain("-preset"));
        Assert.That(BroadcastArgs.Build(Plan(encoder: "h264_v4l2m2m")), Does.Not.Contain("-preset"));
        Assert.That(BroadcastArgs.Build(Plan(encoder: "h264_rkmpp")), Does.Not.Contain("-tune"));
    }

    [Test]
    public void RecordingIsASecondOutput_AndOnlyWhenAsked() {
        Assert.That(Flat(BroadcastArgs.Build(Plan())), Does.Not.Contain(".mp4"));
        var both = BroadcastArgs.Build(Plan(record: "/data/broadcast/night.mp4"));
        Assert.That(Flat(both), Does.Contain("/data/broadcast/night.mp4"));
        Assert.That(Flat(both), Does.Contain("rtmp://"), "recording does not replace the stream");
    }

    [Test]
    public void RecordingWithNoDestinationIsAValidBroadcast() {
        // Upload is bad, or there is no upload: compose and keep the file.
        var args = BroadcastArgs.Build(Plan(url: null, key: null, record: "/data/night.mp4"));
        Assert.That(Flat(args), Does.Contain("/data/night.mp4"));
        Assert.That(Flat(args), Does.Not.Contain("flv"));
    }

    [Test]
    public void NowhereToSendItIsRefused() {
        Assert.Throws<InvalidOperationException>(
            () => BroadcastArgs.Build(Plan(url: null, key: null)));
        Assert.Throws<InvalidOperationException>(
            () => BroadcastArgs.Build(Plan(url: "rtmp://x/live", key: "  ")),
            "a blank key is not a destination");
    }

    [Test]
    public void TheCardIsOverlaidTopRight_AndTheBannerReloadsItself() {
        var args = BroadcastArgs.Build(Plan(card: "/tmp/card.png", banner: "/tmp/banner.txt"));
        var flat = Flat(args);
        Assert.That(flat, Does.Contain("overlay=x=W-w-"), "the card sits on the right");
        Assert.That(flat, Does.Contain("drawtext"));
        Assert.That(flat, Does.Contain("reload=1"),
            "the banner is a file so the numbers can change without restarting ffmpeg");
        Assert.That(flat, Does.Contain("[v]"), "the filter chain has to end somewhere mapped");
    }

    [Test]
    public void APathWithAColonDoesNotEndTheFilterOption() {
        // A Windows path, or a target folder with a colon, would otherwise cut
        // the drawtext option in half and ffmpeg would refuse to start. Two
        // backslashes, not one: the filtergraph parser eats a level of escaping
        // before the filter's own option parser ever sees the colon. Verified
        // against a real ffmpeg in FfmpegLiveRunTests, which is where a single
        // backslash was caught failing.
        var args = BroadcastArgs.Build(Plan(banner: @"C:\data\banner.txt"));
        Assert.That(Flat(args), Does.Contain(@"C\\:/data/banner.txt"));
    }

    [Test]
    public void EveryOutputEndsWithThePicture() {
        // The silent audio and the card PNG are endless sources. Without
        // -shortest, closing stdin does not end the run: ffmpeg carries on
        // encoding silence, the stop times out and the process is killed, and
        // a killed writer is a truncated recording. One per output, because it
        // is an output option.
        var both = BroadcastArgs.Build(Plan(record: "/data/night.mp4", card: "/tmp/card.png"));
        Assert.That(both.Count(a => a == "-shortest"), Is.EqualTo(2));
        Assert.That(both.IndexOf("-shortest"), Is.LessThan(both.IndexOf("flv")));
    }

    [Test]
    public void TheRecordingIsWrittenSoAnInterruptionCostsOnlyTheLastSeconds() {
        // Hours of recording on a board that can lose power. A plain MP4 holds
        // its index until the writer exits, so a crash costs the whole night.
        var args = BroadcastArgs.Build(Plan(record: "/data/night.mp4"));
        var flags = args[args.IndexOf("-movflags") + 1];
        Assert.That(flags, Does.Contain("frag_keyframe"));
        Assert.That(flags, Does.Contain("empty_moov"));
        Assert.That(flags, Does.Not.Contain("faststart"),
            "faststart would also rewrite gigabytes before the process could stop");
    }

    [Test]
    public void ProgressIsReportedInTheFormAReaderCanFollow() {
        // -stats writes its one-liner terminated by a carriage return, so a
        // line reader holds each reading until the next one pushes it out. Over
        // a broadcast that lag is what a stall watchdog would fire on.
        var args = BroadcastArgs.Build(Plan());
        Assert.That(args, Does.Contain("-progress"));
        Assert.That(args[args.IndexOf("-progress") + 1], Is.EqualTo("pipe:2"));
        Assert.That(args, Does.Not.Contain("-stats"));
    }

    [Test]
    public void NoCardAndNoBannerMeansNoFilterAtAll() {
        var args = BroadcastArgs.Build(Plan());
        Assert.That(args, Does.Not.Contain("-filter_complex"));
        Assert.That(Flat(args), Does.Contain("-map 0:v"));
    }
}
