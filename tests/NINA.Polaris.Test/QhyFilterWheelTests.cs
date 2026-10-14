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

using NUnit.Framework;
using NINA.Camera.QhySdk;
using State = NINA.Camera.QhySdk.QhyFilterWheel.WheelState;

namespace NINA.Polaris.Test;

/// <summary>The QHY CFW protocol as GetQHYCCDCFWStatus and SendOrder2QHYCCDCFW
/// speak it: one ASCII byte, a hex digit for a slot, "N" or "/" while the
/// wheel turns.</summary>
[TestFixture]
public class QhyFilterWheelTests {
    [TestCase((byte)'0', 0)]
    [TestCase((byte)'6', 6)]
    [TestCase((byte)'9', 9)]
    [TestCase((byte)'A', 10)]
    [TestCase((byte)'f', 15)]
    public void ADigit_IsTheSlotTheWheelStandsOn(byte status, int slot) {
        Assert.That(QhyFilterWheel.ParseStatus(status, out var s), Is.EqualTo(State.Stopped));
        Assert.That(s, Is.EqualTo(slot));
    }

    [TestCase((byte)'N')]   // CFW2 / CFW3
    [TestCase((byte)'/')]   // A-series
    public void NOrSlash_MeansMoving(byte status) {
        Assert.That(QhyFilterWheel.ParseStatus(status, out var s), Is.EqualTo(State.Moving));
        Assert.That(s, Is.EqualTo(-1));
    }

    [TestCase((byte)0)]
    [TestCase((byte)'Z')]
    public void AnythingElse_IsUnknown(byte status) {
        Assert.That(QhyFilterWheel.ParseStatus(status, out _), Is.EqualTo(State.Unknown));
    }

    [Test]
    public void TheOrder_IsTheZeroBasedSlotAsOneHexDigit() {
        Assert.Multiple(() => {
            Assert.That(QhyFilterWheel.OrderFor(0), Is.EqualTo('0'));
            Assert.That(QhyFilterWheel.OrderFor(6), Is.EqualTo('6'));
            Assert.That(QhyFilterWheel.OrderFor(10), Is.EqualTo('A'));
        });
    }

    [Test]
    public void TheOrderAndTheStatus_AgreeOnEverySlot() {
        for (int slot = 0; slot < 16; slot++) {
            Assert.That(QhyFilterWheel.ParseStatus((byte)QhyFilterWheel.OrderFor(slot), out var back),
                Is.EqualTo(State.Stopped));
            Assert.That(back, Is.EqualTo(slot));
        }
    }

    [Test]
    public void Moving_BeforeConnect_IsFalse_AndAMoveIsRefused() {
        var wheel = new QhyFilterWheel("QHYminiCAM8-0000000000000000");
        Assert.That(wheel.IsConnected, Is.False);
        Assert.That(wheel.IsMoving, Is.False);
        Assert.ThrowsAsync<InvalidOperationException>(() => wheel.SetPositionAsync(1));
    }
}
