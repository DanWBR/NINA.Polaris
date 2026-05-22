using System.Globalization;

namespace NINA.Mount.SynScanWifi;

/// <summary>
/// Encode + decode the ASCII sexagesimal payloads the LX200 / SynScan
/// command set uses. The radio side of the driver only cares about
/// bytes in / bytes out; this class isolates the format conversions
/// so they're unit-testable without UDP.
///
/// LX200 conventions:
/// <list type="bullet">
/// <item><c>RA</c> as <c>HH:MM:SS</c> — hours 0..23, no sign.</item>
/// <item><c>Dec</c> as <c>sDD*MM:SS</c> — leading <c>+</c> or <c>-</c>,
/// degrees 0..90, the literal <c>*</c> between degrees and minutes.
/// (Some firmware versions accept <c>°</c> too; <c>*</c> is the
/// canonical character used in the published spec.)</item>
/// </list>
/// </summary>
public static class SynScanCommandCodec {

    /// <summary>Format an RA value (hours, 0..24) as <c>HH:MM:SS</c>
    /// for the <c>:Sr</c> command. Seconds are rounded to the nearest
    /// integer — sub-second precision isn't useful at typical SynScan
    /// pointing accuracy (~30 arcsec).</summary>
    public static string FormatRA(double raHours) {
        raHours = ((raHours % 24) + 24) % 24;   // wrap into [0, 24)
        int h = (int)Math.Floor(raHours);
        double rem = (raHours - h) * 60.0;
        int m = (int)Math.Floor(rem);
        int s = (int)Math.Round((rem - m) * 60.0);
        if (s == 60) { s = 0; m++; }
        if (m == 60) { m = 0; h = (h + 1) % 24; }
        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    /// <summary>Format a Dec value (degrees, -90..+90) as
    /// <c>sDD*MM:SS</c> for the <c>:Sd</c> command. Sign is always
    /// emitted (LX200 spec requires it). Clamps to [-90, +90] —
    /// callers should already be passing astronomy-valid values.</summary>
    public static string FormatDec(double decDeg) {
        decDeg = Math.Clamp(decDeg, -90, 90);
        char sign = decDeg < 0 ? '-' : '+';
        double abs = Math.Abs(decDeg);
        int d = (int)Math.Floor(abs);
        double rem = (abs - d) * 60.0;
        int m = (int)Math.Floor(rem);
        int s = (int)Math.Round((rem - m) * 60.0);
        if (s == 60) { s = 0; m++; }
        if (m == 60) { m = 0; d++; }
        return $"{sign}{d:D2}*{m:D2}:{s:D2}";
    }

    /// <summary>Parse the response of <c>:GR#</c> — <c>HH:MM:SS#</c> —
    /// into RA hours. Trailing <c>#</c> and surrounding whitespace are
    /// tolerated. Returns null on malformed input so the caller can
    /// distinguish a transport failure from a parse failure.</summary>
    public static double? ParseRA(string? response) {
        if (string.IsNullOrEmpty(response)) return null;
        var s = response.Trim().TrimEnd('#').Trim();
        var parts = s.Split(':');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return null;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec)) return null;
        return h + m / 60.0 + sec / 3600.0;
    }

    /// <summary>Parse the response of <c>:GD#</c> — <c>sDD*MM:SS#</c> —
    /// into Dec degrees. Tolerates both <c>*</c> and <c>°</c>
    /// (some firmware variants emit one or the other) as the
    /// degree-minute separator.</summary>
    public static double? ParseDec(string? response) {
        if (string.IsNullOrEmpty(response)) return null;
        var s = response.Trim().TrimEnd('#').Trim();
        if (s.Length == 0) return null;

        int signMul = 1;
        if (s[0] == '+' || s[0] == '-') {
            if (s[0] == '-') signMul = -1;
            s = s.Substring(1);
        }
        // Normalise the degree separator. Some mounts emit ° (0xB0 in
        // Latin-1), some emit *, some emit a literal space.
        s = s.Replace('°', '*').Replace(' ', '*');
        var firstSep = s.IndexOf('*');
        if (firstSep < 0) return null;

        var degStr = s.Substring(0, firstSep);
        var rest = s.Substring(firstSep + 1);
        var mmParts = rest.Split(':');
        if (mmParts.Length != 2) return null;

        if (!int.TryParse(degStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) return null;
        if (!int.TryParse(mmParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return null;
        if (!int.TryParse(mmParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec)) return null;
        return signMul * (d + m / 60.0 + sec / 3600.0);
    }
}
