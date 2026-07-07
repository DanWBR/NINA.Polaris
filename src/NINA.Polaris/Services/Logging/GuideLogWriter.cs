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
using System.Text;

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// Writes a PHD2-compatible Guide Log for a native-guider session, so the file
/// opens in PHD2 Log Viewer / phdlogview and shows the guide graph + RMS exactly
/// like a real PHD2 log. Format mirrors PHD2's <c>guidinglog.cpp</c> (Log version
/// 2.5): a version line, a "Guiding Begins" header block, the CSV column header,
/// one row per guide frame, INFO: lines for dither/settling, then "Guiding Ends"
/// + "Log Summary". For the EXTERNAL PHD2 backend we copy PHD2's own log instead
/// (see <see cref="GuideSessionLogService"/>); this writer is native-only.
///
/// The static <c>Format*</c> helpers are pure (unit-tested); the instance owns a
/// single append stream guarded by a lock (guide events fire from the guide-loop
/// thread).
/// </summary>
public sealed class GuideLogWriter : IDisposable {
    private const string GuideLogVersion = "2.5";
    // A plausible PHD2 version string keeps PHD2 Log Viewer's header parse happy;
    // the bracket (normally the OS name) marks this as a Polaris-produced log.
    private const string PhdVersion = "2.6.11";

    private readonly object _lock = new();
    private StreamWriter? _writer;

    public string Path { get; }

    public GuideLogWriter(string path) {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create,
            FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static CultureInfo Inv => CultureInfo.InvariantCulture;
    private static string Ts(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Inv);

    // ---- pure formatters (unit-tested) ----

    public static string FormatVersionLine(DateTime utc) =>
        $"PHD2 version {PhdVersion} [Polaris], Log version {GuideLogVersion}. Log enabled at {Ts(utc)}";

    /// <summary>The "Guiding Begins" header block + the CSV column header.
    /// Equipment/exposure/scale lines are free-form text PHD2 Log Viewer just
    /// displays; the column header is what it parses.</summary>
    public static string FormatGuidingBeginsHeader(DateTime utc, string profile,
            string camera, int exposureMs, double pixelScale, double lockX, double lockY) {
        var sb = new StringBuilder();
        sb.Append('\n');
        sb.Append("Guiding Begins at ").Append(Ts(utc)).Append('\n');
        sb.Append("Equipment Profile = ").Append(profile).Append('\n');
        sb.Append("Camera = ").Append(camera).Append('\n');
        sb.Append("Exposure = ").Append(exposureMs.ToString(Inv)).Append(" ms\n");
        sb.Append("Pixel scale = ").Append(pixelScale.ToString("F2", Inv)).Append(" arc-sec/px\n");
        sb.Append("Lock position = ").Append(lockX.ToString("F3", Inv)).Append(", ")
          .Append(lockY.ToString("F3", Inv)).Append(", Star position = ")
          .Append(lockX.ToString("F3", Inv)).Append(", ").Append(lockY.ToString("F3", Inv)).Append('\n');
        sb.Append("Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,"
                + "RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode");
        return sb.ToString();
    }

    /// <summary>One guide-frame row. Polaris doesn't separate camera-frame from
    /// mount-frame offsets, so the raw/guide distances reuse the RA/Dec pixel
    /// offsets — enough for PHD2 Log Viewer to compute RMS and draw the graph.</summary>
    public static string FormatFrameRow(int frame, double timeSec, double raPx, double decPx,
            int raDurationMs, string? raDir, int decDurationMs, string? decDir,
            double starMass, double snr) {
        string rd = raDurationMs > 0 ? DirChar(raDir) : "";
        string dd = decDurationMs > 0 ? DirChar(decDir) : "";
        return string.Format(Inv,
            "{0},{1:F3},\"Mount\",{2:F3},{3:F3},{2:F3},{3:F3},{2:F3},{3:F3},{4},{5},{6},{7},,,{8:F0},{9:F2},0",
            frame, timeSec, raPx, decPx, raDurationMs, rd, decDurationMs, dd, starMass, snr);
    }

    public static string FormatDitherInfo(double dx, double dy, double lockX, double lockY) =>
        string.Format(Inv, "INFO: DITHER by {0:F3}, {1:F3}, new lock pos = {2:F3}, {3:F3}",
            dx, dy, lockX, lockY);

    public static string FormatSettlingInfo(string message) => $"INFO: SETTLING STATE CHANGE, {message}";

    public static string FormatGuidingEnds(DateTime utc) => "Guiding Ends at " + Ts(utc);

    public static string FormatSummary(int calCnt, int guideCnt, double guideDurSec) =>
        string.Format(Inv, "Log closed at {0}\nLog Summary: calcnt:{1} gcnt:{2} gdur:{3:F0} gacnt:0",
            Ts(DateTime.UtcNow), calCnt, guideCnt, guideDurSec);

    // PHD2 uses single direction chars (N/S/E/W); accept either a char or a word.
    private static string DirChar(string? d) =>
        string.IsNullOrEmpty(d) ? "" : d.Trim().Substring(0, 1).ToUpperInvariant();

    // ---- instance writes (thread-safe append) ----

    public void WriteLine(string line) {
        lock (_lock) {
            _writer?.WriteLine(line);
        }
    }

    public void Dispose() {
        lock (_lock) {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* best-effort */ }
            _writer = null;
        }
    }
}
