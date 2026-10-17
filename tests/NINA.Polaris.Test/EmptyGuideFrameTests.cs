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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Camera.ZwoSdk;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>An ASI678MC on an Orange Pi 5 Pro's USB 2 port returned frames
/// with every pixel 0 on each exposure, each followed by a USB reset. The ZWO
/// driver handed them over as good frames and the guide Loop kept saying
/// "Looping" with nothing in the log.</summary>
[TestFixture]
public class EmptyGuideFrameTests {
    [Test]
    public void AnAllZeroAsiBufferIsEmpty() {
        Assert.That(AsiSdkCamera.IsEmptyFrame(new byte[4096]), Is.True);
    }

    [Test]
    public void OneSetByteMakesItARealFrame() {
        // A real read always carries offset and noise somewhere.
        var b = new byte[4096];
        b[4095] = 1;
        Assert.That(AsiSdkCamera.IsEmptyFrame(b), Is.False);
    }

    [Test]
    public void NoBufferAtAllIsNotCalledEmpty() {
        Assert.That(AsiSdkCamera.IsEmptyFrame(System.Array.Empty<byte>()), Is.False);
    }

    [Test]
    public void TheGuiderSeesTheSameEmptyFrameFromAnyDriver() {
        Assert.That(NativeGuider.IsEmptyFrame(new ushort[1920 * 1080]), Is.True);
        var real = new ushort[1920 * 1080];
        real[12345] = 280;
        Assert.That(NativeGuider.IsEmptyFrame(real), Is.False);
        Assert.That(NativeGuider.IsEmptyFrame(null), Is.False);
    }

    private static NativeGuider NewGuider() {
        var profiles = new ProfileService(new ConfigurationBuilder().Build(), NullLogger<ProfileService>.Instance);
        var equipment = new EquipmentManager(new IndiClient("localhost", 7624), NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        return new NativeGuider(equipment, profiles, NullLogger<NativeGuider>.Instance);
    }

    [Test]
    public void ThreeBadCapturesInARowRaiseOneAlert() {
        var g = NewGuider();
        var alerts = 0;
        g.Alert += _ => alerts++;
        g.NoteBadFrame("empty");
        g.NoteBadFrame("empty");
        Assert.That(alerts, Is.Zero, "one or two dropped frames happen and are not news");
        g.NoteBadFrame("empty frames");
        Assert.That(alerts, Is.EqualTo(1));
        Assert.That(g.LastAlert, Does.Contain("3 captures in a row").And.Contain("empty frames"));
        for (int i = 0; i < 20; i++) g.NoteBadFrame("empty frames");
        Assert.That(alerts, Is.EqualTo(1), "the streak is announced once, not per frame");
    }

    [Test]
    public void AGoodFrameEndsTheStreakAndSaysSo() {
        var g = NewGuider();
        for (int i = 0; i < 3; i++) g.NoteBadFrame("empty");
        g.NoteGoodFrame();
        Assert.That(g.LastAlert, Does.Contain("arriving again"));
        // A fresh streak needs three again before it is announced.
        var alerts = 0;
        g.Alert += _ => alerts++;
        g.NoteBadFrame("empty");
        g.NoteBadFrame("empty");
        Assert.That(alerts, Is.Zero);
    }

    [Test]
    public void AGoodFrameWithoutAStreakSaysNothing() {
        var g = NewGuider();
        g.NoteBadFrame("empty");
        g.NoteGoodFrame();
        Assert.That(g.LastAlert, Is.Null);
    }
}
