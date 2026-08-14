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

namespace NINA.Polaris.Services;

/// <summary>
/// Pure, unit-testable decision helpers for mount-slew safety. Born from a
/// near tripod-strike on a ZWO AM3: after a meridian flip + a safety-guard
/// trip, a fresh SKY "Go To" made the mount try to un-flip and swing the OTA
/// the long way down toward the tripod. These helpers let the slew path
/// (a) skip a redundant GoTo when the mount is already sitting on the target
/// (removing the trigger that made the AM3 un-flip), (b) flag a large or
/// near-meridian slew — or a target below the altitude floor — for a
/// confirmation before the mount moves, and (c) decide when a slew that is
/// driving the OTA below the altitude floor must be aborted mid-flight.
/// </summary>
public static class MountSlewSafety {
    /// <summary>A GoTo landing within this of the current pointing is treated as
    /// "already on target": skip the slew and go straight to center-only, so we
    /// never re-command a full GoTo the mount could satisfy by un-flipping.</summary>
    public const double AlreadyOnTargetDeg = 2.0;

    /// <summary>Default move size (deg) at or above which a slew is flagged for
    /// confirmation, used when a rig supplies no per-rig override.</summary>
    public const double LargeMoveDeg = 60.0;

    /// <summary>Default anti-crash altitude floor (deg) used when a rig supplies
    /// no per-rig override.</summary>
    public const double AltitudeFloorDeg = 5.0;

    /// <summary>The altitude floor to apply WHILE a meridian flip is executing,
    /// when a rig supplies no per-rig override.
    ///
    /// A meridian flip is a deliberate slew to a known-safe target, but the
    /// transit gets there by swinging the OTA across the sky, and near the
    /// equator (low |latitude|) the polar axis lies almost flat, so that transit
    /// legitimately dips close to the horizon before rising to the target.
    /// Field, lat -5°: a valid AM3 flip to a 62°-altitude target dipped to 4°,
    /// and the normal 5° floor aborted it mid-flip. During a flip the floor
    /// therefore drops to the horizon: a transit passing low is expected, while
    /// the OTA actually going BELOW the horizon is never part of a real flip and
    /// still trips. The floor returns to <see cref="AltitudeFloorDeg"/> the
    /// instant the flip finishes, so every ordinary slew keeps full protection.</summary>
    public const double FlipTransitFloorDeg = 0.0;

    /// <summary>A target within this many minutes of the meridian is flagged:
    /// a fresh GoTo can pick the un-flipped pier side and swing the long way.</summary>
    public const double NearMeridianMinutes = 10.0;

    /// <summary>Great-circle separation between two equatorial coords, in degrees.</summary>
    public static double AngularSeparationDeg(double ra1Hours, double dec1Deg,
            double ra2Hours, double dec2Deg) {
        double ra1 = ra1Hours * 15.0 * Math.PI / 180.0;
        double d1 = dec1Deg * Math.PI / 180.0;
        double ra2 = ra2Hours * 15.0 * Math.PI / 180.0;
        double d2 = dec2Deg * Math.PI / 180.0;
        double cos = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(ra1 - ra2);
        return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI;
    }

    /// <summary>Target hour angle in minutes of time (± = west/east of meridian),
    /// normalised to (-720, 720].</summary>
    public static double TargetHaMinutes(double targetRaHours, double lstHours) {
        double ha = lstHours - targetRaHours;
        while (ha > 12) ha -= 24;
        while (ha < -12) ha += 24;
        return ha * 60.0;
    }

    /// <summary>Should the altitude-floor guard abort a slew? True only when the
    /// floor is enabled (>0), the mount is actively slewing, and the current
    /// pointing has dropped below the floor — i.e. an OTA being driven down
    /// toward the pier/tripod. A parked or idle mount is never slewing, so it
    /// can sit low without tripping.</summary>
    public static bool ShouldAbortForAltitude(double currentAltDeg, double minAltFloorDeg,
            bool isSlewing) {
        if (minAltFloorDeg <= 0) return false;
        if (!isSlewing) return false;
        if (double.IsNaN(currentAltDeg)) return false;
        return currentAltDeg < minAltFloorDeg;
    }

    /// <summary>Should the altitude-floor guard abort a slew that is a meridian
    /// flip in progress? Unlike <see cref="ShouldAbortForAltitude"/>, a floor of
    /// 0 here means "the horizon" (abort below it), not "off": a flip's transit
    /// legitimately dips low, so the floor is deliberately at/near the horizon and
    /// must still catch the OTA actually going below it. The on/off switch for
    /// flips is the caller's SafetyStopEnabled, checked before this. NaN altitude
    /// or a stationary mount never aborts.</summary>
    public static bool ShouldAbortForFlipTransit(double currentAltDeg, double floorDeg,
            bool isSlewing) {
        if (!isSlewing) return false;
        if (double.IsNaN(currentAltDeg)) return false;
        return currentAltDeg < floorDeg;
    }

    /// <summary>Evaluate a proposed GoTo before it is issued. <paramref name="mountRaHours"/>
    /// / <paramref name="mountDecDeg"/> may be NaN / out of range when the mount
    /// doesn't report a usable position (then the move-size and already-on-target
    /// tests are skipped).</summary>
    public static SlewSafetyVerdict Evaluate(
            double mountRaHours, double mountDecDeg,
            double targetRaHours, double targetDecDeg,
            double latDeg, double lonDeg, DateTime utc,
            double minAltFloorDeg,
            double largeMoveDeg = LargeMoveDeg) {
        double lst = MeridianFlipService.ComputeLstHours(utc, lonDeg);
        double haMin = TargetHaMinutes(targetRaHours, lst);
        var (targetAlt, _) = AltitudeService.RaDecToAltAz(targetRaHours, targetDecDeg, utc, latDeg, lonDeg);

        bool haveMount = !double.IsNaN(mountRaHours) && !double.IsNaN(mountDecDeg)
            && mountRaHours >= 0 && mountRaHours <= 24 && mountDecDeg >= -90 && mountDecDeg <= 90;
        double moveDeg = haveMount
            ? AngularSeparationDeg(mountRaHours, mountDecDeg, targetRaHours, targetDecDeg)
            : double.NaN;

        bool alreadyOnTarget = haveMount && moveDeg <= AlreadyOnTargetDeg;
        bool belowFloor = minAltFloorDeg > 0 && targetAlt < minAltFloorDeg;
        bool largeMove = haveMount && largeMoveDeg > 0 && moveDeg >= largeMoveDeg;
        // Near-meridian only matters if we'd actually issue a fresh GoTo (not
        // already parked on the target) — that's the AM3 un-flip case.
        bool nearMeridian = Math.Abs(haMin) <= NearMeridianMinutes && !alreadyOnTarget;

        var reasons = new List<string>();
        if (belowFloor)
            reasons.Add($"the target is only {targetAlt:F0}° above the horizon (floor {minAltFloorDeg:F0}°)");
        if (largeMove)
            reasons.Add($"a large slew of ~{moveDeg:F0}°");
        if (nearMeridian)
            reasons.Add($"the target is near the meridian ({Math.Abs(haMin):F0} min) — the mount may swing the long way / un-flip");

        bool warn = belowFloor || largeMove || nearMeridian;
        return new SlewSafetyVerdict(
            Warn: warn,
            AlreadyOnTarget: alreadyOnTarget,
            BelowAltFloor: belowFloor,
            MoveDeg: moveDeg,
            TargetAltDeg: targetAlt,
            HaMinutes: haMin,
            Reason: reasons.Count > 0 ? string.Join("; ", reasons) : "");
    }
}

/// <summary>Result of a pre-slew safety evaluation. <see cref="Warn"/> means the
/// caller should confirm before slewing; <see cref="AlreadyOnTarget"/> means the
/// mount is essentially on the target and the initial slew can be skipped.</summary>
public record SlewSafetyVerdict(
    bool Warn,
    bool AlreadyOnTarget,
    bool BelowAltFloor,
    double MoveDeg,
    double TargetAltDeg,
    double HaMinutes,
    string Reason);
