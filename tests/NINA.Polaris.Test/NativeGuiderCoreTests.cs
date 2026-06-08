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

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Guider.Portable;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Alpaca;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Unit coverage for the native autoguider's portable core
/// (NINA.Guider.Portable, ported PHD2 math) plus the
/// ActiveGuiderProvider backend-switch wiring.
/// </summary>
[TestFixture]
public class NativeGuiderCoreTests {

    // ---- GuideStar centroid ----

    private static ushort[] GaussianStar(int w, int h, double cx, double cy,
                                         double sigma, double peak, double bg) {
        var img = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                double dx = x - cx, dy = y - cy;
                double v = bg + peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                img[y * w + x] = (ushort)Math.Clamp(v, 0, 65535);
            }
        }
        return img;
    }

    [Test]
    public void GuideStar_FindsGaussianCentroid_WithinTenthPixel() {
        int w = 64, h = 64;
        double cx = 31.4, cy = 28.7;
        var img = GaussianStar(w, h, cx, cy, sigma: 1.8, peak: 8000, bg: 300);

        var r = GuideStar.Find(img, w, h, 31, 29);

        Assert.That(r.Found, Is.True, "star should be found");
        Assert.That(r.X, Is.EqualTo(cx).Within(0.1), "centroid X within 0.1px");
        Assert.That(r.Y, Is.EqualTo(cy).Within(0.1), "centroid Y within 0.1px");
        Assert.That(r.Snr, Is.GreaterThan(3.0), "SNR above the low-SNR floor");
    }

    [Test]
    public void GuideStar_EmptyFrame_ReportsLowMass() {
        int w = 48, h = 48;
        var flat = new ushort[w * h];
        Array.Fill(flat, (ushort)500);

        var r = GuideStar.Find(flat, w, h, 24, 24);

        Assert.That(r.Found, Is.False);
        Assert.That(r.Status, Is.EqualTo(GuideStarStatus.LowMass));
    }

    [Test]
    public void GuideStar_PureNoise_DoesNotReportFoundStar() {
        int w = 48, h = 48;
        var rng = new Random(1234);
        var noise = new ushort[w * h];
        for (int i = 0; i < noise.Length; i++) noise[i] = (ushort)(500 + rng.Next(-40, 40));

        var r = GuideStar.Find(noise, w, h, 24, 24);

        // Random noise has no real star: must not be flagged Ok/Saturated.
        Assert.That(r.Status, Is.AnyOf(GuideStarStatus.LowMass, GuideStarStatus.LowSnr));
        Assert.That(r.Found, Is.False);
    }

    // ---- Hysteresis algorithm ----

    [Test]
    public void Hysteresis_FirstMove_IsAggressionTimesInput() {
        // With lastMove == 0, out = (1-h)*input*aggression on the first call.
        var a = new HysteresisAlgorithm(hysteresis: 0.0, aggression: 0.7, minMove: 0.0);
        double r = a.Result(1.0);
        Assert.That(r, Is.EqualTo(0.7).Within(1e-9));
    }

    [Test]
    public void Hysteresis_BlendsHysteresisWeight() {
        // h=0.5, agg=1.0: first call out=0.5; second call with same input
        // = ((1-0.5)*1 + 0.5*0.5) = 0.75.
        var a = new HysteresisAlgorithm(hysteresis: 0.5, aggression: 1.0, minMove: 0.0);
        Assert.That(a.Result(1.0), Is.EqualTo(0.5).Within(1e-9));
        Assert.That(a.Result(1.0), Is.EqualTo(0.75).Within(1e-9));
    }

    [Test]
    public void Hysteresis_BelowMinMove_IsZero() {
        var a = new HysteresisAlgorithm(hysteresis: 0.1, aggression: 0.7, minMove: 0.5);
        Assert.That(a.Result(0.3), Is.EqualTo(0.0));
    }

    [Test]
    public void Hysteresis_Reset_ClearsLastMove() {
        var a = new HysteresisAlgorithm(hysteresis: 0.9, aggression: 1.0, minMove: 0.0);
        a.Result(5.0);          // builds up lastMove
        a.Reset();
        // After reset, first call again behaves like lastMove == 0.
        Assert.That(a.Result(1.0), Is.EqualTo(0.1).Within(1e-9)); // (1-0.9)*1
    }

    // ---- Resist-switch algorithm ----

    [Test]
    public void ResistSwitch_VetoesDirectionSwitch_ThenAllowsAfterCompellingHistory() {
        var a = new ResistSwitchAlgorithm(minMove: 0.15, aggression: 1.0, fastSwitch: false);
        // Establish a positive side with a run of positive errors.
        for (int i = 0; i < 6; i++) a.Result(1.0);
        // A single opposite-sign error should be vetoed (resisted).
        Assert.That(a.Result(-1.0), Is.EqualTo(0.0),
            "a lone direction reversal is resisted");
        // A sustained run the other way eventually gets through.
        double last = 0;
        for (int i = 0; i < 8; i++) last = a.Result(-1.0);
        Assert.That(Math.Abs(last), Is.GreaterThan(0.0),
            "compelling reverse history is eventually allowed");
    }

    // ---- MountCoordTransform ----

    [Test]
    public void MountCoordTransform_RoundTrip_Identity_OrthogonalAxes() {
        // With perfectly orthogonal axes (YAngle = XAngle + 90deg) the
        // forward + inverse transform is a clean rotation pair and
        // round-trips to machine precision.
        var cal = new GuideCalibration(
            XAngle: 0.40, YAngle: 0.40 + Math.PI / 2,
            XRate: 0.02, YRate: 0.018, DeclinationRad: 0.3, IsValid: true);

        double dx = 7.3, dy = -4.1;
        var (ra, dec) = MountCoordTransform.CameraToMount(cal, dx, dy);
        var (bx, by) = MountCoordTransform.MountToCamera(cal, ra, dec);

        Assert.That(bx, Is.EqualTo(dx).Within(1e-6));
        Assert.That(by, Is.EqualTo(dy).Within(1e-6));
    }

    [Test]
    public void MountCoordTransform_RoundTrip_NonOrthogonal_ApproximatesIdentity() {
        // PHD2's MountToCamera collapses the two skewed mount axes back
        // onto one rotation, so with a non-90deg orthogonality error the
        // round-trip is only approximate (matches PHD2 behaviour). It
        // still preserves the vector magnitude and stays close.
        var cal = new GuideCalibration(
            XAngle: 0.40, YAngle: 0.40 + Math.PI / 2 + 0.05,
            XRate: 0.02, YRate: 0.018, DeclinationRad: 0.3, IsValid: true);

        double dx = 7.3, dy = -4.1;
        var (ra, dec) = MountCoordTransform.CameraToMount(cal, dx, dy);
        var (bx, by) = MountCoordTransform.MountToCamera(cal, ra, dec);

        // Approximate (a small orthogonality error perturbs the result),
        // but it stays in the same neighbourhood as the input vector.
        Assert.That(bx, Is.EqualTo(dx).Within(0.5));
        Assert.That(by, Is.EqualTo(dy).Within(0.5));
    }

    [Test]
    public void RaRateAtDec_ScalesByInverseCosine() {
        var cal = new GuideCalibration(0, Math.PI / 2, 0.02, 0.02, 0.0, true);
        double dec = 0.5; // radians
        double expected = 0.02 / Math.Cos(dec);
        Assert.That(MountCoordTransform.RaRateAtDec(cal, dec),
            Is.EqualTo(expected).Within(1e-9));
    }

    [Test]
    public void ComputeMoveDurationMs_ClampsAndDeadbands() {
        // rate = 0.01 px/ms.
        // 5 px -> 500 ms, within [50, 2000] -> 500.
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(5.0, 0.01, 50, 2000),
            Is.EqualTo(500));
        // 0.2 px -> 20 ms, below minMove 50 -> 0.
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(0.2, 0.01, 50, 2000),
            Is.EqualTo(0));
        // 100 px -> 10000 ms, above max 2000 -> clamped to 2000.
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(100.0, 0.01, 50, 2000),
            Is.EqualTo(2000));
        // Guard: zero rate -> 0 (no divide-by-zero).
        Assert.That(MountCoordTransform.ComputeMoveDurationMs(5.0, 0.0, 50, 2000),
            Is.EqualTo(0));
    }

    // ---- Pier-side flip (meridian flip calibration mirroring) ----

    [Test]
    public void FlipForPierChange_AddsPiToRa_KeepsDec_WhenNoDecReverse() {
        var cal = new GuideCalibration(
            XAngle: 0.30, YAngle: 0.30 + Math.PI / 2,
            XRate: 0.02, YRate: 0.018, DeclinationRad: 0.4, IsValid: true,
            BacklashMs: 500, CalibrationPierSide: PierSide.pierEast);

        var flipped = MountCoordTransform.FlipForPierChange(cal, reverseDec: false);

        // RA angle rotates by pi (mod 2pi); Dec angle unchanged.
        Assert.That(MountCoordTransform.NormAngle(flipped.XAngle - cal.XAngle),
            Is.EqualTo(Math.PI).Within(1e-9).Or.EqualTo(-Math.PI).Within(1e-9));
        Assert.That(flipped.YAngle, Is.EqualTo(cal.YAngle).Within(1e-12));
        // Rates / dec / backlash preserved.
        Assert.That(flipped.XRate, Is.EqualTo(cal.XRate));
        Assert.That(flipped.YRate, Is.EqualTo(cal.YRate));
        Assert.That(flipped.DeclinationRad, Is.EqualTo(cal.DeclinationRad));
        Assert.That(flipped.BacklashMs, Is.EqualTo(cal.BacklashMs));
        Assert.That(flipped.IsValid, Is.True);
    }

    [Test]
    public void FlipForPierChange_AlsoFlipsDec_WhenReverseDecRequested() {
        var cal = new GuideCalibration(0.30, 0.30 + Math.PI / 2, 0.02, 0.018, 0.4, true);

        var flipped = MountCoordTransform.FlipForPierChange(cal, reverseDec: true);

        Assert.That(MountCoordTransform.NormAngle(flipped.YAngle - cal.YAngle),
            Is.EqualTo(Math.PI).Within(1e-9).Or.EqualTo(-Math.PI).Within(1e-9));
    }

    [Test]
    public void FlipForPierChange_ReversesRaCorrectionDirection() {
        // A meridian flip reverses the RA sense in the camera, so the same
        // camera-x offset must map to the opposite RA correction sign.
        var cal = new GuideCalibration(0.0, Math.PI / 2, 0.02, 0.02, 0.0, true);
        var (raBefore, _) = MountCoordTransform.CameraToMount(cal, 10.0, 0.0);

        var flipped = MountCoordTransform.FlipForPierChange(cal, reverseDec: false);
        var (raAfter, _) = MountCoordTransform.CameraToMount(flipped, 10.0, 0.0);

        Assert.That(Math.Sign(raAfter), Is.EqualTo(-Math.Sign(raBefore)));
        Assert.That(Math.Abs(raAfter), Is.EqualTo(Math.Abs(raBefore)).Within(1e-9));
    }

    [Test]
    public void FlipForPierChange_TwiceReturnsToOriginalAngles() {
        var cal = new GuideCalibration(0.65, 0.65 + Math.PI / 2, 0.02, 0.018, 0.2, true);

        var twice = MountCoordTransform.FlipForPierChange(
            MountCoordTransform.FlipForPierChange(cal, true), true);

        Assert.That(MountCoordTransform.NormAngle(twice.XAngle - cal.XAngle),
            Is.EqualTo(0.0).Within(1e-9));
        Assert.That(MountCoordTransform.NormAngle(twice.YAngle - cal.YAngle),
            Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void FlipForPierChange_InvalidCalibration_StaysInvalid() {
        var flipped = MountCoordTransform.FlipForPierChange(GuideCalibration.Invalid, false);
        Assert.That(flipped.IsValid, Is.False);
    }

    // ---- RmsCalculator ----

    [Test]
    public void RmsCalculator_KnownWindow_ComputesRmsAndPeak() {
        var rms = new RmsCalculator(10);
        rms.Add(3, 4);    // total 5
        rms.Add(-3, -4);  // total 5
        var (rmsRa, rmsDec, rmsTotal, peakRa, peakDec) = rms.Compute();
        // rmsRa = sqrt((9+9)/2) = 3; rmsDec = sqrt((16+16)/2)=4.
        Assert.That(rmsRa, Is.EqualTo(3.0).Within(1e-9));
        Assert.That(rmsDec, Is.EqualTo(4.0).Within(1e-9));
        Assert.That(rmsTotal, Is.EqualTo(5.0).Within(1e-9));
        Assert.That(peakRa, Is.EqualTo(3.0).Within(1e-9));
        Assert.That(peakDec, Is.EqualTo(4.0).Within(1e-9));
    }

    // ---- CalibrationProcess ----

    [Test]
    public void CalibrationProcess_DrivesWestThenSouth_ProducesValidCalibration() {
        // Synthetic linear star response: WEST pulses move the star +X,
        // SOUTH pulses move it +Y, both at a fixed rate per step. The
        // process should recover a valid calibration.
        const int pulseMs = 1000;
        var proc = new CalibrationProcess(pulseMs, distThresholdPx: 25.0,
            maxSteps: 60, declinationRad: 0.0);

        double x = 100, y = 100;
        const double perStep = 5.0; // px per pulse
        bool sawWest = false, sawSouth = false;
        CalibrationStep step = default;
        for (int i = 0; i < 200; i++) {
            step = proc.Tick(x, y);
            if (step.Done || step.Failed) break;
            if (!step.Pulse) continue;
            switch (step.Direction) {
                case GuideDirections.guideWest: x += perStep; sawWest = true; break;
                case GuideDirections.guideEast: x -= perStep; break;
                case GuideDirections.guideSouth: y += perStep; sawSouth = true; break;
                case GuideDirections.guideNorth: y -= perStep; break;
            }
        }

        Assert.That(sawWest, Is.True, "calibration should pulse WEST first");
        Assert.That(sawSouth, Is.True, "calibration should pulse SOUTH after recenter");
        Assert.That(step.Done, Is.True, "calibration should complete");
        Assert.That(proc.Result.IsValid, Is.True);
        Assert.That(proc.Result.XRate, Is.GreaterThan(0));
        Assert.That(proc.Result.YRate, Is.GreaterThan(0));
        // WEST moved +X -> xAngle ~ 0; SOUTH moved +Y -> yAngle ~ pi/2.
        Assert.That(proc.Result.XAngle, Is.EqualTo(0.0).Within(0.05));
        Assert.That(Math.Abs(proc.Result.YAngle), Is.EqualTo(Math.PI / 2).Within(0.05));
    }

    // ---- ActiveGuiderProvider wiring ----

    [Test]
    public void ActiveGuiderProvider_RoutesByGuiderDriver() {
        var tmp = Path.Combine(Path.GetTempPath(),
            "polaris-guider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Profiles:Directory"] = tmp
                })
                .Build();
            var profiles = new ProfileService(config, NullLogger<ProfileService>.Instance);
            var indi = new IndiClient();
            var alpaca = new AlpacaDiscoveryCache();
            var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance, alpaca,
                new NINA.Polaris.Services.Simulator.Gear.SimGearService());
            var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
            var native = new NativeGuider(equip, profiles, NullLogger<NativeGuider>.Instance);
            var provider = new ActiveGuiderProvider(profiles, phd2, native);

            // Default rig: PHD2.
            profiles.UpdateEquipmentProfile(profiles.ActiveEquipmentProfile.Id,
                r => r.GuiderDriver = "phd2");
            Assert.That(provider.Active.Backend, Is.EqualTo("phd2"));

            // Flip to native.
            profiles.UpdateEquipmentProfile(profiles.ActiveEquipmentProfile.Id,
                r => r.GuiderDriver = "native");
            Assert.That(provider.Active.Backend, Is.EqualTo("native"));
        } finally {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ---- Lowpass / Lowpass2 / factory ----

    [Test]
    public void Lowpass_DeadbandsBelowMinMove_AndCapsAtInput() {
        var algo = new LowpassAlgorithm(minMove: 0.5, slopeWeight: 5.0);
        Assert.That(algo.Result(0.2), Is.EqualTo(0.0)); // below minMove -> 0
        for (int i = 0; i < 20; i++) {
            double outp = algo.Result(2.0);
            Assert.That(Math.Abs(outp), Is.LessThanOrEqualTo(2.0 + 1e-9)); // never exceeds input
        }
    }

    [Test]
    public void Lowpass2_AttenuatesAndNeverReversesDirection() {
        var algo = new Lowpass2Algorithm(minMove: 0.2, aggressiveness: 80.0);
        Assert.That(algo.Result(1.0), Is.EqualTo(0.8).Within(1e-9)); // first pts pass through * 0.8
        for (int i = 0; i < 30; i++) {
            double inp = (i % 2 == 0) ? 1.0 : 0.8;
            double outp = algo.Result(inp);
            Assert.That(Math.Abs(outp), Is.LessThanOrEqualTo(inp + 1e-9));
            Assert.That(outp * inp, Is.GreaterThanOrEqualTo(0.0)); // never opposes input
        }
    }

    [Test]
    public void Factory_MapsNamesToAlgorithms() {
        Assert.That(GuideAlgorithmFactory.Create("hysteresis", 0.1, 0.7, 0.1), Is.TypeOf<HysteresisAlgorithm>());
        Assert.That(GuideAlgorithmFactory.Create("resistswitch", 0.1, 1.0, 0.0), Is.TypeOf<ResistSwitchAlgorithm>());
        Assert.That(GuideAlgorithmFactory.Create("lowpass", 0.1, 0.7, 0.1), Is.TypeOf<LowpassAlgorithm>());
        Assert.That(GuideAlgorithmFactory.Create("lowpass2", 0.1, 0.7, 0.1), Is.TypeOf<Lowpass2Algorithm>());
        Assert.That(GuideAlgorithmFactory.Create("identity", 0.1, 0.7, 0.1), Is.TypeOf<IdentityAlgorithm>());
        Assert.That(GuideAlgorithmFactory.Create("bogus", 0.1, 0.7, 0.1), Is.TypeOf<IdentityAlgorithm>()); // fallback
    }

    // ---- BacklashComp ----

    [Test]
    public void BacklashComp_DisabledWhenMeasuredZero_PassesThrough() {
        var bc = new BacklashComp(0);
        Assert.That(bc.Enabled, Is.False);
        // No comp ever added, regardless of direction changes.
        Assert.That(bc.Adjust(GuideDirections.guideNorth, 500), Is.EqualTo(500));
        Assert.That(bc.Adjust(GuideDirections.guideSouth, 500), Is.EqualTo(500));
        Assert.That(bc.Adjust(GuideDirections.guideNorth, 500), Is.EqualTo(500));
    }

    [Test]
    public void BacklashComp_AddsOnReversal_NotOnSameDirection() {
        var bc = new BacklashComp(300);
        Assert.That(bc.Enabled, Is.True);
        // First move establishes direction, no comp.
        Assert.That(bc.Adjust(GuideDirections.guideNorth, 400), Is.EqualTo(400));
        // Same direction again: still no comp.
        Assert.That(bc.Adjust(GuideDirections.guideNorth, 400), Is.EqualTo(400));
        // Reversal: add the measured backlash (300).
        Assert.That(bc.Adjust(GuideDirections.guideSouth, 400), Is.EqualTo(700));
        // Continue same (south) direction: no further comp.
        Assert.That(bc.Adjust(GuideDirections.guideSouth, 400), Is.EqualTo(400));
    }

    [Test]
    public void BacklashComp_ZeroRequest_DoesNotConsumeDirectionState() {
        var bc = new BacklashComp(200);
        bc.Adjust(GuideDirections.guideNorth, 400); // set last dir = north
        // A zero-length move must not flip the remembered direction.
        Assert.That(bc.Adjust(GuideDirections.guideSouth, 0), Is.EqualTo(0));
        // Next north move is still "same direction" -> no comp.
        Assert.That(bc.Adjust(GuideDirections.guideNorth, 400), Is.EqualTo(400));
    }

    [Test]
    public void BacklashComp_CapsAddedAmountAtMaxMs() {
        // measured 1000ms but capped at 250ms.
        var bc = new BacklashComp(1000, maxMs: 250);
        bc.Adjust(GuideDirections.guideNorth, 400);
        // Reversal adds at most maxMs.
        Assert.That(bc.Adjust(GuideDirections.guideSouth, 400), Is.EqualTo(650));
    }

    [Test]
    public void BacklashComp_TrimsAppliedAmountOnChatter() {
        var bc = new BacklashComp(400);
        int first = bc.AppliedMs;
        Assert.That(first, Is.EqualTo(400));
        // Drive repeated reversals; after 3 in a row the applied amount trims.
        bc.Adjust(GuideDirections.guideNorth, 300);
        bc.Adjust(GuideDirections.guideSouth, 300); // reversal 1
        bc.Adjust(GuideDirections.guideNorth, 300); // reversal 2
        bc.Adjust(GuideDirections.guideSouth, 300); // reversal 3 -> trim to 300
        Assert.That(bc.AppliedMs, Is.LessThan(first));
    }

    // ---- MultiStarTracker ----

    // Build a frame with several Gaussian stars at given centres.
    private static ushort[] MultiStarFrame(int w, int h, (double x, double y)[] centres,
                                           double sigma = 1.8, double peak = 9000, double bg = 300) {
        var img = new ushort[w * h];
        Array.Fill(img, (ushort)bg);
        foreach (var (cx, cy) in centres) {
            int x0 = Math.Max(0, (int)(cx - 6)), x1 = Math.Min(w - 1, (int)(cx + 6));
            int y0 = Math.Max(0, (int)(cy - 6)), y1 = Math.Min(h - 1, (int)(cy + 6));
            for (int y = y0; y <= y1; y++) {
                for (int x = x0; x <= x1; x++) {
                    double dx = x - cx, dy = y - cy;
                    double v = bg + peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                    int i = y * w + x;
                    if (v > img[i]) img[i] = (ushort)Math.Clamp(v, 0, 65535);
                }
            }
        }
        return img;
    }

    [Test]
    public void MultiStar_AveragesRigidFieldShift_FromAllStars() {
        int w = 128, h = 128;
        var refsArr = new (double x, double y)[] { (32, 40), (90, 35), (60, 95) };
        var tracker = new MultiStarTracker(searchRegion: 12);
        tracker.Reset(refsArr);

        // Shift the whole field by a known vector; every star moves identically.
        double sx = 2.3, sy = -1.4;
        var shifted = refsArr.Select(r => (r.x + sx, r.y + sy)).ToArray();
        var img = MultiStarFrame(w, h, shifted);

        var res = tracker.Update(img, w, h);

        Assert.That(res.Found, Is.True);
        Assert.That(res.UsedCount, Is.EqualTo(3));
        Assert.That(res.OffsetX, Is.EqualTo(sx).Within(0.15));
        Assert.That(res.OffsetY, Is.EqualTo(sy).Within(0.15));
    }

    [Test]
    public void MultiStar_RejectsOutlierStar() {
        int w = 128, h = 128;
        var refsArr = new (double x, double y)[] { (30, 30), (95, 40), (55, 100), (100, 100) };
        var tracker = new MultiStarTracker(searchRegion: 10, maxMiss: 10, outlierPx: 3.0);
        tracker.Reset(refsArr);

        // Three stars shift by (1.5, 1.0); the fourth jumps far (a bad match /
        // hot pixel) and must be rejected so it doesn't bias the average.
        double sx = 1.5, sy = 1.0;
        var pos = new (double x, double y)[] {
            (refsArr[0].x + sx, refsArr[0].y + sy),
            (refsArr[1].x + sx, refsArr[1].y + sy),
            (refsArr[2].x + sx, refsArr[2].y + sy),
            (refsArr[3].x + 9.0, refsArr[3].y - 8.0), // outlier
        };
        var img = MultiStarFrame(w, h, pos);

        var res = tracker.Update(img, w, h);

        Assert.That(res.Found, Is.True);
        // The outlier is dropped, so the consensus shift survives.
        Assert.That(res.OffsetX, Is.EqualTo(sx).Within(0.2));
        Assert.That(res.OffsetY, Is.EqualTo(sy).Within(0.2));
        Assert.That(res.UsedCount, Is.EqualTo(3), "outlier excluded from the average");
    }

    [Test]
    public void MultiStar_SurvivesLossOfPrimaryStar() {
        int w = 128, h = 128;
        var refsArr = new (double x, double y)[] { (30, 30), (95, 40), (60, 100) };
        var tracker = new MultiStarTracker(searchRegion: 10);
        tracker.Reset(refsArr);

        // Primary star absent this frame; the two secondaries still define the shift.
        double sx = -2.0, sy = 1.7;
        var pos = new (double x, double y)[] {
            (refsArr[1].x + sx, refsArr[1].y + sy),
            (refsArr[2].x + sx, refsArr[2].y + sy),
        };
        var img = MultiStarFrame(w, h, pos);

        var res = tracker.Update(img, w, h);

        Assert.That(res.Found, Is.True, "should still produce an offset without the primary");
        Assert.That(res.UsedCount, Is.EqualTo(2));
        Assert.That(res.OffsetX, Is.EqualTo(sx).Within(0.2));
        Assert.That(res.OffsetY, Is.EqualTo(sy).Within(0.2));
    }

    [Test]
    public void MultiStar_OffsetReferences_ShiftsAllRefsConsistently() {
        int w = 128, h = 128;
        var refsArr = new (double x, double y)[] { (40, 40), (90, 60) };
        var tracker = new MultiStarTracker(searchRegion: 10);
        tracker.Reset(refsArr);

        // Simulate a dither: move the desired lock by (5, -3). After shifting
        // the references, a frame with the stars still at their ORIGINAL spots
        // should report an offset of (-5, +3) (the field must move back).
        tracker.OffsetReferences(5, -3);
        var img = MultiStarFrame(w, h, refsArr);

        var res = tracker.Update(img, w, h);

        Assert.That(res.Found, Is.True);
        Assert.That(res.OffsetX, Is.EqualTo(-5.0).Within(0.2));
        Assert.That(res.OffsetY, Is.EqualTo(3.0).Within(0.2));
    }

    [Test]
    public void MultiStar_AllStarsLost_ReportsNotFound() {
        int w = 96, h = 96;
        var refsArr = new (double x, double y)[] { (30, 30), (60, 60) };
        var tracker = new MultiStarTracker(searchRegion: 8);
        tracker.Reset(refsArr);

        var flat = new ushort[w * h];
        Array.Fill(flat, (ushort)400);

        var res = tracker.Update(flat, w, h);

        Assert.That(res.Found, Is.False);
        Assert.That(res.UsedCount, Is.EqualTo(0));
    }

    // ---- CalibrationProcess backlash measurement ----

    [Test]
    public void CalibrationProcess_MeasuresDecBacklash_FromSyntheticSlack() {
        // Model gear slack: the first N SOUTH pulses take up backlash and the
        // star does NOT move; after that the star tracks at a fixed rate.
        const int pulseMs = 1000;
        const int slackSteps = 3;       // backlash = 3 pulses = 3000ms
        const double perStep = 5.0;
        var proc = new CalibrationProcess(pulseMs, distThresholdPx: 25.0,
            maxSteps: 80, declinationRad: 0.0, catchThresholdPx: 3.0);

        double x = 100, y = 100;
        int southSeen = 0;
        bool decClearing = false;
        CalibrationStep step = default;
        for (int i = 0; i < 400; i++) {
            step = proc.Tick(x, y);
            if (step.Done || step.Failed) break;
            if (!step.Pulse) continue;
            switch (step.Direction) {
                case GuideDirections.guideWest: x += perStep; break;
                case GuideDirections.guideEast: x -= perStep; break;
                case GuideDirections.guideSouth:
                    southSeen++;
                    // Absorb the first slackSteps SOUTH pulses (no motion).
                    if (southSeen > slackSteps) y += perStep;
                    decClearing = true;
                    break;
                case GuideDirections.guideNorth: y -= perStep; break;
            }
        }

        Assert.That(decClearing, Is.True);
        Assert.That(step.Done, Is.True, "calibration should complete");
        Assert.That(proc.Result.IsValid, Is.True);
        // Backlash should reflect the slack pulses consumed before the star
        // started moving (3 pulses * 1000ms), allowing one catch-step tolerance.
        Assert.That(proc.Result.BacklashMs, Is.GreaterThan(0));
        // Catch detection lags the actual take-up by up to one tick, so allow
        // a couple of pulses of slack around the true value.
        Assert.That(proc.Result.BacklashMs,
            Is.EqualTo(slackSteps * (double)pulseMs).Within(2.0 * pulseMs));
    }
}