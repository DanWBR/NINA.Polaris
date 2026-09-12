// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors

using NINA.Polaris.Services.Alpaca;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class AlpacaRotatorTests {
    [TestCase("192.168.1.20:11111")]
    [TestCase("alpaca.local:6800:0")]
    public void FromDeviceId_AcceptsCanonicalDiscoveryIds(string deviceId) {
        Assert.DoesNotThrow(() => AlpacaRotator.FromDeviceId(deviceId));
    }

    [TestCase("")]
    [TestCase("host")]
    [TestCase("host:not-a-port")]
    [TestCase("host:0")]
    [TestCase("host:70000")]
    [TestCase("host:11111:-1")]
    [TestCase("host:11111:not-a-device-number")]
    [TestCase("host:11111:0:extra")]
    public void FromDeviceId_RejectsEveryMalformedIdWithTheSameHelpfulError(string deviceId) {
        var ex = Assert.Throws<ArgumentException>(() => AlpacaRotator.FromDeviceId(deviceId));
        Assert.That(ex!.Message, Does.Contain("host:port[:deviceNumber]"));
    }
}
