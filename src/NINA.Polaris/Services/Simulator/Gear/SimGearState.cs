// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

// Ported to C# from PHD2 (OpenPHDGuiding) src/gear_simulator.cpp.
//
// PHD2 is Copyright (c) Craig Stark, Bret McKee, Dad Dog Development Ltd.
// Licensed under the BSD 3-Clause License. See licenses/PHD2-LICENSE.txt.
//
// Shared mutable state for the gear simulator: the cumulative guide offsets
// (RA + Dec-with-backlash), pointing, pier side, and the error-model maths
// (ST4PulseGuideScope, periodic error, Dec drift, seeing). Both SimGuideCamera
// and SimMount hold a reference to one instance so a pulse on the mount shifts
// the star field the camera renders.

using System.Diagnostics;
using NINA.Core.Enum;

namespace NINA.Polaris.Services.Simulator.Gear;

/// <summary>
/// Value with backlash (port of PHD2 <c>BacklashVal</c>). The reported value is
/// the upper limit; reversing direction first has to cross the backlash gap
/// before the value starts moving again.
/// </summary>
public struct BacklashVal {
    private double _cur;
    private double _upper;
    private readonly double _amount;

    public BacklashVal(double backlashAmount) {
        _cur = 0;
        _upper = backlashAmount;
        _amount = backlashAmount;
    }

    public readonly double Val => _upper;

    public void Incr(double d) {
        _cur += d;
        if (d > 0.0) {
            if (_cur > _upper) _upper = _cur;
        } else if (d < 0.0) {
            if (_cur < _upper - _amount) _upper = _cur + _amount;
        }
    }
}

/// <summary>Shared virtual-sky state behind a lock.</summary>
public sealed class SimGearState {
    private readonly object _gate = new();
    private readonly SimGearParams _p;

    // Cumulative guider offsets relative to the zero point (pixels).
    private double _raOfs;
    private BacklashVal _decOfs;
    private double _cumDecDrift;
    private double _lastSec;

    // Pointing + mount state.
    public double RightAscensionHours { get; set; }
    public double DeclinationDeg { get; set; } = 45.0;
    public PierSide PierSide { get; set; }
    public bool Tracking { get; set; } = true;
    public bool Parked { get; set; }

    /// <summary>Elapsed-seconds clock. Defaults to a Stopwatch; tests can swap
    /// in a deterministic source.</summary>
    public Func<double> ClockSec { get; set; }

    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public SimGearState(SimGearParams p) {
        _p = p;
        _decOfs = new BacklashVal(p.DecBacklashPx);
        PierSide = p.PierSide;
        ClockSec = () => _sw.Elapsed.TotalSeconds;
    }

    public SimGearParams Params => _p;

    public double NowSec() => ClockSec();

    /// <summary>Current cumulative offsets in pixels (no PE/drift/seeing),
    /// exposed for tests.</summary>
    public (double ra, double dec) RawOffsets {
        get { lock (_gate) return (_raOfs, _decOfs.Val); }
    }

    /// <summary>Port of <c>CameraSimulator::ST4PulseGuideScope</c>. Converts a
    /// timed pulse on one axis into a cumulative pixel offset, scaling RA by
    /// cos(dec) and honouring Dec backlash + post-flip Dec reversal.</summary>
    public void St4Pulse(GuideDirections direction, int durationMs, int binning = 1) {
        lock (_gate) {
            double d = _p.GuideRateArcsecPerSec * binning * durationMs / (1000.0 * _p.ImageScale);

            if (direction == GuideDirections.guideWest || direction == GuideDirections.guideEast) {
                double decRad = double.IsNaN(DeclinationDeg)
                    ? 25.0 * Math.PI / 180.0
                    : DeclinationDeg * Math.PI / 180.0;
                d *= Math.Cos(decRad);
            }

            var dir = direction;
            if (PierSide == PierSide.pierWest && _p.ReverseDecOnWestSide) {
                if (dir == GuideDirections.guideNorth) dir = GuideDirections.guideSouth;
                else if (dir == GuideDirections.guideSouth) dir = GuideDirections.guideNorth;
            }

            switch (dir) {
                case GuideDirections.guideWest: _raOfs += d; break;
                case GuideDirections.guideEast: _raOfs -= d; break;
                case GuideDirections.guideNorth: _decOfs.Incr(d); break;
                case GuideDirections.guideSouth: _decOfs.Incr(-d); break;
            }
        }
    }

    /// <summary>Advance the time-dependent error sources to <paramref name="nowSec"/>
    /// and return the total star-field shift in pixels (RA axis = x, Dec axis = y),
    /// before camera rotation. Combines periodic error, Dec drift, the cumulative
    /// ST4 offsets and per-frame seeing.</summary>
    public (double x, double y) AdvanceAndComputeShift(double nowSec, Random rng) {
        lock (_gate) {
            double pe = _p.UsePeriodicError ? ComputePeriodicErrorPx(nowSec) : 0.0;

            double deltaSec = nowSec - _lastSec;
            if (deltaSec < 0) deltaSec = 0;
            _lastSec = nowSec;
            if (_p.UseDrift) _cumDecDrift += deltaSec * _p.DecDriftPxPerSec;

            double sx = pe + _raOfs;
            double sy = _cumDecDrift + _decOfs.Val;

            if (_p.UseSeeing && _p.SeeingArcsecFwhm > 0.0) {
                var (g0, g1) = RandNormal(rng);
                // PHD2: sigma = seeing_scale / (2.345*1.4*2.4 * image_scale)
                const double seeingAdjustment = 2.345 * 1.4 * 2.4;
                double sigma = _p.SeeingArcsecFwhm / (seeingAdjustment * _p.ImageScale);
                sx += g0 * sigma;
                sy += g1 * sigma;
            }
            return (sx, sy);
        }
    }

    // Canned multi-harmonic periodic error (port of the default-params branch).
    private static readonly double[] PePeriod = { 230.5, 122.0, 49.4, 9.56, 76.84 };
    private static readonly double[] PeAmp = { 2.02, 0.69, 0.22, 0.137, 0.14 };
    private static readonly double[] PePhase = { 0.0, 1.4, 98.8, 35.9, 150.4 };
    private const double PeMaxAmp = 4.85;

    private double ComputePeriodicErrorPx(double nowSec) {
        double pe = 0.0;
        for (int i = 0; i < PePeriod.Length; i++)
            pe += PeAmp[i] * Math.Cos((nowSec - PePhase[i]) / PePeriod[i] * 2.0 * Math.PI);
        // modulated PE in pixels
        return pe * (_p.PeAmplitudeArcsec / (PeMaxAmp * _p.ImageScale));
    }

    // Box-Muller pair, sigma = 1 (port of rand_normal).
    private static (double, double) RandNormal(Random rng) {
        double u = rng.NextDouble();
        double v = rng.NextDouble();
        if (u < 1e-12) u = 1e-12;
        double a = Math.Sqrt(-2.0 * Math.Log(u));
        double p = 2.0 * Math.PI * v;
        return (a * Math.Cos(p), a * Math.Sin(p));
    }
}