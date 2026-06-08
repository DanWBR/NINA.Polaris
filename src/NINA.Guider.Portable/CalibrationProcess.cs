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

// Calibration state machine. Math (rate = dist/(steps*pulseMs), angle = atan2,
// Dec backlash clearing) ported from PHD2 (OpenPHDGuiding) scope.cpp,
// BSD-3-Clause.

using NINA.Core.Enum;

namespace NINA.Guider.Portable;

/// <summary>What the guider should do this calibration tick.</summary>
public readonly record struct CalibrationStep(
    bool Pulse, GuideDirections Direction, int DurationMs, bool Done, bool Failed, string Phase);

/// <summary>
/// Drives mount calibration: pulse WEST until the star moves a threshold distance
/// (measure RA angle+rate), recenter EAST, clear Dec backlash going SOUTH (count
/// the slack take-up = backlash), then continue SOUTH to measure Dec angle+rate.
/// The host feeds the current star centroid each tick and applies the returned
/// pulse. Happy-path port of PHD2's calibration incl. backlash measurement.
/// </summary>
public sealed class CalibrationProcess {
    private enum Phase { Init, West, EastRecenter, DecClear, DecMeasure, Done, Failed }

    private readonly int _pulseMs;
    private readonly double _distThresholdPx;
    private readonly double _catchThresholdPx; // motion that means backlash is taken up
    private readonly int _maxSteps;
    private readonly double _decRad;

    private Phase _phase = Phase.Init;
    private double _startX, _startY;     // phase start position
    private double _decStartX, _decStartY; // Dec measure start (after backlash cleared)
    private int _stepCount;
    private int _westSteps;
    private int _decSteps;
    private int _backlashSteps;
    private double _xAngle, _xRate, _yAngle, _yRate, _backlashMs;

    public GuideCalibration Result { get; private set; } = GuideCalibration.Invalid;

    /// <summary>Number of WEST pulses used to measure the RA rate/angle.</summary>
    public int RaSteps => _westSteps;
    /// <summary>Number of SOUTH pulses used to measure the Dec rate/angle (after
    /// backlash was cleared).</summary>
    public int DecSteps => _decSteps;
    /// <summary>Backlash-clearing pulses counted before Dec started moving.</summary>
    public int BacklashSteps => _backlashSteps;

    public CalibrationProcess(int pulseMs = 1000, double distThresholdPx = 25.0,
                              int maxSteps = 60, double declinationRad = double.NaN,
                              double catchThresholdPx = 3.0) {
        _pulseMs = Math.Max(50, pulseMs);
        _distThresholdPx = Math.Max(5.0, distThresholdPx);
        _catchThresholdPx = Math.Max(1.0, catchThresholdPx);
        _maxSteps = Math.Max(4, maxSteps);
        _decRad = declinationRad;
    }

    /// <summary>Advance the state machine given the latest measured star position.</summary>
    public CalibrationStep Tick(double curX, double curY) {
        switch (_phase) {
            case Phase.Init:
                _startX = curX; _startY = curY; _stepCount = 0;
                _phase = Phase.West;
                return West();

            case Phase.West: {
                _stepCount++;
                double d = Dist(curX, curY, _startX, _startY);
                if (d >= _distThresholdPx) {
                    _xAngle = Math.Atan2(curY - _startY, curX - _startX);
                    _xRate = d / (_stepCount * (double)_pulseMs);
                    _westSteps = _stepCount;
                    _phase = Phase.EastRecenter; _stepCount = 0;
                    return East();
                }
                if (_stepCount >= _maxSteps) { _phase = Phase.Failed; return Fail("RA did not move enough"); }
                return West();
            }

            case Phase.EastRecenter: {
                _stepCount++;
                if (_stepCount >= _westSteps) {
                    // Begin Dec: clear backlash going south, counting slack steps.
                    _startX = curX; _startY = curY;
                    _stepCount = 0; _backlashSteps = 0;
                    _phase = Phase.DecClear;
                    return South();
                }
                return East();
            }

            case Phase.DecClear: {
                _backlashSteps++;
                double moved = Dist(curX, curY, _startX, _startY);
                if (moved >= _catchThresholdPx) {
                    // Star caught: slack is taken up. Backlash = clearing pulses so far.
                    _backlashMs = _backlashSteps * (double)_pulseMs;
                    _decStartX = curX; _decStartY = curY;
                    _stepCount = 0;
                    _phase = Phase.DecMeasure;
                    return South();
                }
                if (_backlashSteps >= _maxSteps) { _phase = Phase.Failed; return Fail("Dec did not move (backlash/jam?)"); }
                return South();
            }

            case Phase.DecMeasure: {
                _stepCount++;
                double d = Dist(curX, curY, _decStartX, _decStartY);
                if (d >= _distThresholdPx) {
                    _yAngle = Math.Atan2(curY - _decStartY, curX - _decStartX);
                    _yRate = d / (_stepCount * (double)_pulseMs);
                    _decSteps = _stepCount;
                    Result = new GuideCalibration(_xAngle, _yAngle, _xRate, _yRate, _decRad, true, _backlashMs);
                    _phase = Phase.Done;
                    return new CalibrationStep(false, GuideDirections.guideNorth, 0, true, false, "Done");
                }
                if (_stepCount >= _maxSteps) { _phase = Phase.Failed; return Fail("Dec did not move enough"); }
                return South();
            }

            case Phase.Done:
                return new CalibrationStep(false, GuideDirections.guideNorth, 0, true, false, "Done");
            default:
                return Fail("calibration failed");
        }
    }

    private static double Dist(double ax, double ay, double bx, double by) {
        double dx = ax - bx, dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private CalibrationStep West() => new(true, GuideDirections.guideWest, _pulseMs, false, false, "RA (west)");
    private CalibrationStep East() => new(true, GuideDirections.guideEast, _pulseMs, false, false, "RA recenter (east)");
    private CalibrationStep South() => new(true, GuideDirections.guideSouth, _pulseMs, false, false,
        _phase == Phase.DecClear ? "Dec backlash clear" : "Dec (south)");
    private static CalibrationStep Fail(string why) =>
        new(false, GuideDirections.guideNorth, 0, false, true, why);
}