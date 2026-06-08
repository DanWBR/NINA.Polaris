using NINA.Core.Enum;
using NINA.Guider.Portable;
using NINA.Polaris.Services.Simulator.Gear;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the built-in gear simulator (PHD2 gear_simulator port): the ST4
/// pulse -> pixel maths, Dec backlash, pier-side reversal, and the
/// camera/mount coupling that makes a pulse guide move the star field.
/// </summary>
[TestFixture]
public class SimGearTests {

    private static SimGearParams CleanParams() => new() {
        // Deterministic, error-free field for centroid + pulse maths.
        UsePeriodicError = false,
        UseDrift = false,
        UseSeeing = false,
        CameraAngleDeg = 0.0,
        NoiseSigma = 0.0,
        Background = 0.0,
        HotPixels = 0,
        ImageScale = 1.0,
        GuideRateArcsecPerSec = 15.0,
    };

    // ---- BacklashVal hysteresis ----

    [Test]
    public void Backlash_DeadbandsSmallReversals() {
        var b = new BacklashVal(5.0); // baseline upper = amount = 5
        Assert.That(b.Val, Is.EqualTo(5.0).Within(1e-9));
        b.Incr(10.0); // forward past the gap
        Assert.That(b.Val, Is.EqualTo(15.0).Within(1e-9));
        b.Incr(-3.0); // small reversal inside the 5px gap -> no movement
        Assert.That(b.Val, Is.EqualTo(15.0).Within(1e-9));
        b.Incr(-7.0); // now beyond the gap -> moves, lagging by the backlash
        Assert.That(b.Val, Is.EqualTo(10.0).Within(1e-9));
    }

    // ---- ST4 pulse -> pixel ----

    [Test]
    public void St4_WestPulse_ShiftsRaByGuideRate() {
        var st = new SimGearState(CleanParams()) { DeclinationDeg = 0.0 };
        st.St4Pulse(GuideDirections.guideWest, 1000); // 15 a-s/s * 1s / 1 a-s/px
        Assert.That(st.RawOffsets.ra, Is.EqualTo(15.0).Within(1e-6));
    }

    [Test]
    public void St4_RaPulse_ScalesByCosDeclination() {
        var st = new SimGearState(CleanParams()) { DeclinationDeg = 60.0 }; // cos = 0.5
        st.St4Pulse(GuideDirections.guideWest, 1000);
        Assert.That(st.RawOffsets.ra, Is.EqualTo(7.5).Within(1e-6));
    }

    [Test]
    public void St4_PierWest_ReversesDecPulses() {
        var p = CleanParams();
        p.DecBacklashArcsec = 0.0; // remove backlash so the sign is clean
        p.ReverseDecOnWestSide = true;

        var east = new SimGearState(p) { PierSide = PierSide.pierEast };
        east.St4Pulse(GuideDirections.guideNorth, 1000);
        double eastDec = east.RawOffsets.dec;

        var west = new SimGearState(p) { PierSide = PierSide.pierWest };
        west.St4Pulse(GuideDirections.guideNorth, 1000);
        double westDec = west.RawOffsets.dec;

        // Same NORTH command, opposite Dec effect across the flip.
        Assert.That(Math.Sign(eastDec), Is.Not.EqualTo(0));
        Assert.That(westDec, Is.EqualTo(-eastDec).Within(1e-6));
    }

    // ---- Star field rendering ----

    [Test]
    public void StarField_RendersStableCentroid_NoErrors() {
        var p = CleanParams();
        var field = new SimStarField(p);
        var buf = new ushort[p.Width * p.Height];
        var rng = new Random(1);

        field.FillImage(buf, p.Width, p.Height, 1, 0, 0, pierWest: false,
                        exptimeSec: 1.0, gain: 4500.0, rng);
        var (cx0, cy0) = BrightestCentroid(buf, p.Width, p.Height);

        field.FillImage(buf, p.Width, p.Height, 1, 0, 0, pierWest: false,
                        exptimeSec: 1.0, gain: 4500.0, rng);
        var (cx1, cy1) = BrightestCentroid(buf, p.Width, p.Height);

        Assert.That(cx1, Is.EqualTo(cx0).Within(0.1));
        Assert.That(cy1, Is.EqualTo(cy0).Within(0.1));
    }

    // ---- Camera + mount coupling (the point of the whole exercise) ----

    [Test]
    public async Task PulseGuide_ShiftsCapturedStar() {
        var gear = new SimGearService(CleanParams());
        gear.State.DeclinationDeg = 0.0; // cos(dec) = 1
        var cam = new SimGuideCamera(gear);
        var mount = new SimMount(gear);
        await cam.ConnectAsync();
        await mount.ConnectAsync();

        var img0 = await cam.CaptureAsync(0.0);
        int w = img0.Properties.Width, h = img0.Properties.Height;
        var (bx, by) = BrightestCentroid(img0.Data, w, h);
        var r0 = GuideStar.Find(img0.Data, w, h, bx, by, 15);
        Assert.That(r0.Found, Is.True, "should detect the saturated star");

        // Pulse WEST 1000ms -> +15 px on the RA (x) axis at cam angle 0.
        await mount.PulseGuideAsync(GuideDirections.guideWest, 1000);

        var img1 = await cam.CaptureAsync(0.0);
        var r1 = GuideStar.Find(img1.Data, w, h, r0.X + 15, r0.Y, 20);
        Assert.That(r1.Found, Is.True);

        Assert.That(r1.X - r0.X, Is.EqualTo(15.0).Within(1.0));
        Assert.That(r1.Y - r0.Y, Is.EqualTo(0.0).Within(1.0));
    }

    [Test]
    public void Driver_IsRegisteredAndPulseGuideCapable() {
        var gear = new SimGearService();
        var mount = new SimMount(gear);
        Assert.That(mount.Capabilities.SupportsPulseGuide, Is.True);
        var cam = new SimGuideCamera(gear);
        Assert.That(cam.Capabilities.SupportsRoi, Is.True);
        Assert.That(cam.DeviceName, Is.EqualTo("Simulator"));
    }

    // Brightest-pixel centroid over a small window (good enough to seed Find).
    private static (double x, double y) BrightestCentroid(ushort[] buf, int w, int h) {
        int peak = 0; ushort max = 0;
        for (int i = 0; i < buf.Length; i++) if (buf[i] > max) { max = buf[i]; peak = i; }
        int px = peak % w, py = peak / w;
        double sum = 0, sx = 0, sy = 0;
        for (int y = Math.Max(0, py - 3); y <= Math.Min(h - 1, py + 3); y++)
            for (int x = Math.Max(0, px - 3); x <= Math.Min(w - 1, px + 3); x++) {
                double v = buf[y * w + x];
                sum += v; sx += v * x; sy += v * y;
            }
        return sum > 0 ? (sx / sum, sy / sum) : (px, py);
    }
}
