// Copyright (c) 2018 Ken Self — PHD2 / OpenPHDGuiding (BSD-3-Clause).
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// The ZFilter guide algorithm + IIR filter factory are ported to C# from PHD2
// (guide_algorithm_zfilter.cpp / zfilterfactory.cpp), which is distributed under
// the BSD-3-Clause license — see licenses/PHD2-LICENSE.txt. The factory is itself
// based on A. J. Fisher's mkfilter (University of York, 1992),
// https://www-users.cs.york.ac.uk/~fisher/mkfilter/.
//
// As part of N.I.N.A. Polaris this file is also available under the GNU Affero
// General Public License v3.0 (see LICENSE.txt and NOTICE).

using System.Numerics;

namespace NINA.Guider.Portable;

/// <summary>
/// ZFilter guide algorithm (PHD2 <c>GuideAlgorithmZFilter</c>). A low-pass IIR
/// filter applied to the *uncorrected* star-position waveform (input + the sum of
/// all corrections issued so far), so it smooths sensor noise / seeing while still
/// chasing real drift. The cutoff is set by <c>expFactor</c> ("exposure factor"):
/// the equivalent post-filter exposure time ≈ expFactor × exposure. Below a corner
/// of 6 it falls back to a Butterworth design, otherwise Bessel (order 4), exactly
/// like PHD2. Faithful port — see file header for provenance + license.
/// </summary>
public sealed class ZFilterAlgorithm : IGuideAlgorithm {
    private readonly double _minMove;
    private readonly double _expFactor;
    private double[] _xcoeff = [];
    private double[] _ycoeff = [];
    private double _gain = 1.0;
    private double[] _xv = [];   // input history (length == _xcoeff.Length)
    private double[] _yv = [];   // output history (length == _ycoeff.Length)
    private double _sumCorr;     // sum of all corrections issued

    public ZFilterAlgorithm(double expFactor = 2.0, double minMove = 0.1) {
        _expFactor = Math.Max(1.0, expFactor);
        _minMove = Math.Max(0.0, minMove);
        BuildFilter(order: 4);
        Reset();
    }

    public string Name => "zfilter";

    public double Result(double input) {
        double gain = _gain;
        // Add total guide output back to the input to recover the uncorrected
        // waveform, then prepend to the input ring (drop the oldest).
        Shift(_xv, (input + _sumCorr) / gain);
        Shift(_yv, 0.0);

        double y = 0.0;
        for (int i = 0; i < _xcoeff.Length; i++) y += _xv[i] * _xcoeff[i];
        for (int i = 1; i < _ycoeff.Length; i++) y += _yv[i] * _ycoeff[i];
        _yv[0] = y;

        double r = y - _sumCorr;           // correction = filtered − already-applied
        if (Math.Abs(r) < _minMove) r = 0.0;
        _sumCorr += r;
        return r;
    }

    public void Reset() {
        _xv = new double[_xcoeff.Length];
        _yv = new double[_ycoeff.Length];
        _sumCorr = 0.0;
    }

    private static void Shift(double[] ring, double v) {
        for (int i = ring.Length - 1; i >= 1; i--) ring[i] = ring[i - 1];
        if (ring.Length > 0) ring[0] = v;
    }

    private void BuildFilter(int order) {
        double corner = _expFactor * 4.0;                 // corner period multiplier
        var design = corner < 6.0 ? FilterDesign.Butterworth : FilterDesign.Bessel;
        var f = new ZFilterFactory(design, order, corner);
        _gain = f.Gain;
        _xcoeff = f.XCoeffs.ToArray();
        _ycoeff = f.YCoeffs.ToArray();
    }
}

internal enum FilterDesign { Bessel, Butterworth }

/// <summary>
/// Designs a low-pass IIR filter (S-plane prototype → bilinear transform → Z-plane
/// recurrence coefficients). Port of PHD2's <c>ZFilterFactory</c>, itself from
/// A. J. Fisher's mkfilter. Only the Bessel + Butterworth low-pass paths PHD2's
/// ZFilter uses are ported (no Chebyshev, no matched-z).
/// </summary>
internal sealed class ZFilterFactory {
    private const double TwoPi = 2.0 * Math.PI;

    public List<double> XCoeffs { get; } = new();
    public List<double> YCoeffs { get; } = new();
    public double Gain => Complex.Abs(_dcGain);

    private readonly FilterDesign _filt;
    private readonly int _order;
    private readonly double _rawAlpha1, _rawAlpha2;
    private Complex _dcGain;
    private double _warpedAlpha1;
    private readonly List<Complex> _spoles = new();
    private readonly List<Complex> _szeros = new();
    private readonly List<Complex> _zpoles = new();
    private readonly List<Complex> _zzeros = new();

    // Bessel S-plane prototype poles (one of each complex-conjugate pair),
    // verbatim from PHD2/mkfilter's table.
    private static readonly Complex[] BesselPoles = [
        new(-1.00000000000e+00, 0.00000000000e+00),
        new(-1.10160133059e+00, 6.36009824757e-01),
        new(-1.32267579991e+00, 0.00000000000e+00),
        new(-1.04740916101e+00, 9.99264436281e-01),
        new(-1.37006783055e+00, 4.10249717494e-01),
        new(-9.95208764350e-01, 1.25710573945e+00),
        new(-1.50231627145e+00, 0.00000000000e+00),
        new(-1.38087732586e+00, 7.17909587627e-01),
        new(-9.57676548563e-01, 1.47112432073e+00),
        new(-1.57149040362e+00, 3.20896374221e-01),
        new(-1.38185809760e+00, 9.71471890712e-01),
        new(-9.30656522947e-01, 1.66186326894e+00),
        new(-1.68436817927e+00, 0.00000000000e+00),
        new(-1.61203876622e+00, 5.89244506931e-01),
        new(-1.37890321680e+00, 1.19156677780e+00),
        new(-9.09867780623e-01, 1.83645135304e+00),
        new(-1.75740840040e+00, 2.72867575103e-01),
        new(-1.63693941813e+00, 8.22795625139e-01),
        new(-1.37384121764e+00, 1.38835657588e+00),
        new(-8.92869718847e-01, 1.99832584364e+00),
        new(-1.85660050123e+00, 0.00000000000e+00),
        new(-1.80717053496e+00, 5.12383730575e-01),
        new(-1.65239648458e+00, 1.03138956698e+00),
        new(-1.36758830979e+00, 1.56773371224e+00),
        new(-8.78399276161e-01, 2.14980052431e+00),
        new(-1.92761969145e+00, 2.41623471082e-01),
        new(-1.84219624443e+00, 7.27257597722e-01),
        new(-1.66181024140e+00, 1.22110021857e+00),
        new(-1.36069227838e+00, 1.73350574267e+00),
        new(-8.65756901707e-01, 2.29260483098e+00),
    ];

    public ZFilterFactory(FilterDesign f, int order, double cornerPeriodMult) {
        if (order <= 0) throw new ArgumentException("invalid filter order", nameof(order));
        if (cornerPeriodMult < 2.0)
            throw new ArgumentException("invalid corner period multiplier", nameof(cornerPeriodMult));
        _filt = f;
        _order = order;
        _rawAlpha1 = _rawAlpha2 = 1.0 / cornerPeriodMult;
        SPlane();
        Prewarp();
        Normalize();
        ZPlane();
        ExpandPoly();
    }

    private void SetPole(Complex z) { if (z.Real < 0.0) _spoles.Add(z); }

    private void SPlane() {
        if (_filt == FilterDesign.Bessel) {
            int p = (_order * _order) / 4;            // ptr into the table
            if ((_order & 1) != 0) SetPole(BesselPoles[p++]);
            for (int i = 0; i < _order / 2; i++) {
                SetPole(BesselPoles[p]);
                SetPole(Complex.Conjugate(BesselPoles[p]));
                p++;
            }
        } else { // Butterworth
            for (int i = 0; i < 2 * _order; i++) {
                double theta = (_order & 1) != 0
                    ? (i * Math.PI) / _order
                    : ((i + 0.5) * Math.PI) / _order;
                SetPole(Complex.FromPolarCoordinates(1.0, theta));
            }
        }
    }

    private void Prewarp() {
        // Bilinear pre-warp of the corner frequency (no matched-z here).
        _warpedAlpha1 = Math.Tan(Math.PI * _rawAlpha1) / Math.PI;
    }

    private void Normalize() {
        double w1 = TwoPi * _warpedAlpha1;
        for (int i = 0; i < _spoles.Count; i++) _spoles[i] *= w1;
        _szeros.Clear();
    }

    private static Complex Bilinear(Complex pz) => (2.0 + pz) / (2.0 - pz);

    private void ZPlane() {
        _zpoles.Clear();
        _zzeros.Clear();
        foreach (var sp in _spoles) _zpoles.Add(Bilinear(sp));
        foreach (var sz in _szeros) _zzeros.Add(Bilinear(sz));
        while (_zzeros.Count < _zpoles.Count) _zzeros.Add(new Complex(-1.0, 0.0));
    }

    private void ExpandPoly() {
        var top = Expand(_zzeros);
        var bot = Expand(_zpoles);
        _dcGain = Eval(top, Complex.One) / Eval(bot, Complex.One);

        double botBack = bot[^1].Real;
        XCoeffs.Clear();
        YCoeffs.Clear();
        for (int i = top.Count - 1; i >= 0; i--) XCoeffs.Add(+(top[i].Real / botBack));
        for (int i = bot.Count - 1; i >= 0; i--) YCoeffs.Add(-(bot[i].Real / botBack));
    }

    private static List<Complex> Expand(List<Complex> pz) {
        // Polynomial whose roots are pz: product of (z − pz[i]).
        var coeffs = new List<Complex> { Complex.One };
        for (int i = 0; i < pz.Count; i++) coeffs.Add(Complex.Zero);
        foreach (var w in pz) MultIn(w, coeffs);
        return coeffs;
    }

    private static void MultIn(Complex w, List<Complex> coeffs) {
        // Multiply the running polynomial by the factor (z − w).
        Complex nw = -w;
        for (int i = coeffs.Count - 1; i >= 1; i--) coeffs[i] = (nw * coeffs[i]) + coeffs[i - 1];
        coeffs[0] = nw * coeffs[0];
    }

    private static Complex Eval(List<Complex> coeffs, Complex z) {
        Complex sum = Complex.Zero;
        for (int i = coeffs.Count - 1; i >= 0; i--) sum = (sum * z) + coeffs[i];
        return sum;
    }
}
