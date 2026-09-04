using NINA.Guider.Portable;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The calibration retry used to call every miss a "dropped frame". The reason
/// string must separate a capture that never arrived from a frame the detector
/// rejected, and name the detector's verdict with its number.
/// </summary>
[TestFixture]
public class NativeGuiderCalibrationReasonTests {
    [Test]
    public void NoFrame_IsTheOnlyCameraReason() {
        var r = NativeGuider.CalibrationRetryReason(null, 0, 0, 50);
        Assert.That(r, Does.StartWith("No frame from the guide camera"));
    }

    [TestCase(GuideStarStatus.LowSnr, 2.4, 3.0, "too faint", "SNR 2.4")]
    [TestCase(GuideStarStatus.LowMass, 0.5, 1.0, "too faint", "SNR 0.5")]
    [TestCase(GuideStarStatus.HighHfd, 40.0, 12.3, "bloated", "HFD 12.3")]
    [TestCase(GuideStarStatus.LowHfd, 40.0, 0.4, "hot pixel", "HFD 0.4")]
    [TestCase(GuideStarStatus.Error, 0.0, 0.0, "Search window of 50 px", "left the frame")]
    public void DetectorVerdicts_NameTheProblemAndItsNumber(GuideStarStatus status, double snr, double hfd, string word, string number) {
        var r = NativeGuider.CalibrationRetryReason(status, snr, hfd, 50);
        Assert.That(r, Does.Contain(word));
        Assert.That(r, Does.Contain(number));
        Assert.That(r, Does.Not.Contain("dropped frame"));
    }
}
