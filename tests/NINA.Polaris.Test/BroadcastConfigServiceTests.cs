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
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services.Broadcast;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Where the stream key lives and who gets to see it. A stream key is enough
/// to broadcast to someone's channel as them, so the rules about it are pinned
/// here rather than left to review.
/// </summary>
[TestFixture]
public class BroadcastConfigServiceTests {

    private string _dir = null!;
    private BroadcastConfigService _svc = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-bcast-" + Guid.NewGuid().ToString("N")[..8]);
        _svc = New();
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A second service over the same file: what a restart sees.</summary>
    private BroadcastConfigService New() =>
        new(Path.Combine(_dir, "broadcast", "config.json"), NullLogger<BroadcastConfigService>.Instance);

    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Test]
    public void NothingConfiguredIsAWorkingDefault() {
        var c = _svc.Get();
        Assert.That(c.Quality, Is.EqualTo("medium"));
        Assert.That(c.ShowObjectCard, Is.True);
        Assert.That(c.ShowBanner, Is.True);
        Assert.That(c.FetchDescriptions, Is.True, "it never blocks a frame, so there is nothing to protect against");
        Assert.That(c.RecordToDisk, Is.False, "writing gigabytes to the card is opt in");
        Assert.That(c.HasStreamKey, Is.False);
    }

    [Test]
    public void NullKeepsAndEmptyClears() {
        _svc.Update(new BroadcastConfigUpdate(RtmpUrl: "rtmp://a.rtmp.youtube.com/live2", StreamKey: "abcd-1234"));

        // The interface sends null for a password field the operator did not
        // touch, because it never had the value to send back.
        var kept = _svc.Update(new BroadcastConfigUpdate(Quality: "high"));
        Assert.That(kept.StreamKey, Is.EqualTo("abcd-1234"));
        Assert.That(kept.Quality, Is.EqualTo("high"));

        var cleared = _svc.Update(new BroadcastConfigUpdate(StreamKey: ""));
        Assert.That(cleared.HasStreamKey, Is.False);
        Assert.That(cleared.RtmpUrl, Is.EqualTo("rtmp://a.rtmp.youtube.com/live2"), "clearing the key is not clearing the rest");
    }

    [Test]
    public void TheKeyIsNotInWhatTheBrowserIsGiven() {
        _svc.Update(new BroadcastConfigUpdate(RtmpUrl: "rtmp://live.twitch.tv/app", StreamKey: "live_123_supersecret"));
        var shown = Json(_svc.GetPublic());
        Assert.That(shown, Does.Not.Contain("live_123_supersecret"));
        Assert.That(shown, Does.Contain("hasStreamKey"));
        Assert.That(shown, Does.Contain("true"));
        Assert.That(shown, Does.Contain("rtmp://live.twitch.tv/app"),
            "the destination is not the secret, and hiding it would make the card unreadable");
    }

    [Test]
    public void AKeyCopiedOutOfAWebPageBringsWhitespaceWithIt() {
        var c = _svc.Update(new BroadcastConfigUpdate(StreamKey: "  abcd-1234-efgh\n"));
        Assert.That(c.StreamKey, Is.EqualTo("abcd-1234-efgh"));
    }

    [Test]
    public void TheQualityIsMatchedAgainstTheAllowlist() {
        // It becomes ffmpeg arguments, so anything not on the list is refused
        // rather than passed along.
        Assert.Throws<ArgumentException>(() => _svc.Update(new BroadcastConfigUpdate(Quality: "4k")));
        Assert.Throws<ArgumentException>(() => _svc.Update(new BroadcastConfigUpdate(Quality: "high; rm -rf /")));
        Assert.That(_svc.Get().Quality, Is.EqualTo("medium"), "a refused update changes nothing");
    }

    [Test]
    public void TheAddressOfTheStudioPageIsNotAnIngestUrl() {
        // The commonest way to get this wrong: paste what is in the browser's
        // address bar instead of the ingest URL the platform shows.
        var ex = Assert.Throws<ArgumentException>(
            () => _svc.Update(new BroadcastConfigUpdate(RtmpUrl: "https://studio.youtube.com/channel/x/livestreaming")));
        Assert.That(ex!.Message, Does.Contain("rtmp"));

        Assert.DoesNotThrow(() => _svc.Update(new BroadcastConfigUpdate(RtmpUrl: "rtmp://a.rtmp.youtube.com/live2")));
        Assert.DoesNotThrow(() => _svc.Update(new BroadcastConfigUpdate(
            RtmpUrl: "rtmps://live-api-s.facebook.com:443/rtmp")), "Facebook only takes RTMPS");
        Assert.DoesNotThrow(() => _svc.Update(new BroadcastConfigUpdate(RtmpUrl: "")),
            "emptying the field is how you go back to recording only");
    }

    [Test]
    public void AnUnknownDestinationIsRefused() {
        Assert.Throws<ArgumentException>(() => _svc.Update(new BroadcastConfigUpdate(Destination: "myspace")));
        Assert.DoesNotThrow(() => _svc.Update(new BroadcastConfigUpdate(Destination: "instagram")));
    }

    [Test]
    public void EveryDestinationEitherPrefillsAWorkingUrlOrNoneAtAll() {
        foreach (var d in BroadcastDestinations.All) {
            Assert.That(d.Id, Is.Not.Empty);
            Assert.That(d.Label, Is.Not.Empty);
            if (d.RtmpUrl.Length == 0) continue;
            Assert.That(d.RtmpUrl, Does.StartWith("rtmp"), d.Id);
            Assert.That(d.RtmpUrl, Does.Not.EndWith("/"),
                $"{d.Id}: the key is appended with a slash, and two would be a 404");
        }
        // Instagram issues a URL per session and Custom is the operator's own,
        // so there is nothing honest to prefill for either.
        Assert.That(BroadcastDestinations.Find("instagram")!.RtmpUrl, Is.Empty);
        Assert.That(BroadcastDestinations.Find("custom")!.RtmpUrl, Is.Empty);
    }

    [Test]
    public void ItSurvivesARestart() {
        _svc.Update(new BroadcastConfigUpdate(
            Destination: "twitch", RtmpUrl: "rtmp://live.twitch.tv/app", StreamKey: "live_42",
            Quality: "low", ShowBanner: false, RecordToDisk: true));

        var afterRestart = New().Get();
        Assert.That(afterRestart.Destination, Is.EqualTo("twitch"));
        Assert.That(afterRestart.StreamKey, Is.EqualTo("live_42"));
        Assert.That(afterRestart.Quality, Is.EqualTo("low"));
        Assert.That(afterRestart.ShowBanner, Is.False);
        Assert.That(afterRestart.ShowObjectCard, Is.True);
        Assert.That(afterRestart.RecordToDisk, Is.True);
    }

    [Test]
    public void AConfigFileThatCannotBeReadIsNotAStartupFailure() {
        var path = Path.Combine(_dir, "broadcast", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");
        Assert.That(New().Get().Quality, Is.EqualTo("medium"));
    }

    [Test]
    public void RecordingWithNoDestinationIsAValidSetup() {
        Assert.That(_svc.Validate(ffmpegAvailable: true), Is.Not.Null, "nothing configured yet");

        _svc.Update(new BroadcastConfigUpdate(RecordToDisk: true));
        Assert.That(_svc.Validate(ffmpegAvailable: true), Is.Null,
            "composing the video and keeping the file is what a bad uplink leaves you");

        _svc.Update(new BroadcastConfigUpdate(RecordToDisk: false, RtmpUrl: "rtmp://a.rtmp.youtube.com/live2"));
        Assert.That(_svc.Validate(ffmpegAvailable: true), Does.Contain("stream key"));

        _svc.Update(new BroadcastConfigUpdate(StreamKey: "abcd"));
        Assert.That(_svc.Validate(ffmpegAvailable: true), Is.Null);
    }

    [Test]
    public void WithoutFfmpegNothingCanStart() {
        _svc.Update(new BroadcastConfigUpdate(RtmpUrl: "rtmp://a.rtmp.youtube.com/live2", StreamKey: "abcd"));
        Assert.That(_svc.Validate(ffmpegAvailable: false), Does.Contain("ffmpeg"));
    }

    [Test]
    [Platform(Exclude = "Win", Reason = "Unix file modes")]
    public void TheFileIsReadableOnlyByTheAccountRunningPolaris() {
        _svc.Update(new BroadcastConfigUpdate(StreamKey: "abcd-1234"));
        var path = _svc.FilePath;

        Assert.That(File.GetUnixFileMode(path),
            Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        // A 0755 directory around a 0600 file still announces that there is a
        // stream key on this host.
        Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(path)!),
            Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }
}
