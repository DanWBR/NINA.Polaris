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

using NINA.Core.Enum;
using NINA.Guider.Portable;
using NINA.Image.Interfaces;
using PortableGuideStep = NINA.Guider.Portable.GuideStep;

namespace NINA.Polaris.Services;

// Part of the NativeGuider class — split from NativeGuider.cs for
// readability. See NativeGuider.cs for the type overview + fields.
public sealed partial class NativeGuider {
    /// <summary>Rebuild the per-axis guide algorithms from the current rig
    /// settings (aggression / min-move / hysteresis) so a settings change made
    /// while guiding takes effect on the next frame without a stop/start.</summary>
    public void ApplyAlgorithmSettings() {
        if (IsConnected) BuildAlgorithms();
    }

    private void BuildAlgorithms() {
        // Per-axis algorithm selection (default hysteresis RA / resist-switch Dec,
        // PHD2's defaults). Lowpass/Lowpass2/Identity also available.
        // Predictive (PE + drift) tuning, shared by either axis if selected.
        double wormSec = Math.Max(0.0, Rig.NativePredictiveWormPeriodSec);
        int predWin = Rig.NativePredictiveWindowSamples;
        double predBlend = Math.Clamp(Rig.NativePredictiveBlend, 0.0, 1.0);
        // ZFilter exposure factor: 0 = use the PHD2 default (2.0); else clamp [1,20].
        double zExp = Rig.NativeZFilterExpFactor >= 1.0 ? Math.Min(Rig.NativeZFilterExpFactor, 20.0) : 2.0;
        _raAlgo = GuideAlgorithmFactory.Create(
            string.IsNullOrWhiteSpace(Rig.NativeRaAlgorithm) ? "hysteresis" : Rig.NativeRaAlgorithm,
            minMove: Math.Max(0.0, Rig.NativeMinMoveRaPx),
            aggression: Math.Clamp(Rig.NativeRaAggression, 0.0, 2.0),
            hysteresis: Math.Clamp(Rig.NativeRaHysteresis, 0.0, 0.99),
            wormPeriodSec: wormSec, predictiveWindow: predWin, predictiveBlend: predBlend,
            zfilterExpFactor: zExp);
        _decAlgo = GuideAlgorithmFactory.Create(
            string.IsNullOrWhiteSpace(Rig.NativeDecAlgorithm) ? "resistswitch" : Rig.NativeDecAlgorithm,
            minMove: Math.Max(0.0, Rig.NativeMinMoveDecPx),
            aggression: Math.Clamp(Rig.NativeDecAggression, 0.0, 2.0),
            hysteresis: Math.Clamp(Rig.NativeRaHysteresis, 0.0, 0.99),
            wormPeriodSec: wormSec, predictiveWindow: predWin, predictiveBlend: predBlend,
            zfilterExpFactor: zExp);
        _raAlgo.Reset();
        _decAlgo.Reset();
        _lastGuideMs = 0;
        // Dec backlash compensation: only when enabled on the rig AND the
        // calibration actually measured a backlash. Disabled by default
        // because an over-large value oscillates worse than no comp.
        double measuredBacklash = Rig.NativeBacklashComp ? _calibration.BacklashMs : 0;
        _backlashComp = new BacklashComp(measuredBacklash, Rig.NativeBacklashMaxMs);
        _backlashComp.Reset();
    }

    private static int RateToMs(double px, double ratePxPerMs) {
        if (ratePxPerMs <= 0) return 0;
        return (int)Math.Round(px / ratePxPerMs);
    }

    private void PushStep(PortableGuideStep p) {
        var step = new GuideStep {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(p.TimestampMs).UtcDateTime,
            RaPixels = p.RaRawPx,
            DecPixels = p.DecRawPx,
            RaArcsec = p.RaArcsec,
            DecArcsec = p.DecArcsec,
            SNR = p.Snr,
            Mass = 0,
            RaDuration = p.RaDurationMs,
            DecDuration = p.DecDurationMs,
            RaDirection = null,
            DecDirection = null,
            PredRaArcsec = p.PredRaArcsec,
            PredDecArcsec = p.PredDecArcsec,
            // Mark dither/settle steps so the guide charts can hatch the region.
            Dither = IsDithering || IsSettling
        };
        lock (_stepsLock) {
            _recentSteps.Add(step);
            if (_recentSteps.Count > MaxSteps) _recentSteps.RemoveAt(0);
            // Don't feed the deliberate dither excursion into the RMS/history:
            // while settling (dither chase or post-start), the error is the
            // distance back to a moved lock, not guiding performance. Counting
            // it would spike the displayed RMS and the error graph every dither.
            if (p.StarFound && !IsSettling) _rms.Add(p.RaArcsec, p.DecArcsec);
            var (rRa, rDec, rTot, pRa, pDec) = _rms.Compute();
            RmsRA = rRa; RmsDec = rDec; RmsTotal = rTot; PeakRA = pRa; PeakDec = pDec;
        }
        if (IsGuiding) SetAppState("Guiding");
        GuideStepReceived?.Invoke(step);
    }

    // ----- Live view (PHD2-style GUIDE UI) -----

    /// <summary>Snapshot the latest captured frame + star/lock overlay into the
    /// atomically-swapped <see cref="_view"/> for the WS payload and JPEG endpoint.</summary>
    private void BuildView(double primaryX, double primaryY, double snr, bool found) {
        var img = _lastFrame;
        if (img == null) return;
        var vf = new ViewFrame {
            Pixels = img.Data,
            Width = img.Properties.Width,
            Height = img.Properties.Height,
            BitDepth = img.Properties.BitDepth,
            IsBayered = img.Properties.IsBayered,
            OriginX = _lastFrameOriginX,
            OriginY = _lastFrameOriginY,
            // Pin the crosshair to the calibration anchor while calibrating so it
            // stays put; the moving star is conveyed by its marker (below).
            LockX = _calAnchorActive ? _calAnchorX : _lockX,
            LockY = _calAnchorActive ? _calAnchorY : _lockY,
            HaveLock = _haveLock,
            FrameId = ++_viewSeq
        };
        bool multi = Rig.NativeMultiStar && _multiStar.Count > 1;
        if (multi) {
            foreach (var s in _multiStar.Stars)
                vf.Stars.Add((s.CurX, s.CurY, s.Snr, s.IsPrimary, s.Found));
        } else if (found && !double.IsNaN(primaryX)) {
            vf.Stars.Add((primaryX, primaryY, snr, true, true));
        }
        _view = vf;
    }

    /// <summary>WS-serializable view: frame geometry, lock, star markers, and a
    /// star-profile cross-section + FWHM. Coordinates are full-sensor pixels;
    /// the frame buffer's top-left maps to (OriginX, OriginY).</summary>
    public object? ViewState {
        get {
            var vf = _view;
            if (vf == null) return null;
            var (profile, fwhm) = ComputeProfile(vf);
            return new {
                width = vf.Width,
                height = vf.Height,
                originX = vf.OriginX,
                originY = vf.OriginY,
                lockX = vf.HaveLock ? vf.LockX : (double?)null,
                lockY = vf.HaveLock ? vf.LockY : (double?)null,
                frameId = vf.FrameId,
                stars = vf.Stars.Select(s => new {
                    x = s.x, y = s.y, snr = s.snr, primary = s.primary, found = s.found
                }),
                profile,
                fwhm
            };
        }
    }

    /// <summary>Mid-row intensity cross-section (normalized 0..1) through the
    /// primary star + a FWHM estimate (px). Returns an empty profile when no
    /// primary star/lock is available.</summary>
    private static (double[] profile, double fwhm) ComputeProfile(ViewFrame vf) {
        double px = double.NaN, py = double.NaN;
        foreach (var s in vf.Stars) {
            if (s.primary && s.found) { px = s.x - vf.OriginX; py = s.y - vf.OriginY; break; }
        }
        if (double.IsNaN(px) && vf.HaveLock) { px = vf.LockX - vf.OriginX; py = vf.LockY - vf.OriginY; }
        if (double.IsNaN(px)) return (Array.Empty<double>(), 0);

        int cx = (int)Math.Round(px), cy = (int)Math.Round(py);
        if (cy < 0 || cy >= vf.Height || vf.Pixels.Length < (long)vf.Width * vf.Height)
            return (Array.Empty<double>(), 0);

        const int half = 15;
        int n = half * 2 + 1;
        var prof = new double[n];
        double mn = double.MaxValue, mx = double.MinValue;
        for (int i = 0; i < n; i++) {
            int x = cx - half + i;
            double v = (x >= 0 && x < vf.Width) ? vf.Pixels[cy * vf.Width + x] : 0;
            prof[i] = v;
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        double range = mx - mn;
        if (range < 1e-6) range = 1;
        for (int i = 0; i < n; i++) prof[i] = (prof[i] - mn) / range;
        return (prof, FwhmFromProfile(prof));
    }

    /// <summary>FWHM (px) from a normalized cross-section: width between the
    /// half-maximum crossings on either side of the peak, linearly interpolated.</summary>
    private static double FwhmFromProfile(double[] p) {
        if (p.Length < 3) return 0;
        int peak = 0;
        for (int i = 1; i < p.Length; i++) if (p[i] > p[peak]) peak = i;
        const double halfMax = 0.5; // normalized peak is 1, baseline 0
        double Cross(int from, int step) {
            for (int i = from; i >= 0 && i < p.Length; i += step) {
                if (p[i] <= halfMax) {
                    int prev = i - step;
                    if (prev < 0 || prev >= p.Length) return i;
                    double denom = p[prev] - p[i];
                    double frac = Math.Abs(denom) < 1e-9 ? 0 : (p[prev] - halfMax) / denom;
                    return prev + step * frac;
                }
            }
            return step < 0 ? 0 : p.Length - 1;
        }
        double left = Cross(peak, -1);
        double right = Cross(peak, 1);
        return Math.Max(0, right - left);
    }

    /// <summary>Encode the latest guide frame as an auto-stretched JPEG for the
    /// PHD2-style camera view. Returns null when no frame is available yet.</summary>
    public byte[]? EncodeViewJpeg(int maxDim = 600, int quality = 75, double gamma = 1.0) {
        var vf = _view;
        if (vf == null || vf.Pixels.Length < (long)vf.Width * vf.Height) return null;
        try {
            return NINA.Polaris.Services.Studio.FitsThumbnailer.RenderJpegFromBuffer(
                vf.Pixels, vf.Width, vf.Height, vf.BitDepth, maxDim, quality,
                guideStretch: true, bayer: vf.IsBayered, guideGamma: gamma);
        } catch {
            return null;
        }
    }

    private void EnsureConnected() {
        if (!IsConnected)
            throw new InvalidOperationException("Native guider not connected.");
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static async Task SettleAfterPulse(int pulseMs, CancellationToken ct) {
        // Small dwell after a pulse so the mount applies it before the next
        // measurement. Cap so calibration doesn't crawl.
        int dwell = Math.Clamp(pulseMs + 250, 100, 3000);
        try { await Task.Delay(dwell, ct); } catch (OperationCanceledException) { }
    }

    public void Dispose() {
        try { StopLoopAsync().Wait(2000); } catch { }
        _gate.Dispose();
    }
}
