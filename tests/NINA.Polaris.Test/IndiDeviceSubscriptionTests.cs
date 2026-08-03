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

using System.Reflection;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The INDI device adapters subscribe to the shared IndiClient in their
/// constructor. The client outlives every adapter, so an adapter that does not
/// unsubscribe stays in the client's delegate list for the process lifetime.
///
/// The memory is the smaller half. IndiCamera.OnBlobReceived only filters on
/// the device name, so once driver recovery re-selects the same camera, every
/// abandoned adapter decodes the incoming frame alongside the live one: N
/// recoveries, N decodes of every frame.
///
/// These tests read the event's backing delegate list directly, because that is
/// the thing that actually leaks. Asserting on observable behaviour instead
/// would need a connected INDI server.
/// </summary>
[TestFixture]
public class IndiDeviceSubscriptionTests {

    private static int HandlerCount(IndiClient client, string eventName) {
        // Field-like event: the compiler emits a private backing field of the
        // same name holding the multicast delegate.
        var field = typeof(IndiClient).GetField(eventName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.That(field, Is.Not.Null,
            $"IndiClient.{eventName} nao e mais um evento field-like; ajuste o teste.");
        var handler = (Delegate?)field!.GetValue(client);
        return handler?.GetInvocationList().Length ?? 0;
    }

    /// <summary>Every adapter that subscribes must give the subscription back.</summary>
    [TestCase("camera")]
    [TestCase("guider")]
    [TestCase("rotator")]
    [TestCase("flat")]
    [TestCase("dome")]
    [TestCase("weather")]
    public void DisposingADeviceReleasesItsSubscriptions(string kind) {
        var client = new IndiClient();
        var propsBefore = HandlerCount(client, nameof(IndiClient.PropertyChanged));
        var blobsBefore = HandlerCount(client, nameof(IndiClient.BlobReceived));

        IDisposable device = kind switch {
            "camera"  => new IndiCamera(client, "CCD Simulator"),
            "guider"  => new IndiGuider(client, "CCD Simulator"),
            "rotator" => new IndiRotator(client, "Rotator Simulator"),
            "flat"    => new IndiFlatDevice(client, "Flat Simulator"),
            "dome"    => new IndiDome(client, "Dome Simulator"),
            "weather" => new IndiWeather(client, "Weather Simulator"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.That(HandlerCount(client, nameof(IndiClient.PropertyChanged)),
            Is.GreaterThan(propsBefore),
            $"o adaptador '{kind}' nem chegou a assinar PropertyChanged");

        device.Dispose();

        Assert.That(HandlerCount(client, nameof(IndiClient.PropertyChanged)),
            Is.EqualTo(propsBefore), $"'{kind}' deixou um handler de PropertyChanged para tras");
        Assert.That(HandlerCount(client, nameof(IndiClient.BlobReceived)),
            Is.EqualTo(blobsBefore), $"'{kind}' deixou um handler de BlobReceived para tras");
    }

    /// <summary>The shape of the real failure: the same camera selected over and
    /// over, which is what driver recovery does all night.</summary>
    [Test]
    public void ReselectingTheSameCameraDoesNotAccumulateHandlers() {
        var client = new IndiClient();
        var blobsBefore = HandlerCount(client, nameof(IndiClient.BlobReceived));

        var live = new IndiCamera(client, "CCD Simulator");
        for (int recovery = 0; recovery < 20; recovery++) {
            var replacement = new IndiCamera(client, "CCD Simulator");
            live.Dispose();
            live = replacement;
        }

        Assert.That(HandlerCount(client, nameof(IndiClient.BlobReceived)),
            Is.EqualTo(blobsBefore + 1),
            "apos 20 recuperacoes deveria restar exatamente UMA camera ouvindo os BLOBs");

        live.Dispose();
        Assert.That(HandlerCount(client, nameof(IndiClient.BlobReceived)),
            Is.EqualTo(blobsBefore));
    }
}
