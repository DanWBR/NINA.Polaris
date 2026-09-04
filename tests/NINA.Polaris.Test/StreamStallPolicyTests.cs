using System;
using NINA.Image.Portable.Streaming;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class StreamStallPolicyTests {
    [TestCase(0.005, 250)]   // 5 ms planetary: 2*5+500 = 510 → capped
    [TestCase(0.0, 250)]     // 500 → capped
    [TestCase(2.0, 250)]     // long exposure never blocks the SDK lock for seconds
    public void PollWait_IsCappedSoControlsNeverQueueBehindAnExposure(double exp, int expected) =>
        Assert.That(StreamStallPolicy.PollWaitMs(exp), Is.EqualTo(expected));

    [Test]
    public void PollWait_NeverBelow50ms() =>
        Assert.That(StreamStallPolicy.PollWaitMs(-1), Is.EqualTo(50));

    [TestCase(0.005, 3.0)]   // planetary: floor of 3 s
    [TestCase(0.5, 3.0)]     // 4 x 0.5 = 2 → floor
    [TestCase(2.0, 8.0)]     // 4 exposures
    public void StallAfter_FourExposures_AtLeastThreeSeconds(double exp, double seconds) =>
        Assert.That(StreamStallPolicy.StallAfter(exp).TotalSeconds, Is.EqualTo(seconds));

    [Test]
    public void IsStalled_OnlyAfterTheWindow() {
        var t0 = new DateTime(2026, 9, 3, 23, 0, 0, DateTimeKind.Utc);
        Assert.That(StreamStallPolicy.IsStalled(t0, t0.AddSeconds(2.9), 0.01), Is.False);
        Assert.That(StreamStallPolicy.IsStalled(t0, t0.AddSeconds(3.1), 0.01), Is.True);
        Assert.That(StreamStallPolicy.IsStalled(t0, t0.AddSeconds(7.9), 2.0), Is.False);
        Assert.That(StreamStallPolicy.IsStalled(t0, t0.AddSeconds(8.1), 2.0), Is.True);
    }
}
