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

// Lowpass + Lowpass2 guide algorithms ported to C# from PHD2 (OpenPHDGuiding),
// BSD-3-Clause. See licenses/PHD2-LICENSE.txt. Sources:
// guide_algorithm_lowpass.cpp, guide_algorithm_lowpass2.cpp. HISTORY_SIZE=10.

namespace NINA.Guider.Portable;

/// <summary>Fixed-window stats over (t, value) samples: median + least-squares
/// slope of value vs t. Mirrors the bits of PHD2 AxisStats the lowpass
/// algorithms use.</summary>
internal sealed class WindowedStats {
    private readonly int _window;
    private readonly List<(double t, double v)> _pts = new();

    public WindowedStats(int window) => _window = Math.Max(2, window);

    public int Count => _pts.Count;
    public void Clear() => _pts.Clear();

    public void Add(double t, double v) {
        _pts.Add((t, v));
        while (_pts.Count > _window) _pts.RemoveAt(0);
    }

    public void RemoveOldest() {
        if (_pts.Count > 0) _pts.RemoveAt(0);
    }

    public double Median() {
        if (_pts.Count == 0) return 0.0;
        var v = _pts.Select(p => p.v).OrderBy(x => x).ToArray();
        int n = v.Length;
        return n % 2 == 1 ? v[n / 2] : 0.5 * (v[n / 2 - 1] + v[n / 2]);
    }

    /// <summary>Least-squares slope of v vs t. Returns 0 when undetermined.</summary>
    public double Slope() {
        int n = _pts.Count;
        if (n < 2) return 0.0;
        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        foreach (var (t, v) in _pts) { sx += t; sy += v; sxy += t * v; sxx += t * t; }
        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-12) return 0.0;
        return (n * sxy - sx * sy) / denom;
    }
}

/// <summary>Lowpass: median + slopeWeight*slope over a 10-sample window,
/// capped at the input magnitude, with a minMove deadband.</summary>
public sealed class LowpassAlgorithm : IGuideAlgorithm {
    private const int HistorySize = 10;
    private readonly double _slopeWeight;
    private readonly double _minMove;
    private readonly WindowedStats _stats = new(HistorySize + 1);
    private long _t;

    public LowpassAlgorithm(double minMove = 0.2, double slopeWeight = 5.0) {
        _minMove = Math.Max(0.0, minMove);
        _slopeWeight = slopeWeight;
        Reset();
    }

    public string Name => "lowpass";

    public double Result(double input) {
        _stats.Add(_t++, input);
        double median = _stats.Median();
        _stats.RemoveOldest();
        double slope = _stats.Slope();
        double r = median + _slopeWeight * slope;
        if (Math.Abs(r) > Math.Abs(input)) r = input;
        if (Math.Abs(input) < _minMove) r = 0.0;
        return r;
    }

    public void Reset() {
        _stats.Clear();
        _t = 0;
        // PHD2 pre-fills the window with zeros so the first corrections behave.
        for (int i = 0; i < HistorySize; i++) _stats.Add(_t++, 0.0);
    }
}

/// <summary>Lowpass2: auto-windowed least-squares predictor with aggressiveness
/// attenuation, outlier dumping and wrong-direction rejection.</summary>
public sealed class Lowpass2Algorithm : IGuideAlgorithm {
    private const int HistorySize = 10;
    private readonly double _minMove;
    private readonly double _aggressiveness; // percent (0..100)
    private readonly WindowedStats _stats = new(HistorySize);
    private long _t;
    private int _rejects;

    public Lowpass2Algorithm(double minMove = 0.2, double aggressiveness = 80.0) {
        _minMove = Math.Max(0.0, minMove);
        _aggressiveness = Math.Clamp(aggressiveness, 1.0, 100.0);
    }

    public string Name => "lowpass2";

    public double Result(double input) {
        _stats.Add(_t++, input);
        int n = _stats.Count;
        double atten = _aggressiveness / 100.0;
        double r;

        if (n < 4) {
            r = input * atten;
        } else if (Math.Abs(input) > 4.0 * _minMove) {
            r = input * atten;
            Reset();
        } else {
            double slope = _stats.Slope();
            r = slope * n * atten;
            if (input * r < 0) r = 0.0;
        }

        if (Math.Abs(r) > Math.Abs(input)) {
            r = input * atten;
            if (++_rejects > 3) Reset();
        } else {
            _rejects = 0;
        }

        if (Math.Abs(input) < _minMove) r = 0.0;
        return r;
    }

    public void Reset() {
        _stats.Clear();
        _t = 0;
        _rejects = 0;
    }
}

/// <summary>Builds a per-axis <see cref="IGuideAlgorithm"/> by name. Unknown
/// names fall back to identity (pass-through).</summary>
public static class GuideAlgorithmFactory {
    public static IGuideAlgorithm Create(string? name, double minMove,
                                         double aggression, double hysteresis,
                                         double wormPeriodSec = 0.0,
                                         int predictiveWindow = 256,
                                         double predictiveBlend = 0.7) {
        return (name ?? "").Trim().ToLowerInvariant() switch {
            "hysteresis"  => new HysteresisAlgorithm(hysteresis, aggression, minMove),
            "resistswitch" => new ResistSwitchAlgorithm(minMove, aggression),
            "lowpass"     => new LowpassAlgorithm(minMove),
            "lowpass2"    => new Lowpass2Algorithm(minMove, Math.Clamp(aggression * 100.0, 1.0, 100.0)),
            "predictive"  => new PredictiveAlgorithm(minMove, aggression, hysteresis,
                                                     wormPeriodSec, predictiveWindow, predictiveBlend),
            "identity"    => new IdentityAlgorithm(),
            _             => new IdentityAlgorithm(),
        };
    }
}