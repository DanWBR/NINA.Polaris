using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test.Equipment;

/// <summary>
/// Field session 2026-09-05: an ASI585MC Pro was open in Polaris (ZWO SDK) and
/// in indi_asi_ccd at the same time, and exposures failed. The guard decides
/// which INDI device is that same camera, so the matching has to recognise the
/// two naming styles without ever pairing two different cameras.
/// </summary>
[TestFixture]
public class NativeCameraIndiGuardTests {
    [Test]
    public void TheFieldCase_IndiAndSdkNamesOfOneCamera_Match() {
        Assert.That(NativeCameraIndiGuard.SameCamera("ZWO CCD ASI585MC Pro", "ZWO ASI585MC Pro"), Is.True);
    }

    [TestCase("SVBONY SV405CC", "SVBony SV405CC")]
    [TestCase("Player One Uranus-C", "PlayerOne Uranus-C")]
    [TestCase("ToupTek CCD ATR3CMOS", "ToupTek ATR3CMOS")]
    public void VendorAndBusWordsDoNotBlockAMatch(string indi, string native) {
        Assert.That(NativeCameraIndiGuard.SameCamera(indi, native), Is.True);
    }

    [TestCase("ZWO CCD ASI678MC", "ZWO ASI585MC Pro")]
    [TestCase("ZWO CCD ASI585MC", "ZWO ASI585MC Pro")]
    [TestCase("SVBONY SV405CC", "SVBONY SV605CC")]
    public void DifferentCamerasNeverMatch(string indi, string native) {
        Assert.That(NativeCameraIndiGuard.SameCamera(indi, native), Is.False);
    }

    [Test]
    public void ASecondCameraOfTheSameModelIsStillADifferentDevice() {
        // INDI suffixes a duplicate model, which is the only thing telling the
        // two apart; the guard must not disconnect the wrong one.
        Assert.That(NativeCameraIndiGuard.SameCamera("ZWO CCD ASI585MC Pro 2", "ZWO ASI585MC Pro"), Is.False);
    }

    [TestCase(null, "ZWO ASI585MC Pro")]
    [TestCase("", "ZWO ASI585MC Pro")]
    [TestCase("ZWO CCD ASI585MC Pro", null)]
    [TestCase("ZWO", "ZWO")]           // nothing but noise words: never a match
    public void MissingOrEmptyNamesNeverMatch(string? indi, string? native) {
        Assert.That(NativeCameraIndiGuard.SameCamera(indi, native), Is.False);
    }

    [TestCase("indi", true)]
    [TestCase("INDI", true)]
    [TestCase(null, true)]
    [TestCase("", true)]
    [TestCase("zwo-sdk", false)]
    [TestCase("ZWO-SDK", false)]
    [TestCase("svbony-sdk", false)]
    [TestCase("alpaca", false)]
    [TestCase("canon-edsdk", false)]
    public void OnlyTheIndiDriverIsTheIndiDeviceItself(string? driver, bool expected) {
        Assert.That(NativeCameraIndiGuard.IsIndiDriver(driver), Is.EqualTo(expected));
    }
}
