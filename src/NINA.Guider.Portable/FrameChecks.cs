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

// The two frame-rejection gates PHD2 runs between finding the star and moving
// the mount, ported from guider_multistar.cpp (MassChecker, DistanceChecker)
// and guider.cpp (Guider::UpdateCurrentDistance), BSD-3-Clause. See
// licenses/PHD2-LICENSE.txt.
//
// Without them a frame where the star changed brightness (a cloud, a passing
// satellite, a second star wandering into the window) or where the centroid
// jumped right after a star loss is treated as a real error and pulsed at the
// mount. PHD2 drops those frames instead.

namespace NINA.Guider.Portable;

/// <summary>
/// PHD2 MassChecker. Keeps a time window of star masses and rejects a frame
/// whose mass sits outside a band around the running median, which is how PHD2
/// notices that the thing it just centroided is not the star it was guiding on.
/// </summary>
public sealed class MassChecker {
    /// <summary>PHD2 DefaultTimeWindowMs. The window actually kept is twice
    /// this, because an abrupt change reaches the median after half of it.</summary>
    public const int DefaultTimeWindowMs = 22500;
    /// <summary>PHD2 DefaultMassChangeThreshold.</summary>
    public const double DefaultThreshold = 0.5;
    private const int MinSamples = 5;

    private readonly record struct Entry(long TimeMs, double Mass);

    private readonly List<Entry> _data = new();
    private readonly Func<long> _now;
    private long _timeWindowMs;
    private double _highMass;
    private double _lowMass = 9e99;
    private int _exposureMs;
    private bool _autoExposure;

    public MassChecker(Func<long>? nowMs = null, int timeWindowMs = DefaultTimeWindowMs) {
        _now = nowMs ?? (() => Environment.TickCount64);
        _timeWindowMs = (long)timeWindowMs * 2;
    }

    /// <summary>The band the last check computed: low limit, median, high
    /// limit, and the spike limit. Diagnostics only.</summary>
    public (double Low, double Median, double High, double Spike) LastLimits { get; private set; }

    public void SetExposure(int exposureMs, bool isAutoExposure) {
        if (isAutoExposure != _autoExposure) {
            _autoExposure = isAutoExposure;
            _exposureMs = exposureMs;
            Reset();
        } else if (exposureMs != _exposureMs) {
            _exposureMs = exposureMs;
            if (!_autoExposure) Reset();
        }
    }

    private double AdjustedMass(double mass) =>
        _autoExposure && _exposureMs > 0 ? mass / _exposureMs : mass;

    public void AppendData(double mass) {
        long now = _now();
        long oldest = now - _timeWindowMs;
        int drop = 0;
        while (drop < _data.Count && _data[drop].TimeMs < oldest) drop++;
        if (drop > 0) _data.RemoveRange(0, drop);
        _data.Add(new Entry(now, AdjustedMass(mass)));
    }

    /// <summary>True when this frame's mass is out of band and the frame should
    /// be dropped. Always false until there are five samples, so guiding is
    /// never blocked while the window fills.</summary>
    public bool CheckMass(double mass, double threshold = DefaultThreshold) {
        if (_data.Count < MinSamples) return false;

        var tmp = new double[_data.Count];
        for (int i = 0; i < _data.Count; i++) tmp[i] = _data[i].Mass;
        Array.Sort(tmp);
        double med = tmp[_data.Count / 2];      // PHD2 takes the upper middle too

        if (med > _highMass) _highMass = med;
        if (med < _lowMass) _lowMass = med;
        // Let the low-water mark drift up towards the median so it recovers
        // after a spell of thin cloud has dragged it down.
        _lowMass += 0.05 * (med - _lowMass);

        double low = _lowMass * (1.0 - threshold);
        double high = _highMass * (1.0 + threshold);
        // A large spike is rejected even when it is still under the high-water
        // limit, which matters when the sky has been depressing the mass.
        double spike = med * (1.0 + 2.0 * threshold);
        LastLimits = (low, med, high, spike);

        double adj = AdjustedMass(mass);
        return adj < low || adj > high || adj > spike;
    }

    public void Reset() {
        _data.Clear();
        _highMass = 0.0;
        _lowMass = 9e99;
    }
}

/// <summary>
/// PHD2 DistanceChecker. After a star loss it distrusts the next few frames:
/// an offset more than twice the smoothed average error is dropped for up to
/// five seconds, and only then does it accept that the star really is where it
/// now appears. With "tolerate jumps" off (PHD2's default) it is otherwise
/// inert, because the tolerance it is given is effectively infinite.
/// </summary>
public sealed class DistanceChecker {
    public enum State { Guiding, Waiting, Recovering }

    /// <summary>PHD2 WAIT_INTERVAL_MS.</summary>
    public const int WaitIntervalMs = 5000;
    /// <summary>PHD2 MIN_FRAMES_FOR_STATS.</summary>
    public const int MinFramesForStats = 10;
    /// <summary>The tolerance PHD2 forces after a star loss.</summary>
    public const double ForcedTolerance = 2.0;

    private readonly Func<long> _now;
    private State _state = State.Guiding;
    private long _expiresMs;
    private double _forceTolerance;

    public DistanceChecker(Func<long>? nowMs = null) {
        _now = nowMs ?? (() => Environment.TickCount64);
    }

    public State CurrentState => _state;

    /// <summary>Called when a frame failed to find the star: the next frames
    /// are suspect.</summary>
    public void Activate() {
        if (_state == State.Guiding) {
            _state = State.Waiting;
            _expiresMs = _now() + WaitIntervalMs;
            _forceTolerance = ForcedTolerance;
        }
    }

    public void Reset() {
        _state = State.Guiding;
        _forceTolerance = 0.0;
    }

    private static bool SmallOffset(double distance, double tolerance,
                                    double avgDistance, int frameCount, bool measuring) {
        // PHD2 does not judge a frame it has no statistics for, and does not
        // judge at all unless it is guiding and not settling.
        if (!measuring || frameCount < MinFramesForStats) return true;
        return distance <= tolerance * avgDistance;
    }

    /// <summary>False when the frame should be dropped instead of guided on.</summary>
    /// <param name="measuring">Guiding, not paused and not settling.</param>
    public bool CheckDistance(double distance, double tolerance,
                              double avgDistance, int frameCount, bool measuring) {
        if (_forceTolerance != 0.0) tolerance = _forceTolerance;

        bool small = SmallOffset(distance, tolerance, avgDistance, frameCount, measuring);

        switch (_state) {
            default:
            case State.Guiding:
                if (small) return true;
                _state = State.Waiting;
                _expiresMs = _now() + WaitIntervalMs;
                return false;

            case State.Waiting:
                if (small) {
                    _state = State.Guiding;
                    _forceTolerance = 0.0;
                    return true;
                }
                if (_now() < _expiresMs) return false;   // still inside the window
                _state = State.Recovering;
                goto case State.Recovering;

            case State.Recovering:
                if (small) {
                    _state = State.Guiding;
                    _forceTolerance = 0.0;
                }
                return true;    // accept it and let guiding pull the star back
        }
    }
}

/// <summary>
/// PHD2 Guider::UpdateCurrentDistance: the fast and slow moving averages of the
/// guide error that the distance checker and the UI read.
/// </summary>
public sealed class CurrentErrorTracker {
    private const double Alpha = 0.3;         // latest sample weighted heavily
    private const double AlphaLong = 0.045;   // ~15 frame half life
    private const int InitFrames = 10;

    public double AvgDistance { get; private set; }
    public double AvgDistanceRa { get; private set; }
    public double AvgDistanceLong { get; private set; }
    public double AvgDistanceLongRa { get; private set; }
    public int FrameCount { get; private set; }

    public void Update(double distance, double distanceRa) {
        AvgDistance += Alpha * (distance - AvgDistance);
        AvgDistanceRa += Alpha * (distanceRa - AvgDistanceRa);
        FrameCount++;
        if (FrameCount < InitFrames) {
            AvgDistanceLong += (distance - AvgDistanceLong) / FrameCount;
            AvgDistanceLongRa += (distanceRa - AvgDistanceLongRa) / FrameCount;
        } else {
            AvgDistanceLong += AlphaLong * (distance - AvgDistanceLong);
            AvgDistanceLongRa += AlphaLong * (distanceRa - AvgDistanceLongRa);
        }
    }

    /// <summary>Not guiding: PHD2 seeds both averages with the current value
    /// and restarts the count.</summary>
    public void Seed(double distance, double distanceRa) {
        AvgDistance = AvgDistanceLong = distance;
        AvgDistanceRa = AvgDistanceLongRa = distanceRa;
        FrameCount = 0;
    }

    public void Reset() {
        AvgDistance = AvgDistanceRa = AvgDistanceLong = AvgDistanceLongRa = 0;
        FrameCount = 0;
    }
}
