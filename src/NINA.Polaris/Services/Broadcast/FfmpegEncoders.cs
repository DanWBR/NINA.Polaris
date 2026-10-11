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

/// <summary>
/// Reads what the host's ffmpeg can actually do, from the output of
/// <c>ffmpeg -encoders</c> and <c>ffmpeg -filters</c>.
///
/// <para>This matters more here than anywhere else in Polaris, because a
/// broadcast runs for hours next to a capture loop. A board encoding H.264 in
/// software is spending CPU the camera needs, and on a Pi 4 it simply will not
/// keep up at any resolution worth watching. Every board that can do it in
/// hardware ships an ffmpeg that says so, and nothing in the tree was asking.</para>
///
/// <para>Pure string parsing, so the preference table is testable against real
/// ffmpeg output without ffmpeg.</para>
/// </summary>
public static class FfmpegEncoders {

    /// <summary>
    /// H.264 encoders on an ARM board, best first: <c>h264_rkmpp</c> is the
    /// RK3588's own (Orange Pi 5, Radxa), <c>h264_v4l2m2m</c> is the kernel
    /// mem2mem path a Raspberry Pi and most ARM boards expose.
    /// </summary>
    public static readonly string[] PreferenceArm = {
        "h264_rkmpp", "h264_v4l2m2m", "h264_vaapi", "libx264"
    };

    /// <summary>
    /// The same on x86.
    ///
    /// <para><c>h264_v4l2m2m</c> sits BELOW software here, and that is the
    /// point of having two lists: the stock Ubuntu x86 build reports it
    /// whether or not the machine has a kernel encoder, so preferring it by
    /// name picks an encoder that opens no device and fails at the first
    /// frame. Being compiled in is not being present.</para>
    /// </summary>
    public static readonly string[] PreferenceX86 = {
        "h264_qsv", "h264_nvenc", "h264_vaapi", "libx264", "h264_v4l2m2m"
    };

    /// <summary>The list for this architecture. Kept public so the service can
    /// report what it considered, not only what it picked.</summary>
    public static string[] PreferenceFor(bool isArm) => isArm ? PreferenceArm : PreferenceX86;

    /// <summary>Every encoder name the binary reports.</summary>
    public static IReadOnlyCollection<string> ParseEncoders(string? encodersOutput) {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(encodersOutput)) return found;
        // " V....D libx264              libx264 H.264 / AVC ..." : the name is
        // the second whitespace-separated field, after the capability flags.
        foreach (var raw in encodersOutput.Split('\n')) {
            var line = raw.TrimEnd('\r');
            if (line.Length < 8 || !line.StartsWith(' ')) continue;
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var flags = parts[0];
            // The flag column is fixed width and starts with the media type.
            if (flags.Length < 6 || (flags[0] != 'V' && flags[0] != 'A')) continue;
            found.Add(parts[1]);
        }
        return found;
    }

    /// <summary>
    /// The encoder to use, given what the binary reports. Falls back to
    /// <c>libx264</c> even when the probe found nothing at all: a build without
    /// it is not a build Polaris can do anything about, and failing at spawn
    /// with ffmpeg's own message beats refusing on a guess.
    /// </summary>
    public static string Choose(IReadOnlyCollection<string>? available, bool isArm = false) {
        if (available == null || available.Count == 0) return "libx264";
        foreach (var e in PreferenceFor(isArm)) if (available.Contains(e)) return e;
        return "libx264";
    }

    /// <summary>
    /// Drop encoders whose kernel device is not there.
    ///
    /// <para>Pure: the caller supplies the existence test (<c>File.Exists</c>
    /// over <c>/dev/video*</c> or <c>/dev/dri/*</c>), so the rule is testable
    /// without a board. This is the second half of "compiled in is not
    /// present": on an ARM host with no encoder node, v4l2m2m has to lose to
    /// software as well.</para>
    /// </summary>
    public static IReadOnlyCollection<string> DropUnusable(
            IReadOnlyCollection<string> available, Func<string, bool> deviceExists) {
        var keep = new HashSet<string>(available, StringComparer.Ordinal);
        if (keep.Contains("h264_v4l2m2m") && !deviceExists("/dev/video11")
            && !deviceExists("/dev/video10") && !deviceExists("/dev/video-enc0")) keep.Remove("h264_v4l2m2m");
        if (keep.Contains("h264_rkmpp") && !deviceExists("/dev/mpp_service")) keep.Remove("h264_rkmpp");
        if (keep.Contains("h264_vaapi") && !deviceExists("/dev/dri/renderD128")) keep.Remove("h264_vaapi");
        return keep;
    }

    /// <summary>True when the encoder is one the board does in silicon, which
    /// is what decides whether a high quality setting is honest on this host.</summary>
    public static bool IsHardware(string? encoder) =>
        encoder is "h264_rkmpp" or "h264_v4l2m2m" or "h264_qsv" or "h264_nvenc" or "h264_vaapi";

    /// <summary>Is a filter compiled into this build? The overlay card needs
    /// <c>overlay</c> and the banner needs <c>drawtext</c> (which in turn needs
    /// libfreetype). Both are in the Debian and Ubuntu builds; a build without
    /// them broadcasts the bare image rather than failing.</summary>
    public static bool HasFilter(string? filtersOutput, string filter) {
        if (string.IsNullOrWhiteSpace(filtersOutput) || string.IsNullOrWhiteSpace(filter)) return false;
        foreach (var raw in filtersOutput.Split('\n')) {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // " T.C drawtext          V->V       Draw text on top of video frames"
            if (parts.Length >= 2 && string.Equals(parts[1], filter, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
