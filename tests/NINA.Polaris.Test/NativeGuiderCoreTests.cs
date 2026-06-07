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
            var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance, alpaca);
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
