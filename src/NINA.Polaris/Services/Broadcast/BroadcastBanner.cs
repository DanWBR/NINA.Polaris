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

namespace NINA.Polaris.Services.Broadcast;

/// <summary>What the session looks like right now, flattened out of half a
/// dozen services so the line can be composed and tested on its own.</summary>
public sealed record BannerFacts {
    public string? Target { get; init; }
    public string? Filter { get; init; }
    public double? ExposureSeconds { get; init; }
    public int? Gain { get; init; }
    public int? FrameCount { get; init; }
    /// <summary>Total integration kept so far, which is not the elapsed time:
    /// rejected frames, dithers and slews are in one and not the other.</summary>
    public double? IntegratedSeconds { get; init; }
    public double? Snr { get; init; }
    public double? GuideRmsArcsec { get; init; }
    public double? SensorTempC { get; init; }
}

/// <summary>
/// The line along the bottom of a broadcast: the numbers that move.
///
/// <para>Everything fixed for the session lives in the header instead. What is
/// here is what a viewer checks again a minute later, which is the whole
/// reason the two are separated.</para>
///
/// <para>Pure, because a broadcast rewrites this once a second for hours and
/// the failure worth guarding against is not a crash but a line that quietly
/// reads wrong: a zero that means "not measured", a frame count that shows
/// while the stack is empty, a temperature from a camera with no cooler.</para>
/// </summary>
public static class BroadcastBanner {

    private const string Sep = "  ·  ";

    public static string Compose(BannerFacts f) {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(f.Target)) parts.Add(f.Target!.Trim());

        var shot = Shot(f.Filter, f.ExposureSeconds, f.Gain);
        if (shot != null) parts.Add(shot);

        // A frame count of zero is the state before the first sub lands, and
        // "0 frames" on screen reads as something being wrong rather than as
        // something not having happened yet.
        if (f.FrameCount is > 0) parts.Add(f.FrameCount == 1 ? "1 frame" : $"{f.FrameCount} frames");

        var integrated = Duration(f.IntegratedSeconds);
        if (integrated != null) parts.Add(integrated);

        if (f.Snr is { } snr && snr > 0)
            parts.Add("SNR " + snr.ToString("0.#", CultureInfo.InvariantCulture));

        // Only while guiding. Zero is not a perfect night, it is no guider.
        if (f.GuideRmsArcsec is { } rms && rms > 0)
            parts.Add("RMS " + rms.ToString("0.00", CultureInfo.InvariantCulture) + "\"");

        // Nullable all the way down on purpose: a camera with no cooler
        // reports nothing, and 0.0 C would be a plausible looking lie.
        if (f.SensorTempC is { } temp && !double.IsNaN(temp))
            parts.Add(temp.ToString("0.#", CultureInfo.InvariantCulture) + " C");

        return string.Join(Sep, parts);
    }

    /// <summary>"L 120s g100", or as much of it as is known.</summary>
    private static string? Shot(string? filter, double? exposureSeconds, int? gain) {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter)) bits.Add(filter!.Trim());
        if (exposureSeconds is { } e && e > 0)
            bits.Add(Exposure(e));
        if (gain is { } g && g >= 0 && bits.Count > 0) bits.Add("g" + g.ToString(CultureInfo.InvariantCulture));
        return bits.Count == 0 ? null : string.Join(" ", bits);
    }

    /// <summary>Sub-second exposures are planetary and need their decimals;
    /// a 120 second sub does not want to read "120.0s".</summary>
    private static string Exposure(double seconds) =>
        seconds < 1
            ? seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s"
            : seconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";

    /// <summary>Integration time, in the unit a person would say it in.</summary>
    private static string? Duration(double? seconds) {
        if (seconds is not { } s || s < 1) return null;
        if (s < 60) return ((int)Math.Round(s)).ToString(CultureInfo.InvariantCulture) + " s";
        var minutes = (int)Math.Round(s / 60);
        if (minutes < 60) return minutes + " min";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} h" : $"{hours} h {rest} min";
    }
}
