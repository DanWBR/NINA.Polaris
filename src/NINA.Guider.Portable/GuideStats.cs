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

namespace NINA.Guider.Portable;

/// <summary>One guide-loop sample (arcsec errors + pulse durations + star quality).</summary>
public readonly record struct GuideStep(
    long TimestampMs,
    double RaArcsec,
    double DecArcsec,
    double RaRawPx,
    double DecRawPx,
    int RaDurationMs,
    int DecDurationMs,
    double Snr,
    double Hfd,
    bool StarFound);

/// <summary>Rolling RMS / peak over a window of guide errors (arcsec).</summary>
public sealed class RmsCalculator {
    private readonly int _window;
    private readonly Queue<(double ra, double dec)> _q = new();

    public RmsCalculator(int window = 100) => _window = Math.Max(2, window);

    public void Add(double raArcsec, double decArcsec) {
        _q.Enqueue((raArcsec, decArcsec));
        while (_q.Count > _window) _q.Dequeue();
    }

    public void Reset() => _q.Clear();

    public (double rmsRa, double rmsDec, double rmsTotal, double peakRa, double peakDec) Compute() {
        if (_q.Count == 0) return (0, 0, 0, 0, 0);
        double sr = 0, sd = 0, pr = 0, pd = 0;
        foreach (var (ra, dec) in _q) {
            sr += ra * ra; sd += dec * dec;
            pr = Math.Max(pr, Math.Abs(ra)); pd = Math.Max(pd, Math.Abs(dec));
        }
        int n = _q.Count;
        double rRa = Math.Sqrt(sr / n), rDec = Math.Sqrt(sd / n);
        return (rRa, rDec, Math.Sqrt(rRa * rRa + rDec * rDec), pr, pd);
    }
}

/// <summary>Settle monitor: succeeds when the total error stays under a pixel
/// threshold for a dwell time; fails on timeout. Mirrors PHD2 settle semantics.</summary>
public sealed class GuidingSettler {
    private readonly double _pixels;
    private readonly long _settleMs;
    private readonly long _timeoutMs;
    private long _startMs;
    private long _belowSinceMs = -1;

    public GuidingSettler(double pixels, double settleSeconds, double timeoutSeconds, long nowMs) {
        _pixels = pixels;
        _settleMs = (long)(settleSeconds * 1000);
        _timeoutMs = (long)(timeoutSeconds * 1000);
        _startMs = nowMs;
    }

    public enum State { Settling, Done, TimedOut }

    public State Update(double totalErrorPx, long nowMs) {
        if (nowMs - _startMs > _timeoutMs) return State.TimedOut;
        if (totalErrorPx <= _pixels) {
            if (_belowSinceMs < 0) _belowSinceMs = nowMs;
            if (nowMs - _belowSinceMs >= _settleMs) return State.Done;
        } else {
            _belowSinceMs = -1;
        }
        return State.Settling;
    }
}