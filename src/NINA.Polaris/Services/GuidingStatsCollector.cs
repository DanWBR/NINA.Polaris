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

using NINA.Image.ImageData;

namespace NINA.Polaris.Services;

/// <summary>
/// Summarises how the mount tracked during one exposure, for stamping into the
/// saved frame's header.
///
/// The point is per-FRAME numbers, not session numbers: the guider's running
/// RMS covers the whole session, so a single bad gust stays in the average all
/// night and every frame looks equally mediocre. Scoping the statistics to the
/// exposure window is what lets the operator sort subs by guiding and see the
/// effect on the stars.
/// </summary>
public static class GuidingStatsCollector {

    /// <summary>RMS of the guide errors recorded inside [start, end], in
    /// arcseconds. Steps outside the window are ignored, so a frame taken
    /// while guiding was paused simply reports no samples.</summary>
    public static ImageMetaData.GuidingInfo Summarise(
            IEnumerable<GuideStep>? steps, DateTime startUtc, DateTime endUtc,
            string backend = "") {
        var info = new ImageMetaData.GuidingInfo { Backend = backend ?? "" };
        if (steps == null) return info;

        double sumRaSq = 0, sumDecSq = 0, peakSq = 0;
        int n = 0;
        foreach (var s in steps) {
            var t = s.Timestamp;
            if (t < startUtc || t > endUtc) continue;
            double ra = s.RaArcsec, dec = s.DecArcsec;
            if (double.IsNaN(ra) || double.IsNaN(dec)) continue;
            sumRaSq += ra * ra;
            sumDecSq += dec * dec;
            double totSq = ra * ra + dec * dec;
            if (totSq > peakSq) peakSq = totSq;
            n++;
        }
        if (n == 0) return info;

        // RMS about zero, not about the mean: zero IS the target here (the
        // guider's job is to hold the star on the lock position), so a
        // consistent offset is a real tracking error, not something to
        // subtract away as a "mean".
        info.RmsRaArcsec = Math.Sqrt(sumRaSq / n);
        info.RmsDecArcsec = Math.Sqrt(sumDecSq / n);
        info.RmsTotalArcsec = Math.Sqrt((sumRaSq + sumDecSq) / n);
        info.PeakArcsec = Math.Sqrt(peakSq);
        info.SampleCount = n;
        return info;
    }
}
