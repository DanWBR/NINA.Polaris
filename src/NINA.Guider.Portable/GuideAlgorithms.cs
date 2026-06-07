// Guide algorithms ported to C# from PHD2 (OpenPHDGuiding), BSD-3-Clause.
// See licenses/PHD2-LICENSE.txt. Sources: guide_algorithm_hysteresis.cpp,
// guide_algorithm_resistswitch.cpp.

namespace NINA.Guider.Portable;

/// <summary>Pass-through (PHD2 "identity"): output = input.</summary>
public sealed class IdentityAlgorithm : IGuideAlgorithm {
    public string Name => "identity";
    public double Result(double input) => input;
    public void Reset() { }
}

/// <summary>Hysteresis algorithm (PHD2 default for RA).
/// out = aggression * ((1-hys)*input + hys*lastMove), zeroed below minMove.</summary>
public sealed class HysteresisAlgorithm : IGuideAlgorithm {
    private readonly double _hysteresis;
    private readonly double _aggression;
    private readonly double _minMove;
    private double _lastMove;

    public HysteresisAlgorithm(double hysteresis = 0.10, double aggression = 0.70, double minMove = 0.15) {
        _hysteresis = Math.Clamp(hysteresis, 0.0, 0.99);
        _aggression = Math.Clamp(aggression, 0.0, 2.0);
        _minMove = Math.Max(0.0, minMove);
    }

    public string Name => "hysteresis";

    public double Result(double input) {
        double r = (1.0 - _hysteresis) * input + _hysteresis * _lastMove;
        r *= _aggression;
        if (Math.Abs(input) < _minMove) r = 0.0;
        _lastMove = r;
        return r;
    }

    public void Reset() => _lastMove = 0.0;
}

/// <summary>Resist-switch algorithm (PHD2 default for Dec): resists reversing
/// declination direction (avoids backlash-driven oscillation) unless the
/// error history compellingly demands it, then applies aggression.</summary>
public sealed class ResistSwitchAlgorithm : IGuideAlgorithm {
    private const int HistorySize = 10;
    private readonly double _minMove;
    private readonly double _aggression;
    private readonly bool _fastSwitch;
    private readonly double[] _history = new double[HistorySize];
    private int _currentSide; // -1, 0, +1

    public ResistSwitchAlgorithm(double minMove = 0.15, double aggression = 1.0, bool fastSwitch = true) {
        _minMove = Math.Max(0.0, minMove);
        _aggression = Math.Clamp(aggression, 0.0, 2.0);
        _fastSwitch = fastSwitch;
    }

    public string Name => "resistswitch";

    private static int Sign(double v) => v > 0 ? 1 : (v < 0 ? -1 : 0);

    public double Result(double input) {
        double rslt = input;

        // push history (oldest at [0])
        for (int i = 0; i < HistorySize - 1; i++) _history[i] = _history[i + 1];
        _history[HistorySize - 1] = input;

        bool veto = false;
        if (Math.Abs(input) < _minMove) {
            veto = true;
        } else {
            if (_fastSwitch) {
                double thr = 3.0 * _minMove;
                if (Sign(input) != _currentSide && Math.Abs(input) > thr) {
                    _currentSide = 0;
                    int i;
                    for (i = 0; i < HistorySize - 3; i++) _history[i] = 0.0;
                    for (; i < HistorySize; i++) _history[i] = input;
                }
            }

            int decHistory = 0;
            for (int i = 0; i < HistorySize; i++)
                if (Math.Abs(_history[i]) > _minMove) decHistory += Sign(_history[i]);

            if (_currentSide == 0 || Sign(_currentSide) == -Sign(decHistory)) {
                if (Math.Abs(decHistory) < 3) {
                    veto = true;
                } else {
                    double oldest = 0, newest = 0;
                    for (int i = 0; i < 3; i++) {
                        oldest += _history[i];
                        newest += _history[HistorySize - (i + 1)];
                    }
                    if (Math.Abs(newest) <= Math.Abs(oldest)) veto = true; // not getting worse
                    else _currentSide = Sign(decHistory);
                }
            }

            if (!veto && _currentSide != Sign(input)) veto = true; // overshot -> veto
        }

        if (veto) rslt = 0.0;
        rslt *= _aggression;
        return rslt;
    }

    public void Reset() {
        Array.Clear(_history);
        _currentSide = 0;
    }
}
