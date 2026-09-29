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

/// <summary>The rig, flattened out of the equipment profile so the line that
/// describes it can be composed and tested without a profile.</summary>
public sealed record RigFacts {
    public string? RigName { get; init; }
    public double FocalLengthMm { get; init; }
    public double ApertureMm { get; init; }
    public string? Camera { get; init; }
    public string? Mount { get; init; }
    public string? FilterWheel { get; init; }
    public string? Focuser { get; init; }
    public string? GuideCamera { get; init; }
    public string? Guider { get; init; }
}

/// <summary>
/// The two lines of the header strip: what the broadcast is, and what it is
/// being made with.
///
/// <para>"What scope is that?" is the question asked in every astrophotography
/// live chat, so the answer is on screen from the first frame rather than
/// waiting for someone to type it. It sits at the top and not in the bottom
/// banner because it does not change: the banner is where the numbers that
/// move live, and a fixed string of gear names mixed into them makes the part
/// that is actually updating harder to find.</para>
/// </summary>
public static class RigLine {

    /// <summary>The name of the rig as configured, unless it is the name every
    /// untouched install has, which tells a viewer nothing.</summary>
    private static bool IsRealName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), "Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first line. The operator's own title when they set one,
    /// and the rig name after it when the rig has been named.</summary>
    public static string Title(string? configuredTitle, string? rigName) {
        var title = string.IsNullOrWhiteSpace(configuredTitle) ? "Polaris Live Stream" : configuredTitle.Trim();
        return IsRealName(rigName) ? title + "  ·  " + rigName!.Trim() : title;
    }

    /// <summary>
    /// The second line: optics first, then the devices, skipping whatever is
    /// not configured. Never returns something half formed like "f/" or a
    /// string of separators with nothing between them.
    /// </summary>
    public static string Equipment(RigFacts rig) {
        var parts = new List<string>();

        var optics = Optics(rig.FocalLengthMm, rig.ApertureMm);
        if (optics != null) parts.Add(optics);
        Add(parts, rig.Camera);
        Add(parts, rig.Mount);
        Add(parts, rig.FilterWheel);
        Add(parts, rig.Focuser);
        // The guide camera reads as equipment; the guider is software, so it
        // is appended to the camera rather than listed as a device of its own.
        var guiding = Guiding(rig.GuideCamera, rig.Guider);
        if (guiding != null) parts.Add(guiding);

        return string.Join("  ·  ", parts);
    }

    /// <summary>"550 mm f/5.5", or just the focal length when the aperture was
    /// never entered, which is the common case for a profile set up in a
    /// hurry. An f-ratio computed from a zero aperture would be an infinity.</summary>
    private static string? Optics(double focalLengthMm, double apertureMm) {
        if (focalLengthMm <= 0) return null;
        var fl = focalLengthMm.ToString(focalLengthMm < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + " mm";
        if (apertureMm <= 0) return fl;
        var ratio = focalLengthMm / apertureMm;
        return fl + " f/" + ratio.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string? Guiding(string? guideCamera, string? guider) {
        var cam = Clean(guideCamera);
        var soft = Clean(guider);
        if (cam == null && soft == null) return null;
        if (cam == null) return soft;
        return soft == null ? cam : cam + " + " + soft;
    }

    private static void Add(List<string> parts, string? value) {
        var v = Clean(value);
        if (v != null) parts.Add(v);
    }

    private static string? Clean(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        // A driver that was selected and then unselected can leave these
        // behind, and "None" on the header of a broadcast looks like a fault.
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (v.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return v;
    }
}
