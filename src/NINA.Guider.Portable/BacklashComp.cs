// Dec backlash compensation. Concept ported from PHD2 (OpenPHDGuiding)
// backlash_comp.cpp, BSD-3-Clause. Simplified MVP: add the measured slack
// take-up to a Dec pulse on direction reversal, with an overshoot guard that
// trims the applied amount when reversals chatter.

using NINA.Core.Enum;

namespace NINA.Guider.Portable;

/// <summary>Adds an extra pulse to a Dec correction when the Dec direction
/// reverses, to take up gear slack. Capped, and self-trims on chatter so an
/// over-large value can't drive oscillation (the failure mode that makes bad
/// backlash comp worse than none).</summary>
public sealed class BacklashComp {
    private readonly int _baseMs;     // measured backlash (ms)
    private readonly int _maxMs;      // hard ceiling on the applied amount
    private double _appliedMs;        // current applied amount (may be trimmed)
    private GuideDirections _lastDir = GuideDirections.guideNorth;
    private bool _haveLast;
    private int _reversalsInARow;

    public BacklashComp(double measuredMs, int maxMs = 0) {
        _baseMs = (int)Math.Round(Math.Max(0, measuredMs));
        _maxMs = maxMs > 0 ? maxMs : Math.Max(_baseMs, _baseMs * 2);
        _appliedMs = Math.Min(_baseMs, _maxMs);
    }

    public bool Enabled => _baseMs > 0;
    public int AppliedMs => (int)Math.Round(_appliedMs);

    /// <summary>Return the Dec pulse duration to actually issue, given the
    /// requested duration + direction this frame. Adds the comp amount only on
    /// a real direction reversal with a non-zero move.</summary>
    public int Adjust(GuideDirections decDir, int requestedMs) {
        if (requestedMs <= 0) return requestedMs; // no move -> no comp, keep last dir
        if (!Enabled) { _lastDir = decDir; _haveLast = true; return requestedMs; }

        bool reversal = _haveLast && decDir != _lastDir;
        _lastDir = decDir;
        _haveLast = true;

        if (!reversal) { _reversalsInARow = 0; return requestedMs; }

        // Overshoot guard: rapid back-to-back reversals mean we're likely
        // over-pushing; trim the applied amount. A clean single reversal
        // resets toward the measured value.
        _reversalsInARow++;
        if (_reversalsInARow >= 3) {
            _appliedMs = Math.Max(0, _appliedMs * 0.75);
            _reversalsInARow = 0;
        }
        int add = (int)Math.Min(_appliedMs, _maxMs);
        return requestedMs + add;
    }

    public void Reset() {
        _haveLast = false;
        _reversalsInARow = 0;
        _appliedMs = Math.Min(_baseMs, _maxMs);
    }
}
