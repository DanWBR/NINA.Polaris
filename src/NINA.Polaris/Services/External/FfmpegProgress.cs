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

using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.External;

/// <summary>One reading of how a running ffmpeg is doing.</summary>
public sealed record FfmpegProgress {
    public long Frame { get; init; }
    public double Fps { get; init; }
    public double BitrateKbps { get; init; }
    public TimeSpan OutTime { get; init; }
    /// <summary>Frames ffmpeg threw away because it could not keep up. The
    /// number that says the host is too slow for the chosen quality.</summary>
    public long DroppedFrames { get; init; }
    /// <summary>Frames repeated to reach the output rate. Expected and harmless
    /// here: the broadcast feeds 2 fps and sends 25.</summary>
    public long DuplicatedFrames { get; init; }
    /// <summary>Encoding speed relative to real time. Below 1.0 during a live
    /// broadcast means it is falling behind.</summary>
    public double Speed { get; init; }
    public long TotalSizeBytes { get; init; }
}

/// <summary>
/// Reads ffmpeg's running commentary off stderr.
///
/// <para>A batch encode can afford to keep the last few kilobytes of stderr and
/// look at them when the process ends. A broadcast cannot: it runs for hours,
/// and the only evidence that it is still working, or that it has quietly
/// started dropping every other frame, is this stream of numbers. So the lines
/// are parsed as they arrive and the watchdog is driven by the timestamp of the
/// last reading.</para>
///
/// <para>Both of ffmpeg's progress formats are accepted, because which one a
/// build emits is not something to depend on. <c>-progress pipe:2</c> writes one
/// <c>key=value</c> per line and a <c>progress=continue</c> to mark the end of a
/// group, which is what Polaris asks for; <c>-stats</c> writes the whole reading
/// on one line, terminated by a carriage return. Anything else on stderr is a
/// diagnostic and comes back as null so the caller can keep it for the error
/// message.</para>
///
/// <para>Pure and incremental, so the formats are pinned by tests rather than by
/// whatever ffmpeg happens to be installed on the machine of whoever last
/// touched this.</para>
/// </summary>
public sealed partial class FfmpegProgressParser {

    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private FfmpegProgress _last = new();

    /// <summary>The most recent complete reading, whether or not the line just
    /// fed produced one.</summary>
    public FfmpegProgress Last => _last;

    /// <summary>
    /// Feed one line of stderr. Returns a reading when the line completed one,
    /// null when the line was a diagnostic or only part of a group.
    /// </summary>
    public FfmpegProgress? Feed(string? line) {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var matches = PairRegex().Matches(line);
        if (matches.Count == 0) return null;

        var complete = false;
        foreach (Match m in matches) {
            var key = Normalise(m.Groups[1].Value);
            if (key == null) continue;
            if (key == "progress") { complete = true; continue; }
            _pending[key] = m.Groups[2].Value;
        }
        // One pair per line is the -progress form, which only becomes a reading
        // at its "progress=" terminator. Several pairs on one line is the -stats
        // form, which is a whole reading by itself.
        if (!complete && matches.Count < 3) return null;
        if (_pending.Count == 0) return null;

        _last = Snapshot();
        _pending.Clear();
        return _last;
    }

    /// <summary>Carry forward whatever this group did not mention: ffmpeg omits
    /// keys it has nothing to say about, and a status line that blanked its
    /// bitrate every few seconds would read as a fault that is not there.</summary>
    private FfmpegProgress Snapshot() => new() {
        Frame = Long("frame") ?? _last.Frame,
        Fps = Double("fps") ?? _last.Fps,
        BitrateKbps = Unit("bitrate") ?? _last.BitrateKbps,
        OutTime = Time() ?? _last.OutTime,
        DroppedFrames = Long("drop") ?? _last.DroppedFrames,
        DuplicatedFrames = Long("dup") ?? _last.DuplicatedFrames,
        Speed = Unit("speed") ?? _last.Speed,
        TotalSizeBytes = Size() ?? _last.TotalSizeBytes
    };

    /// <summary>The two formats spell the same quantity differently.</summary>
    private static string? Normalise(string key) => key switch {
        "frame" => "frame",
        "fps" => "fps",
        "bitrate" => "bitrate",
        "out_time" or "time" => "time",
        "out_time_us" => "time_us",
        // Not a typo of ffmpeg's that we get to fix: out_time_ms has always
        // carried microseconds. Reading it as milliseconds puts the broadcast
        // clock a thousand times fast.
        "out_time_ms" => "time_us",
        "drop_frames" or "drop" => "drop",
        "dup_frames" or "dup" => "dup",
        "speed" => "speed",
        "total_size" or "size" => "size",
        "progress" => "progress",
        _ => null
    };

    private long? Long(string key) =>
        _pending.TryGetValue(key, out var v)
        && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private double? Double(string key) =>
        _pending.TryGetValue(key, out var v)
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>A number with something stuck to the end of it: "1017.5kbits/s",
    /// "1.01x". Also "N/A", which is what the first second of every encode
    /// reports and which must not read as zero.</summary>
    private double? Unit(string key) {
        if (!_pending.TryGetValue(key, out var v)) return null;
        return double.TryParse(TrimUnit(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>"3421kB" in the one-line form, plain bytes in the other.</summary>
    private long? Size() {
        if (!_pending.TryGetValue("size", out var v)) return null;
        if (!double.TryParse(TrimUnit(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return null;
        if (v.Contains("MiB", StringComparison.OrdinalIgnoreCase)) return (long)(d * 1024 * 1024);
        if (v.Contains("kB", StringComparison.OrdinalIgnoreCase)) return (long)(d * 1024);
        return (long)d;
    }

    private TimeSpan? Time() {
        if (_pending.TryGetValue("time", out var t)
            && TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts)) return ts;
        if (_pending.TryGetValue("time_us", out var us)
            && long.TryParse(us, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
            return TimeSpan.FromMilliseconds(n / 1000.0);
        return null;
    }

    private static string TrimUnit(string v) {
        var end = 0;
        while (end < v.Length && (char.IsDigit(v[end]) || v[end] == '.' || v[end] == '-' || v[end] == '+')) end++;
        return v[..end];
    }

    // "frame=  247", "size=    3421kB", "out_time=00:00:12.000000". The value
    // can be separated from the key by spaces in the one-line form, which is
    // why this is a regex and not a split on whitespace.
    [GeneratedRegex(@"([a-z_0-9]+)=\s*([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PairRegex();
}
