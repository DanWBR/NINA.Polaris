// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class RotatorRangeTests {
    [TestCase(0, 90, true)]
    [TestCase(90, 90, true)]
    [TestCase(90.1, 90, false)]
    [TestCase(-0.1, 180, false)]
    [TestCase(180, 180, true)]
    [TestCase(360, 360, true)]
    [TestCase(double.NaN, 360, false)]
    public void Allows_UsesTheSameInclusiveRangeForEveryCaller(
            double angle, double maximum, bool expected) {
        Assert.That(RotatorRange.Allows(angle, maximum), Is.EqualTo(expected));
    }

    [TestCase(0, 360)]
    [TestCase(45, 360)]
    [TestCase(90, 90)]
    [TestCase(180, 180)]
    [TestCase(360, 360)]
    public void NormalizeMaximum_OnlyAcceptsSupportedTravelLimits(
            double supplied, double expected) {
        Assert.That(RotatorRange.NormalizeMaximum(supplied), Is.EqualTo(expected));
    }
}
