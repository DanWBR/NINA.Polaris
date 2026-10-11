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

namespace NINA.Polaris.Services.Broadcast;

/// <summary>What the operator's music setting resolved to.</summary>
/// <param name="Tracks">The files, in the order they will play. Empty when
/// there is no usable music, and the broadcast then keeps its silent track.</param>
/// <param name="Problem">Why there is no music, in the operator's words. Null
/// when there was nothing to say, including when no music was configured.</param>
public sealed record MusicPlaylist(IReadOnlyList<string> Tracks, string? Problem) {
    public static readonly MusicPlaylist None = new(Array.Empty<string>(), null);
    public bool HasMusic => Tracks.Count > 0;
    public bool IsSingleTrack => Tracks.Count == 1;
}

/// <summary>
/// Background music for a broadcast, from a file or a folder the operator
/// points at.
///
/// <para><b>Polaris ships no music.</b> Almost every library that calls itself
/// royalty free licenses you to put a track in your video and forbids
/// redistributing the file itself, which is exactly what shipping one in a
/// .deb or an SD card image would be. Only CC0 and Creative Commons material
/// could travel that way, and even then someone has to carry the attribution.
/// Pointing at the operator's own file keeps the licence where the account is:
/// whoever presses Start is who the platform sends the claim to.
/// </para>
///
/// <para>Two shapes, because ffmpeg treats them differently and the difference
/// was measured rather than assumed:</para>
/// <list type="bullet">
/// <item>One file loops with <c>-stream_loop -1</c>, which works.</item>
/// <item>A folder becomes an ffconcat playlist. <c>-stream_loop</c> does
/// <b>not</b> loop the concat demuxer: it plays the list once, reports
/// "Operation not permitted" and stops, so the night would fall silent after
/// the first pass. The sequence is written out repeatedly instead, which costs
/// a few kilobytes of text and nothing at runtime.</item>
/// </list>
/// </summary>
public static class BroadcastMusic {

    /// <summary>What ffmpeg decodes without argument on a stock Debian build.</summary>
    public static readonly string[] Extensions =
        { ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wav", ".wma" };

    /// <summary>How many entries the generated playlist is padded to. At four
    /// minutes a track that is well over a day, so the music never runs out
    /// before the night does, and the file is a few tens of kilobytes.</summary>
    public const int PlaylistEntries = 500;

    public static bool IsAudioFile(string path) {
        var ext = Path.GetExtension(path);
        foreach (var e in Extensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Resolve the configured path into the tracks that will play.
    ///
    /// <para>The file system is reached through the three delegates so the
    /// whole decision is testable without touching a disk, and
    /// <paramref name="seed"/> makes the shuffle reproducible for the same
    /// reason.</para>
    /// </summary>
    public static MusicPlaylist Resolve(string? configured, bool shuffle, int seed,
                                        Func<string, bool> fileExists,
                                        Func<string, bool> directoryExists,
                                        Func<string, IEnumerable<string>> listFiles) {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(listFiles);
        if (string.IsNullOrWhiteSpace(configured)) return MusicPlaylist.None;
        var path = configured.Trim();

        if (fileExists(path)) {
            if (!IsAudioFile(path))
                return new MusicPlaylist(Array.Empty<string>(),
                    $"{Path.GetFileName(path)} is not an audio file Polaris can play. "
                    + "Use " + string.Join(", ", Extensions) + ".");
            return new MusicPlaylist(new[] { path }, null);
        }

        if (directoryExists(path)) {
            var found = new List<string>();
            foreach (var f in listFiles(path)) if (IsAudioFile(f)) found.Add(f);
            if (found.Count == 0)
                return new MusicPlaylist(Array.Empty<string>(), $"No audio files in {path}.");
            // Sorted first, always. Without it the order is whatever the file
            // system hands back, which differs between a board and a desktop,
            // and "shuffle off" would not mean anything repeatable.
            found.Sort(StringComparer.OrdinalIgnoreCase);
            if (shuffle) Shuffle(found, seed);
            return new MusicPlaylist(found, null);
        }

        return new MusicPlaylist(Array.Empty<string>(),
            $"No music at {path}. The broadcast will run without it.");
    }

    private static void Shuffle(List<string> items, int seed) {
        var rnd = new Random(seed);
        for (var i = items.Count - 1; i > 0; i--) {
            var j = rnd.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// Render an ffconcat playlist, the sequence repeated enough times to
    /// outlast the session.
    /// </summary>
    public static string RenderPlaylist(IReadOnlyList<string> tracks, int entries = PlaylistEntries) {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
            throw new ArgumentException("A playlist needs at least one track.", nameof(tracks));
        var sb = new System.Text.StringBuilder();
        sb.Append("ffconcat version 1.0\n");
        var repeats = Math.Max(1, (int)Math.Ceiling((double)entries / tracks.Count));
        for (var r = 0; r < repeats; r++)
            foreach (var t in tracks)
                sb.Append("file ").Append(Quote(t)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Quote a path for the concat demuxer.
    ///
    /// <para>This is ffmpeg's own quoting, which for single quotes happens to
    /// read like the shell's: inside single quotes nothing is escaped and a
    /// quote cannot appear, so a quote in a file name has to close the string,
    /// arrive escaped, and reopen it. Both this and backslash-escaping every
    /// special character were tried against a file actually named with a quote
    /// and a space, and both work; this one is used because a single rule
    /// covers the space, the quote, and the hash that would otherwise start a
    /// comment.</para>
    ///
    /// <para>Backslashes become forward slashes first, which ffmpeg accepts on
    /// Windows and which keeps the separator out of the escaping question.</para>
    /// </summary>
    public static string Quote(string path) {
        var p = path.Replace("\\", "/", StringComparison.Ordinal);
        return "'" + p.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>0 to 100 from the interface, as the multiplier ffmpeg's volume
    /// filter wants. Clamped, because it becomes a process argument.</summary>
    public static string VolumeArg(int percent) =>
        (Math.Clamp(percent, 0, 100) / 100.0)
            .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
