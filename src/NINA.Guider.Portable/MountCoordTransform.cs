// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

// Camera<->mount coordinate transform + calibration model. Math ported from
// PHD2 (OpenPHDGuiding) mount.cpp, BSD-3-Clause. See licenses/PHD2-LICENSE.txt.

using NINA.Core.Enum;

namespace NINA.Guider.Portable;

/// <summary>Mount calibration: axis angles (radians) of RA/Dec in the guide-camera
/// frame and guide rates (pixels per ms) at the calibration declination.</summary>
public readonly record struct GuideCalibration(
    double XAngle,        // RA axis angle in camera frame (radians)
    double YAngle,        // Dec axis angle in camera frame (radians)
    double XRate,         // RA rate px/ms at calibration dec
    double YRate,         // Dec rate px/ms
    double DeclinationRad, // dec at calibration time (NaN = unknown)
    bool IsValid,
    double BacklashMs = 0,  // measured Dec backlash (slack take-up) in ms
    PierSide CalibrationPierSide = PierSide.pierUnknown) {  // pier side when calibrated

    public static readonly GuideCalibration Invalid =
        new(0, 0, 0, 0, double.NaN, false);

    /// <summary>Orthogonality error in degrees (ideal axes are 90 deg apart).</summary>
    public double OrthogonalityErrorDeg =>
        Math.Abs(MountCoordTransform.NormAngleDeg((XAngle - YAngle) * 180.0 / Math.PI) - 90.0);
}

/// <summary>Pure camera<->mount transforms + pulse-duration math.</summary>
public static class MountCoordTransform {
    public static double NormAngle(double a) {
        while (a <= -Math.PI) a += 2 * Math.PI;
        while (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }
    public static double NormAngleDeg(double a) {
        while (a <= -180.0) a += 360.0;
        while (a > 180.0) a -= 360.0;
        return a;
    }

    /// <summary>Project a camera-frame offset (px) onto the mount RA/Dec axes (px).</summary>
    public static (double ra, double dec) CameraToMount(in GuideCalibration cal, double dx, double dy) {
        double hyp = Math.Sqrt(dx * dx + dy * dy);
        if (hyp < 1e-9) return (0, 0);
        double cameraTheta = Math.Atan2(dy, dx);
        double yAngleError = NormAngle((cal.XAngle - cal.YAngle) + Math.PI / 2.0);
        double xa = cameraTheta - cal.XAngle;
        double ya = cameraTheta - (cal.XAngle + yAngleError);
        return (Math.Cos(xa) * hyp, Math.Sin(ya) * hyp);
    }

    /// <summary>Inverse: a mount RA/Dec offset (px) back to camera-frame (px).</summary>
    public static (double dx, double dy) MountToCamera(in GuideCalibration cal, double ra, double dec) {
        double hyp = Math.Sqrt(ra * ra + dec * dec);
        if (hyp < 1e-9) return (0, 0);
        double mountTheta = Math.Atan2(dec, ra);
        double yAngleError = NormAngle((cal.XAngle - cal.YAngle) + Math.PI / 2.0);
        if (Math.Abs(yAngleError) > Math.PI / 2.0) mountTheta = -mountTheta;
        double xa = mountTheta + cal.XAngle;
        return (Math.Cos(xa) * hyp, Math.Sin(xa) * hyp);
    }

    /// <summary>
    /// Adjust a calibration for a German-equatorial meridian flip (pier-side
    /// change). After a flip the OTA rotates 180 deg about the RA axis, so the
    /// RA direction reverses in the camera frame: add pi to the RA angle. The
    /// Dec angle is normally preserved; some mounts reverse the Dec output after
    /// a flip, in which case pi is also added to the Dec angle. Rates, the
    /// calibration declination and the measured backlash are unchanged. This
    /// mirrors PHD2's Mount::FlipCalibration (xAngle += pi, yAngle += pi only
    /// when the mount requires a Dec flip).
    /// </summary>
    public static GuideCalibration FlipForPierChange(in GuideCalibration cal, bool reverseDec) {
        if (!cal.IsValid) return cal;
        double newX = NormAngle(cal.XAngle + Math.PI);
        double newY = reverseDec ? NormAngle(cal.YAngle + Math.PI) : cal.YAngle;
        return cal with { XAngle = newX, YAngle = newY };
    }

    /// <summary>RA rate corrected for the current declination (xRate / cos dec).</summary>
    public static double RaRateAtDec(in GuideCalibration cal, double currentDecRad) {
        if (double.IsNaN(currentDecRad)) return cal.XRate;
        double c = Math.Cos(currentDecRad);
        if (Math.Abs(c) < 0.01) c = 0.01; // guard near the pole
        return cal.XRate / c;
    }

    /// <summary>Convert a correction distance (px) to a pulse duration (ms),
    /// clamped to [minMoveMs, maxDurationMs]. Returns 0 below minMoveMs.</summary>
    public static int ComputeMoveDurationMs(double distancePx, double ratePxPerMs,
                                            int minMoveMs, int maxDurationMs) {
        if (ratePxPerMs <= 0) return 0;
        int ms = (int)Math.Round(Math.Abs(distancePx) / ratePxPerMs);
        if (ms < minMoveMs) return 0;
        if (ms > maxDurationMs) ms = maxDurationMs;
        return ms;
    }
}