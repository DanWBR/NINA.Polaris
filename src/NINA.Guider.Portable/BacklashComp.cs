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