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

/// <summary>One of the three quality settings, resolution and bitrate together.</summary>
public sealed record BroadcastQuality(string Id, int Width, int Height, int BitrateKbps, string Label) {
    /// <summary>The allowlist. A quality id becomes process arguments, so it is
    /// matched against this and never interpolated from what arrived on the wire,
    /// the same rule <c>RcloneArgs.IsValidBandwidth</c> follows.</summary>
    public static readonly BroadcastQuality[] All = {
        new("low",    854,  480, 1000, "480p, about 0.5 GB per hour"),
        new("medium", 1280, 720, 2500, "720p, about 1.1 GB per hour"),
        new("high",   1920, 1080, 4500, "1080p, about 2.0 GB per hour")
    };

    public static BroadcastQuality Parse(string? id) {
        foreach (var q in All) if (string.Equals(q.Id, id, StringComparison.OrdinalIgnoreCase)) return q;
        return All[1];
    }

    public static bool IsValid(string? id) {
        foreach (var q in All) if (string.Equals(q.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>Everything the argument builder needs, so the builder itself stays pure.</summary>
public sealed record BroadcastPlan {
    public required BroadcastQuality Quality { get; init; }
    public required string Encoder { get; init; }
    /// <summary>Frames per second written into ffmpeg's stdin. Two is plenty for
    /// deep sky: the picture changes once per sub. The encoder duplicates up to
    /// <see cref="OutputFps"/>, which is what the platforms expect to receive.</summary>
    public int InputFps { get; init; } = 2;
    public int OutputFps { get; init; } = 25;
    /// <summary>RTMP base URL, without the key. Null for record-only.</summary>
    public string? RtmpUrl { get; init; }
    /// <summary>The stream key. Never logged, never returned by an endpoint.</summary>
    public string? StreamKey { get; init; }
    /// <summary>Where to write the MP4, when recording is on.</summary>
    public string? RecordPath { get; init; }
    /// <summary>PNG the object card is rendered into, composited with the overlay
    /// filter. Null when the card is off or the filter is missing.</summary>
    public string? CardPngPath { get; init; }
    /// <summary>Text file the banner line is read from, reloaded every frame.
    /// Null when the banner is off or drawtext is missing.</summary>
    public string? BannerTextPath { get; init; }
    public string? FontFile { get; init; }
}

/// <summary>
/// Builds the ffmpeg argument list for a live broadcast.
///
/// <para>A list, never a string: the stream key and the file paths go in as
/// separate argv entries so nothing has to be quoted and nothing can be split
/// on a space in a target name. <see cref="Redact"/> exists because the key is
/// in there and this is the only shape of it anyone should ever log.</para>
///
/// <para>What the platforms require, and why the arguments look like this:
/// H.264 video and an AAC audio track (YouTube rejects a video-only RTMP
/// stream, hence the silent <c>anullsrc</c>), and a keyframe at least every
/// four seconds, hence the GOP of twice the output frame rate.</para>
/// </summary>
public static class BroadcastArgs {

    /// <summary>The full ffmpeg argv, minus the binary itself.</summary>
    public static List<string> Build(BroadcastPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);
        var q = plan.Quality;
        var inFps = Math.Clamp(plan.InputFps, 1, 60);
        var outFps = Math.Clamp(plan.OutputFps, 5, 60);

        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-stats" };

        // 0: the composed picture, raw over stdin.
        a.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "rgb24",
                           "-s", $"{q.Width}x{q.Height}",
                           "-framerate", inFps.ToString(), "-i", "pipe:0" });

        // 1: the object card, a PNG rewritten whenever the target changes.
        var hasCard = !string.IsNullOrWhiteSpace(plan.CardPngPath);
        if (hasCard) a.AddRange(new[] { "-f", "image2", "-loop", "1", "-i", plan.CardPngPath! });

        // Last: silence, because a video-only RTMP stream is refused.
        a.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo" });

        var filter = BuildFilter(plan, hasCard);
        if (filter != null) a.AddRange(new[] { "-filter_complex", filter, "-map", "[v]" });
        else a.AddRange(new[] { "-map", "0:v" });
        a.AddRange(new[] { "-map", hasCard ? "2:a" : "1:a" });

        a.AddRange(new[] { "-c:v", plan.Encoder,
                           "-b:v", $"{q.BitrateKbps}k",
                           "-maxrate", $"{q.BitrateKbps}k",
                           "-bufsize", $"{q.BitrateKbps * 2}k",
                           "-pix_fmt", "yuv420p",
                           "-r", outFps.ToString(),
                           "-g", (outFps * 2).ToString() });
        // Software x264 needs telling to hurry; the hardware encoders have no
        // equivalent knob and reject the option.
        if (plan.Encoder == "libx264") a.AddRange(new[] { "-preset", "veryfast", "-tune", "zerolatency" });
        a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-ar", "44100" });

        var publishing = !string.IsNullOrWhiteSpace(plan.RtmpUrl) && !string.IsNullOrWhiteSpace(plan.StreamKey);
        if (publishing) a.AddRange(new[] { "-f", "flv", JoinUrl(plan.RtmpUrl!, plan.StreamKey!) });
        if (!string.IsNullOrWhiteSpace(plan.RecordPath))
            a.AddRange(new[] { "-f", "mp4", "-movflags", "+faststart", plan.RecordPath! });
        if (!publishing && string.IsNullOrWhiteSpace(plan.RecordPath))
            throw new InvalidOperationException("A broadcast needs somewhere to go: an RTMP destination, a recording, or both.");
        return a;
    }

    /// <summary>The same arguments with the key replaced, for logs and for the
    /// status block. The only rendering of the command that may leave the host.</summary>
    public static string Redact(IEnumerable<string> args, string? streamKey) {
        var sb = new System.Text.StringBuilder();
        foreach (var raw in args) {
            var a = raw;
            if (!string.IsNullOrWhiteSpace(streamKey) && a.Contains(streamKey, StringComparison.Ordinal))
                a = a.Replace(streamKey, "<stream-key>", StringComparison.Ordinal);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(a.Contains(' ') ? '"' + a + '"' : a);
        }
        return sb.ToString();
    }

    /// <summary>Card over the picture, then the banner text over both. Null when
    /// there is nothing to draw, so the simple case spawns a simple ffmpeg.</summary>
    private static string? BuildFilter(BroadcastPlan plan, bool hasCard) {
        var q = plan.Quality;
        var steps = new List<string>();
        var cur = "0:v";
        if (hasCard) {
            // Top right, inset by 2% of the width so it never touches the edge
            // on a player that overscans.
            var pad = Math.Max(8, q.Width / 50);
            steps.Add($"[{cur}][1:v]overlay=x=W-w-{pad}:y={pad}[withcard]");
            cur = "withcard";
        }
        if (!string.IsNullOrWhiteSpace(plan.BannerTextPath)) {
            var fontSize = Math.Max(14, q.Height / 34);
            var pad = Math.Max(8, q.Width / 50);
            var font = string.IsNullOrWhiteSpace(plan.FontFile)
                ? "" : $"fontfile={Escape(plan.FontFile!)}:";
            // reload=1: the service rewrites the file once a second and ffmpeg
            // picks it up without a restart, which is the whole reason the
            // banner is a file rather than a burned-in string.
            steps.Add($"[{cur}]drawtext={font}textfile={Escape(plan.BannerTextPath!)}:reload=1"
                    + $":fontcolor=white:fontsize={fontSize}:x={pad}:y=h-th-{pad}"
                    + $":box=1:boxcolor=black@0.55:boxborderw={fontSize / 2}[v]");
            return string.Join(";", steps);
        }
        if (steps.Count == 0) return null;
        // Nothing after the overlay, so name its output [v] instead.
        steps[^1] = steps[^1].Replace("[withcard]", "[v]", StringComparison.Ordinal);
        return string.Join(";", steps);
    }

    /// <summary>filter_complex is its own little language: colons separate
    /// options and backslashes escape, so a Windows path or a target with a
    /// colon in it would otherwise end the option early.</summary>
    private static string Escape(string path) =>
        path.Replace("\\", "/", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static string JoinUrl(string baseUrl, string key) =>
        baseUrl.TrimEnd('/') + "/" + key.Trim();
}
