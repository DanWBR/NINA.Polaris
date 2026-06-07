// Calibration state machine. Math (rate = dist/(steps*pulseMs), angle = atan2)
// ported from PHD2 (OpenPHDGuiding) scope.cpp, BSD-3-Clause.

using NINA.Core.Enum;

namespace NINA.Guider.Portable;

/// <summary>What the guider should do this calibration tick.</summary>
public readonly record struct CalibrationStep(
    bool Pulse, GuideDirections Direction, int DurationMs, bool Done, bool Failed, string Phase);

/// <summary>
/// Drives mount calibration: pulse WEST until the star moves a threshold distance
/// (measure RA angle+rate), recenter EAST, pulse SOUTH (measure Dec angle+rate).
/// The host feeds the current star centroid each tick and applies the returned
/// pulse. MVP happy-path port of PHD2's calibration.
/// </summary>
public sealed class CalibrationProcess {
    private enum Phase { Init, West, EastRecenter, South, Done, Failed }

    private readonly int _pulseMs;
    private readonly double _distThresholdPx;
    private readonly int _maxSteps;
    private readonly double _decRad;

    private Phase _phase = Phase.Init;
    private double _startX, _startY;        // phase start position
    private int _stepCount;
    private int _westSteps;                 // steps used in WEST (for EAST recenter)
    private double _xAngle, _xRate, _yAngle, _yRate;

    public GuideCalibration Result { get; private set; } = GuideCalibration.Invalid;

    public CalibrationProcess(int pulseMs = 1000, double distThresholdPx = 25.0,
                              int maxSteps = 60, double declinationRad = double.NaN) {
        _pulseMs = Math.Max(50, pulseMs);
        _distThresholdPx = Math.Max(5.0, distThresholdPx);
        _maxSteps = Math.Max(4, maxSteps);
        _decRad = declinationRad;
    }

    /// <summary>Advance the state machine given the latest measured star position.</summary>
    public CalibrationStep Tick(double curX, double curY) {
        switch (_phase) {
            case Phase.Init:
                _startX = curX; _startY = curY; _stepCount = 0;
                _phase = Phase.West;
                return new CalibrationStep(true, GuideDirections.guideWest, _pulseMs, false, false, "RA (west)");

            case Phase.West: {
                _stepCount++;
                double dx = curX - _startX, dy = curY - _startY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= _distThresholdPx) {
                    _xAngle = Math.Atan2(dy, dx);
                    _xRate = dist / (_stepCount * (double)_pulseMs);
                    _westSteps = _stepCount;
                    _phase = Phase.EastRecenter; _stepCount = 0;
                    return new CalibrationStep(true, GuideDirections.guideEast, _pulseMs, false, false, "RA recenter (east)");
                }
                if (_stepCount >= _maxSteps) { _phase = Phase.Failed; return Fail("RA did not move enough"); }
                return new CalibrationStep(true, GuideDirections.guideWest, _pulseMs, false, false, "RA (west)");
            }

            case Phase.EastRecenter: {
                _stepCount++;
                if (_stepCount >= _westSteps) {
                    _startX = curX; _startY = curY; _stepCount = 0;
                    _phase = Phase.South;
                    return new CalibrationStep(true, GuideDirections.guideSouth, _pulseMs, false, false, "Dec (south)");
                }
                return new CalibrationStep(true, GuideDirections.guideEast, _pulseMs, false, false, "RA recenter (east)");
            }

            case Phase.South: {
                _stepCount++;
                double dx = curX - _startX, dy = curY - _startY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= _distThresholdPx) {
                    _yAngle = Math.Atan2(dy, dx);
                    _yRate = dist / (_stepCount * (double)_pulseMs);
                    Result = new GuideCalibration(_xAngle, _yAngle, _xRate, _yRate, _decRad, true);
                    _phase = Phase.Done;
                    return new CalibrationStep(false, GuideDirections.guideNorth, 0, true, false, "Done");
                }
                if (_stepCount >= _maxSteps) { _phase = Phase.Failed; return Fail("Dec did not move enough"); }
                return new CalibrationStep(true, GuideDirections.guideSouth, _pulseMs, false, false, "Dec (south)");
            }

            case Phase.Done:
                return new CalibrationStep(false, GuideDirections.guideNorth, 0, true, false, "Done");
            default:
                return Fail("calibration failed");
        }
    }

    private static CalibrationStep Fail(string why) =>
        new(false, GuideDirections.guideNorth, 0, false, true, why);
}
