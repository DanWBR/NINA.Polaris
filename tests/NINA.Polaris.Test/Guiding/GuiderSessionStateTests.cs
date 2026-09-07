using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test.Guiding;

/// <summary>
/// The GUIDE buttons, and every caller that waits on the guider, ask one
/// question: is a guiding session running? A slew and a lost star are
/// transients INSIDE a session, not the end of one, so they must not read as
/// stopped (field report 2026-09-05: after a slew the badge fell back to the
/// idle label while the loop was still correcting). The client mirrors this
/// list in guideSessionUp(); the two are edited together.
/// </summary>
[TestFixture]
public class GuiderSessionStateTests {
    [TestCase("Guiding")]
    [TestCase("LostLock")]
    [TestCase("Slewing")]
    [TestCase("Paused")]
    public void TransientsCountAsARunningSession(string state) {
        Assert.That(NativeGuider.IsSessionState(state), Is.True, $"{state} is part of a guiding session");
    }

    [TestCase("Stopped")]
    [TestCase("Looping")]
    [TestCase("Selected")]
    [TestCase("Calibrating")]
    public void EverythingElseIsNotASession(string state) {
        Assert.That(NativeGuider.IsSessionState(state), Is.False, $"{state} is not a guiding session");
    }

    [Test]
    public void AnUnsetStateIsNotASession() {
        Assert.That(NativeGuider.IsSessionState(null), Is.False);
        Assert.That(NativeGuider.IsSessionState(""), Is.False);
    }
}
