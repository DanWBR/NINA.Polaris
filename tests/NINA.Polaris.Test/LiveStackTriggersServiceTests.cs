using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests focus on the pure helpers + the parts of LiveStackTriggersService
/// that don't need a live camera / mount / focuser / plate solver. Full
/// end-to-end trigger-fires-refocus needs hardware (covered by manual
/// integration testing).
/// </summary>
[TestFixture]
public class LiveStackTriggersServiceTests {

    [Test]
    public void AngularDistanceArcsec_SamePoint_IsZero() {
        var d = LiveStackTriggersService.AngularDistanceArcsec(12.0, 34.0, 12.0, 34.0);
        Assert.That(d, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void AngularDistanceArcsec_OneArcsecApartInDec_IsAboutOne() {
        // 1/3600 of a degree in Dec at any RA = 1 arcsec
        var d = LiveStackTriggersService.AngularDistanceArcsec(12.0, 34.0, 12.0, 34.0 + 1.0 / 3600);
        Assert.That(d, Is.EqualTo(1.0).Within(0.001));
    }

    [Test]
    public void AngularDistanceArcsec_AcrossRaAtEquator_ScalesWithCosDec() {
        // 1 hour of RA = 15° on the equator → 15 * 3600 arcsec at Dec 0
        var atEquator = LiveStackTriggersService.AngularDistanceArcsec(12.0, 0.0, 13.0, 0.0);
        Assert.That(atEquator, Is.EqualTo(54000).Within(1));
        // At Dec 60° the naive small-angle approximation Δα·cos(δ) says
        // 15°·0.5 = 7.5° = 27000". But the production code uses the
        // haversine formula (proper great-circle distance), so the actual
        // separation is slightly shorter, the path cuts across the
        // sphere rather than walking along the small circle. Haversine
        // gives ≈26943" here, off by about 57" from the cos-δ estimate.
        // Pin the haversine value so a future "optimization" back to the
        // wrong approximation gets caught.
        var at60 = LiveStackTriggersService.AngularDistanceArcsec(12.0, 60.0, 13.0, 60.0);
        Assert.That(at60, Is.EqualTo(26942.1).Within(0.5));
    }

    [Test]
    public void AngularDistanceArcsec_AntipodalIsHalfSphere() {
        var d = LiveStackTriggersService.AngularDistanceArcsec(0, 0, 12.0, 0);
        // 180° = 648000 arcsec
        Assert.That(d, Is.EqualTo(648000).Within(100));
    }

    [Test]
    public void LiveStackTriggers_DefaultsAreSafe() {
        var t = new LiveStackTriggers();
        // Default = both axes disabled, no triggers fire
        Assert.That(t.RefocusEnabled, Is.False);
        Assert.That(t.RecenterEnabled, Is.False);
        Assert.That(t.RefocusEveryNFrames, Is.EqualTo(0));
        Assert.That(t.RecenterEveryNFrames, Is.EqualTo(0));
        Assert.That(t.RefocusRequest, Is.Not.Null);
        Assert.That(t.RecenterToleranceArcsec, Is.GreaterThan(0));   // safe slew tolerance default
    }

    [Test]
    public void ResetTriggerState_ClearsStateBackToBlank() {
        // Constructed directly (no live infra needed for ResetTriggerState).
        var svc = MakeService();
        svc.ResetTriggerState();
        var st = svc.CurrentStatus;
        Assert.That(st.LastRefocusAt, Is.Null);
        Assert.That(st.LastRecenterAt, Is.Null);
        // Null instead of 0, the "no refocus yet" sentinel was
        // changed to nullable when the NaN/Infinity JSON-serialization
        // bug was fixed (0 / NaN now both map to null).
        Assert.That(st.LastRefocusFrame, Is.Null);
        Assert.That(st.LastRecenterFrame, Is.Null);
        Assert.That(st.ReferenceSolved, Is.False);
        Assert.That(st.ReferenceRaHours, Is.Null);
        Assert.That(st.ReferenceDecDeg, Is.Null);
    }

    [Test]
    public void CurrentStatus_BeforeAnyAction_IsAllNull() {
        var svc = MakeService();
        var st = svc.CurrentStatus;
        Assert.That(st.IsExecuting, Is.False);
        Assert.That(st.ExecutingKind, Is.Null);
        Assert.That(st.LastError, Is.Null);
    }

    private static LiveStackTriggersService MakeService() {
        var cfg = new ConfigurationBuilder().Build();
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var stack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance);
        var autoFocus = new AutoFocusService(equip, relay, NullLogger<AutoFocusService>.Instance);
        var solver = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance);
        var slew = new SlewCenterService(equip, solver, profiles,
            NullLogger<SlewCenterService>.Instance);
        return new LiveStackTriggersService(stack, profiles, equip, autoFocus, slew, solver,
            NullLogger<LiveStackTriggersService>.Instance);
    }
}
