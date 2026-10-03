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

using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Polaris.Services.Bge;
using NINA.Polaris.Services.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The browser as a place to run one 256x256 forward pass, and the routing
/// between it and the host.
///
/// Client-driven live stacking was removed from this project in August for good
/// reasons, so the bar for asking the browser to do anything again is: the host
/// owns the stack throughout, nothing waits on the browser, and a tab that goes
/// away or comes back late cannot put anything into the stack. These pin that.
/// </summary>
[TestFixture]
public class ClientBgeModelProducerTests {

    private const int Tile = 8;
    private static float[] Tensor(int tile) => new float[tile * tile * 3];
    private static float[] Output(int tile, float v) {
        var o = new float[tile * tile * 3];
        for (int i = 0; i < o.Length; i++) o[i] = v;
        return o;
    }

    [Test]
    public void WithNoBrowserEverSeen_ThereIsNowhereToRun() {
        var p = new ClientBgeModelProducer();

        Assert.That(p.CanRun, Is.False, "an open tab is the whole precondition");
        Assert.That(p.JobPending, Is.False);
    }

    /// <summary>The poll is the heartbeat. A tab that has just opened does not
    /// have to announce itself any other way, which is what keeps this free of
    /// the capability handshake the old client stacker needed.</summary>
    [Test]
    public void AskingForWork_IsWhatMakesTheBrowserPresent() {
        var p = new ClientBgeModelProducer();

        Assert.That(p.TryTakeJob(out _, out _, out _), Is.False, "nothing to do yet");
        Assert.That(p.CanRun, Is.True, "but now we know someone is listening");
    }

    [Test]
    public async Task AJobIsCollectedThenAnswered_AndTheRequestCompletes() {
        var p = new ClientBgeModelProducer();
        p.TryTakeJob(out _, out _, out _);                 // heartbeat

        var request = p.RunAsync(Tensor(Tile), Tile, CancellationToken.None);
        Assert.That(p.JobPending, Is.True, "the status block tells the browser to come and get it");

        Assert.That(p.TryTakeJob(out var id, out var tensor, out var tile), Is.True);
        Assert.Multiple(() => {
            Assert.That(tile, Is.EqualTo(Tile));
            Assert.That(tensor.Length, Is.EqualTo(Tile * Tile * 3));
            Assert.That(p.JobPending, Is.False, "collected, so stop advertising it");
        });

        Assert.That(p.TryCompleteJob(id, Output(Tile, 0.5f)), Is.True);
        var result = await request;
        Assert.That(result, Is.Not.Null);
        Assert.That(result![0], Is.EqualTo(0.5f));
    }

    [Test]
    public void OnlyOneJobAtATime() {
        var p = new ClientBgeModelProducer();
        p.TryTakeJob(out _, out _, out _);

        var first = p.RunAsync(Tensor(Tile), Tile, CancellationToken.None);
        var second = p.RunAsync(Tensor(Tile), Tile, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(first.IsCompleted, Is.False, "the first one is still out");
            Assert.That(second.IsCompletedSuccessfully, Is.True);
            Assert.That(second.Result, Is.Null, "and the second is refused, not queued");
        });
    }

    /// <summary>An answer to a job that is no longer outstanding is ignored. A
    /// backgrounded tab that wakes up and posts a model computed from a frame
    /// two binnings ago must not reach the stack.</summary>
    [Test]
    public void AnAnswerToAnUnknownJob_IsIgnored() {
        var p = new ClientBgeModelProducer();
        p.TryTakeJob(out _, out _, out _);
        p.RunAsync(Tensor(Tile), Tile, CancellationToken.None);
        p.TryTakeJob(out var id, out _, out _);

        Assert.Multiple(() => {
            Assert.That(p.TryCompleteJob("not-the-job", Output(Tile, 1f)), Is.False);
            Assert.That(p.TryCompleteJob(id, Output(Tile, 1f)), Is.True, "the real one still works");
        });
    }

    [Test]
    public void AnAnswerOfTheWrongSize_IsRefused() {
        var p = new ClientBgeModelProducer();
        p.TryTakeJob(out _, out _, out _);
        p.RunAsync(Tensor(Tile), Tile, CancellationToken.None);
        p.TryTakeJob(out var id, out _, out _);

        Assert.That(p.TryCompleteJob(id, new float[7]), Is.False);
    }

    /// <summary>A browser that goes away mid-job must not leave the corrector
    /// believing a model is on its way for the rest of the night.</summary>
    [Test]
    public async Task AnUnansweredJob_ExpiresInsteadOfHanging() {
        var p = new ClientBgeModelProducer {
            JobTtl = TimeSpan.FromMilliseconds(40),
        };
        p.TryTakeJob(out _, out _, out _);

        var request = p.RunAsync(Tensor(Tile), Tile, CancellationToken.None);
        p.TryTakeJob(out _, out _, out _);          // collected, then the tab dies
        Thread.Sleep(80);
        _ = p.JobPending;                            // any later look expires it

        var result = await request;
        Assert.That(result, Is.Null, "null is how the corrector learns to ask again");
    }

    [Test]
    public async Task AJobNobodyCollects_AlsoExpires() {
        var p = new ClientBgeModelProducer { JobTtl = TimeSpan.FromMilliseconds(40) };
        p.TryTakeJob(out _, out _, out _);

        var request = p.RunAsync(Tensor(Tile), Tile, CancellationToken.None);
        Thread.Sleep(80);
        _ = p.JobPending;

        Assert.That(await request, Is.Null);
    }

    [Test]
    public async Task ABrowserThatStopsPolling_StopsBeingAPlaceToRun() {
        var p = new ClientBgeModelProducer {
            ClientPresenceWindow = TimeSpan.FromMilliseconds(40),
        };
        p.TryTakeJob(out _, out _, out _);
        Assert.That(p.CanRun, Is.True);

        Thread.Sleep(80);

        Assert.That(p.CanRun, Is.False, "no tab, no client path, and that is not an error");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Reset_ReleasesAnOutstandingJob() {
        var p = new ClientBgeModelProducer();
        p.TryTakeJob(out _, out _, out _);
        var request = p.RunAsync(Tensor(Tile), Tile, CancellationToken.None);

        p.Reset();

        Assert.That(await request, Is.Null);
    }

    // --- routing -----------------------------------------------------

    private static HostBgeModelProducer HostWithNoAccelerator() {
        var cfg = new ConfigurationBuilder().Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        var gx = new GraXpertService(cfg, profiles, NullLogger<GraXpertService>.Instance);
        return new HostBgeModelProducer(gx);
    }

    /// <summary>Auto on a board with no accelerator: the browser, which is the
    /// case for every Raspberry Pi and every Allwinner board.</summary>
    [Test]
    public void Auto_FallsToTheClientWhenTheHostHasNoLane() {
        var client = new ClientBgeModelProducer();
        var router = new BgeProducerRouter(HostWithNoAccelerator(), client) { Where = BgeWhere.Auto };

        Assert.That(router.CanRun, Is.False, "no accelerator and no tab");
        Assert.That(router.Name, Is.EqualTo("none"));

        client.TryTakeJob(out _, out _, out _);     // a tab opens

        Assert.Multiple(() => {
            Assert.That(router.CanRun, Is.True);
            Assert.That(router.Name, Is.EqualTo("client"));
        });
    }

    /// <summary>Host means host. On a board with no lane that means no
    /// correction, which is stated rather than silently sent to the browser.</summary>
    [Test]
    public void Host_DoesNotFallToTheClient() {
        var client = new ClientBgeModelProducer();
        var router = new BgeProducerRouter(HostWithNoAccelerator(), client) { Where = BgeWhere.Host };
        client.TryTakeJob(out _, out _, out _);

        Assert.That(router.CanRun, Is.False);
        Assert.That(router.Name, Is.EqualTo("none"));
    }

    [Test]
    public void Client_IgnoresTheHostEvenIfItCould() {
        var client = new ClientBgeModelProducer();
        var router = new BgeProducerRouter(HostWithNoAccelerator(), client) { Where = BgeWhere.Client };

        Assert.That(router.CanRun, Is.False, "no tab yet");
        client.TryTakeJob(out _, out _, out _);
        Assert.That(router.Name, Is.EqualTo("client"));
    }

    [Test]
    public async Task ARouterWithNowhereToGo_RefusesWithoutThrowing() {
        var router = new BgeProducerRouter(HostWithNoAccelerator(), new ClientBgeModelProducer());

        Assert.That(await router.RunAsync(Tensor(Tile), Tile, CancellationToken.None), Is.Null);
    }
}
