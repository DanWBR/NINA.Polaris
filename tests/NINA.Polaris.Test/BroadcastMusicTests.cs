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
/// Background music for a broadcast: which files play, in what order, and what
/// happens when the operator's path is wrong.
///
/// <para>No disk and no ffmpeg here. The resolver takes its three file system
/// questions as delegates precisely so this can pin the decisions, including
/// the ones that only happen on someone else's machine: a folder that vanished
/// mid-session, a file that is not audio, a track named with an apostrophe.</para>
/// </summary>
[TestFixture]
public class BroadcastMusicTests {

    private static readonly string[] Empty = Array.Empty<string>();

    private static MusicPlaylist Resolve(string? path, bool shuffle = false, int seed = 1,
                                         string[]? files = null, string[]? dirs = null,
                                         string[]? contents = null) {
        var f = new HashSet<string>(files ?? Empty, StringComparer.OrdinalIgnoreCase);
        var d = new HashSet<string>(dirs ?? Empty, StringComparer.OrdinalIgnoreCase);
        return BroadcastMusic.Resolve(path, shuffle, seed,
            p => f.Contains(p), p => d.Contains(p), _ => contents ?? Empty);
    }

    // --- nothing configured ---------------------------------------------

    [Test]
    public void NoPathIsNotAProblem() {
        // The default. Silence is what the broadcast has always had, so it
        // must not produce a warning the operator has to dismiss.
        foreach (var p in new string?[] { null, "", "   " }) {
            var r = Resolve(p);
            Assert.That(r.HasMusic, Is.False);
            Assert.That(r.Problem, Is.Null, $"for {p ?? "null"}");
        }
    }

    // --- one file --------------------------------------------------------

    [Test]
    public void OneFileIsOneTrack() {
        var r = Resolve("/home/polaris/night.mp3", files: new[] { "/home/polaris/night.mp3" });
        Assert.That(r.Tracks, Is.EqualTo(new[] { "/home/polaris/night.mp3" }));
        Assert.That(r.IsSingleTrack, Is.True);
        Assert.That(r.Problem, Is.Null);
    }

    [Test]
    public void ThePathIsTrimmed() {
        // A path pasted into the field brings whitespace with it often enough.
        var r = Resolve("  /music/a.flac  ", files: new[] { "/music/a.flac" });
        Assert.That(r.Tracks, Is.EqualTo(new[] { "/music/a.flac" }));
    }

    [Test]
    public void AFileThatIsNotAudioSaysSoInsteadOfBeingHandedToFfmpeg() {
        var r = Resolve("/home/polaris/notes.txt", files: new[] { "/home/polaris/notes.txt" });
        Assert.That(r.HasMusic, Is.False);
        Assert.That(r.Problem, Does.Contain("notes.txt").And.Contain(".mp3"));
    }

    [Test]
    public void EveryExtensionTheDocsPromiseIsAccepted() {
        foreach (var ext in BroadcastMusic.Extensions) {
            var p = "/music/track" + ext;
            Assert.That(Resolve(p, files: new[] { p }).HasMusic, Is.True, ext);
            // And the same in the case a file manager actually produces.
            var upper = "/music/track" + ext.ToUpperInvariant();
            Assert.That(Resolve(upper, files: new[] { upper }).HasMusic, Is.True, upper);
        }
    }

    // --- a folder ---------------------------------------------------------

    [Test]
    public void AFolderTakesItsAudioFilesAndLeavesTheRest() {
        var r = Resolve("/music", dirs: new[] { "/music" },
            contents: new[] { "/music/b.mp3", "/music/cover.jpg", "/music/a.flac", "/music/readme.txt" });
        Assert.That(r.Tracks, Is.EqualTo(new[] { "/music/a.flac", "/music/b.mp3" }));
    }

    [Test]
    public void WithoutShuffleTheOrderIsSortedAndNotTheFileSystemOrder() {
        // Enumeration order differs between a board and a desktop. Sorting
        // first is what makes "shuffle off" mean something repeatable.
        var r = Resolve("/music", dirs: new[] { "/music" },
            contents: new[] { "/music/03.mp3", "/music/01.mp3", "/music/02.mp3" });
        Assert.That(r.Tracks, Is.EqualTo(new[] { "/music/01.mp3", "/music/02.mp3", "/music/03.mp3" }));
    }

    [Test]
    public void ShuffleReordersButKeepsEveryTrackExactlyOnce() {
        var contents = Enumerable.Range(1, 12).Select(i => $"/music/{i:00}.mp3").ToArray();
        var r = Resolve("/music", shuffle: true, seed: 42, dirs: new[] { "/music" }, contents: contents);
        Assert.That(r.Tracks, Is.Not.EqualTo(contents.OrderBy(x => x).ToArray()));
        Assert.That(r.Tracks.OrderBy(x => x), Is.EqualTo(contents.OrderBy(x => x)));
    }

    [Test]
    public void TheSameSeedShufflesTheSameWay() {
        var contents = Enumerable.Range(1, 10).Select(i => $"/music/{i:00}.mp3").ToArray();
        var a = Resolve("/music", shuffle: true, seed: 7, dirs: new[] { "/music" }, contents: contents);
        var b = Resolve("/music", shuffle: true, seed: 7, dirs: new[] { "/music" }, contents: contents);
        Assert.That(a.Tracks, Is.EqualTo(b.Tracks));
    }

    [Test]
    public void AFolderWithNoAudioSaysSo() {
        var r = Resolve("/music", dirs: new[] { "/music" }, contents: new[] { "/music/cover.jpg" });
        Assert.That(r.HasMusic, Is.False);
        Assert.That(r.Problem, Does.Contain("/music"));
    }

    // --- the path is simply wrong ----------------------------------------

    [Test]
    public void AMissingPathIsReportedAndCostsOnlyTheMusic() {
        // The USB stick came out. The broadcast has to survive that, so the
        // resolver returns no tracks and a sentence, never an exception.
        var r = Resolve("/media/usb/music");
        Assert.That(r.HasMusic, Is.False);
        Assert.That(r.Problem, Does.Contain("/media/usb/music"));
        Assert.That(r.Problem, Does.Contain("without it"));
    }

    // --- the playlist file ------------------------------------------------

    [Test]
    public void ThePlaylistStartsWithTheSignatureFfmpegExpects() {
        var text = BroadcastMusic.RenderPlaylist(new[] { "/music/a.mp3" }, entries: 1);
        Assert.That(text, Does.StartWith("ffconcat version 1.0\n"));
    }

    [Test]
    public void ThePlaylistRepeatsTheSequenceSoTheNightOutlastsIt() {
        // -stream_loop does not loop the concat demuxer: it plays the list once
        // and stops. Repeating the entries is what keeps the music going, so
        // the count is the behaviour, not an implementation detail.
        var tracks = new[] { "/m/a.mp3", "/m/b.mp3" };
        var text = BroadcastMusic.RenderPlaylist(tracks, entries: 10);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Count(l => l.StartsWith("file ")), Is.EqualTo(10));
        // and in order, a b a b, not a a b b
        Assert.That(lines[1], Does.Contain("a.mp3"));
        Assert.That(lines[2], Does.Contain("b.mp3"));
        Assert.That(lines[3], Does.Contain("a.mp3"));
    }

    [Test]
    public void ThePaddingRoundsUpSoTheListIsNeverShortOfWhatWasAsked() {
        var text = BroadcastMusic.RenderPlaylist(new[] { "/m/a.mp3", "/m/b.mp3", "/m/c.mp3" }, entries: 10);
        var files = text.Split('\n').Count(l => l.StartsWith("file "));
        Assert.That(files, Is.GreaterThanOrEqualTo(10));
    }

    [Test]
    public void TheDefaultPlaylistIsLongEnoughToOutlastANight() {
        var text = BroadcastMusic.RenderPlaylist(new[] { "/m/a.mp3" });
        Assert.That(text.Split('\n').Count(l => l.StartsWith("file ")),
            Is.EqualTo(BroadcastMusic.PlaylistEntries));
    }

    [Test]
    public void AnEmptyPlaylistIsAProgrammingErrorNotAnEmptyFile() {
        Assert.That(() => BroadcastMusic.RenderPlaylist(Array.Empty<string>()),
            Throws.ArgumentException);
    }

    // --- quoting ----------------------------------------------------------

    [Test]
    public void APlainPathIsJustQuoted() {
        Assert.That(BroadcastMusic.Quote("/music/a.mp3"), Is.EqualTo("'/music/a.mp3'"));
    }

    [Test]
    public void ASpaceNeedsNothingExtraInsideTheQuotes() {
        Assert.That(BroadcastMusic.Quote("/music/my night.mp3"), Is.EqualTo("'/music/my night.mp3'"));
    }

    [Test]
    public void AnApostropheClosesTheQuoteEscapesAndReopens() {
        // Verified against ffmpeg with a file actually named this way: the
        // shell-looking form works and a bare quote does not.
        Assert.That(BroadcastMusic.Quote("/music/tone a's.mp3"),
            Is.EqualTo(@"'/music/tone a'\''s.mp3'"));
    }

    [Test]
    public void AWindowsPathBecomesForwardSlashes() {
        // ffmpeg takes them on Windows, and it keeps the separator out of the
        // escaping question entirely.
        Assert.That(BroadcastMusic.Quote(@"C:\Users\danie\music\a.mp3"),
            Is.EqualTo("'C:/Users/danie/music/a.mp3'"));
    }

    [Test]
    public void EveryQuotedEntryIsBalanced() {
        // A stray quote would make ffmpeg read the next line as part of the
        // path and lose two tracks rather than one.
        foreach (var p in new[] { "/m/a.mp3", "/m/a b.mp3", "/m/a's.mp3", "/m/#1.mp3", @"C:\m\a.mp3" }) {
            var q = BroadcastMusic.Quote(p);
            Assert.That(q, Does.StartWith("'").And.EndWith("'"), p);
            Assert.That(q.Count(c => c == '\''), Is.EqualTo(2 + 3 * p.Count(c => c == '\'')), p);
        }
    }

    // --- volume -----------------------------------------------------------

    [Test]
    public void VolumeBecomesAMultiplier() {
        Assert.That(BroadcastMusic.VolumeArg(100), Is.EqualTo("1"));
        Assert.That(BroadcastMusic.VolumeArg(50), Is.EqualTo("0.5"));
        Assert.That(BroadcastMusic.VolumeArg(0), Is.EqualTo("0"));
        Assert.That(BroadcastMusic.VolumeArg(35), Is.EqualTo("0.35"));
    }

    [Test]
    public void VolumeIsClampedBecauseItBecomesAProcessArgument() {
        Assert.That(BroadcastMusic.VolumeArg(500), Is.EqualTo("1"));
        Assert.That(BroadcastMusic.VolumeArg(-20), Is.EqualTo("0"));
    }

    [Test]
    public void VolumeUsesAPointWhateverTheHostLocale() {
        // A board set to pt-BR would otherwise write 0,5 and ffmpeg would
        // reject the filter, on that machine only.
        var before = System.Threading.Thread.CurrentThread.CurrentCulture;
        try {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("pt-BR");
            Assert.That(BroadcastMusic.VolumeArg(35), Is.EqualTo("0.35"));
        } finally {
            System.Threading.Thread.CurrentThread.CurrentCulture = before;
        }
    }
}
