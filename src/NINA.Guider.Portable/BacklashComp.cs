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

// Dec backlash compensation. Concept ported from PHD2 (OpenPHDGuiding)
// backlash_comp.cpp, BSD-3-Clause: add the measured slack take-up to a Dec
// pulse on direction reversal, then watch the next frame and shrink the
// amount when it overshoots, as PHD2's adaptive compensation does.

using NINA.Core.Enum;

namespace NINA.Guider.Portable;

/// <summary>What the frame after a compensated reversal said about the amount.</summary>
public enum BacklashObservation { None, Reduced, Increased, Disabled }

/// <summary>Adds an extra pulse to a Dec correction when the Dec direction
/// reverses, to take up gear slack. The amount starts at the measured value
/// (capped), and adapts: the frame after each compensated reversal shows
/// whether it overshot (the star went past the lock) or fell short, and the
/// amount is halved or nudged up accordingly. Repeated overshoots turn it off.
///
/// <para>Field report (2026-11-08, ZWO AM5): calibration recorded 6000 ms of
/// "backlash" on a strain-wave mount with next to none, every Dec reversal
/// then went out as a full 2500 ms pulse, about 3.4 px for a 0.6 px error,
/// and the Dec axis oscillated with a growing amplitude after any
/// disturbance (a passing cloud) until guiding was stopped and restarted.
/// The old guard trimmed only after three reversals in a row, which the real
/// pattern (reverse, correct the same way, reverse) never produced.</para></summary>
public sealed class BacklashComp {
    private readonly int _baseMs;     // measured backlash (ms)
    private readonly int _maxMs;      // hard ceiling on the applied amount
    private double _appliedMs;        // current applied amount (adapted)
    private GuideDirections _lastDir = GuideDirections.guideNorth;
    private bool _haveLast;
    private int _reversalsInARow;
    private bool _pendingCheck;       // a compensated reversal awaits its verdict
    private GuideDirections _pendingDir;
    private int _overshoots;

    /// <summary>Overshoots, after a compensated reversal, before the
    /// compensation is turned off for the session.</summary>
    public const int MaxOvershoots = 3;

    /// <summary>Below this the amount is not worth applying.</summary>
    public const double MinUsefulMs = 50;

    /// <param name="measuredMs">The calibration's backlash measurement.</param>
    /// <param name="maxMs">Ceiling on the applied amount; 0 = the measured value.</param>
    /// <param name="prior">The compensation this one replaces (a settings
    /// change, a new guiding run). When it was built from the same measurement
    /// and ceiling, what it learned carries over, so a stop/start does not put
    /// back an amount the session already found to overshoot.</param>
    public BacklashComp(double measuredMs, int maxMs = 0, BacklashComp? prior = null) {
        _baseMs = (int)Math.Round(Math.Max(0, measuredMs));
        _maxMs = maxMs > 0 ? maxMs : _baseMs;
        _appliedMs = Math.Min(_baseMs, _maxMs);
        if (prior != null && prior._baseMs == _baseMs && prior._maxMs == _maxMs) {
            _appliedMs = prior._appliedMs;
            _overshoots = prior._overshoots;
            Disabled = prior.Disabled;
        }
    }

    public bool Enabled => _baseMs > 0 && !Disabled;
    /// <summary>Turned off this session after repeated overshoots.</summary>
    public bool Disabled { get; private set; }
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

        // Rapid back-to-back reversals mean we're likely over-pushing; trim.
        _reversalsInARow++;
        if (_reversalsInARow >= 3) {
            _appliedMs = Math.Max(0, _appliedMs * 0.75);
            _reversalsInARow = 0;
        }
        int add = (int)Math.Min(_appliedMs, _maxMs);
        if (add <= 0) return requestedMs;
        _pendingCheck = true;
        _pendingDir = decDir;
        return requestedMs + add;
    }

    /// <summary>
    /// Feed the Dec error measured on the frame after a pulse (pixels, signed
    /// so that a positive error is corrected by a NORTH pulse, as in the guide
    /// loop). When the previous pulse was a compensated reversal: an error now
    /// needing the opposite direction is an overshoot and halves the amount;
    /// one still needing the same direction is a shortfall and nudges it up to
    /// the ceiling. Errors within <paramref name="minMovePx"/> say nothing.
    /// </summary>
    public BacklashObservation Observe(double decErrorPx, double minMovePx) {
        if (!_pendingCheck || !Enabled) { _pendingCheck = false; return BacklashObservation.None; }
        _pendingCheck = false;
        if (double.IsNaN(decErrorPx) || Math.Abs(decErrorPx) <= minMovePx) {
            if (_overshoots > 0) _overshoots--;
            return BacklashObservation.None;
        }
        var needed = decErrorPx >= 0 ? GuideDirections.guideNorth : GuideDirections.guideSouth;
        if (needed != _pendingDir) {
            _overshoots++;
            _appliedMs *= 0.5;
            if (_overshoots >= MaxOvershoots || _appliedMs < MinUsefulMs) {
                _appliedMs = 0;
                Disabled = true;
                return BacklashObservation.Disabled;
            }
            return BacklashObservation.Reduced;
        }
        double grown = Math.Min(_maxMs, _appliedMs * 1.1);
        if (grown <= _appliedMs) return BacklashObservation.None;
        _appliedMs = grown;
        return BacklashObservation.Increased;
    }

    /// <summary>Forget the direction history (a new run, a flip, a new
    /// calibration). What the session learned about the amount is kept.</summary>
    public void Reset() {
        _haveLast = false;
        _reversalsInARow = 0;
        _pendingCheck = false;
    }
}
