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

using System.Runtime.CompilerServices;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>End-of-session actions: LIVE (time limit or target SNR), the
/// shared teardown, and host power-off in the Advanced Sequencer.</summary>
[TestFixture]
public class SessionEndActionsTests {
    private static readonly DateTime Start = new(2026, 11, 12, 22, 0, 0, DateTimeKind.Utc);

    [Test]
    public void LiveEnd_RunsOncePerStack() {
        var a = new LiveStackEndActions { HomeMount = true };
        Assert.Multiple(() => {
            Assert.That(LiveStackEndService.ShouldRun(Start, null, true, a), Is.True);
            Assert.That(LiveStackEndService.ShouldRun(Start, Start, true, a), Is.False, "already ran for this stack");
            Assert.That(LiveStackEndService.ShouldRun(Start.AddMinutes(90), Start, true, a), Is.True,
                "a Reset starts a new stack");
        });
    }

    [Test]
    public void LiveEnd_LeavesARunFedByAutorunOrPlanAlone() {
        var a = new LiveStackEndActions { ShutdownHost = true };
        Assert.That(LiveStackEndService.ShouldRun(Start, null, liveLoopFed: false, a), Is.False);
    }

    [Test]
    public void LiveEnd_NeedsAStackAndSomethingToDo() {
        Assert.Multiple(() => {
            Assert.That(LiveStackEndService.ShouldRun(null, null, true, new LiveStackEndActions()), Is.False,
                "nothing stacked yet");
            Assert.That(LiveStackEndService.ShouldRun(Start, null, true,
                new LiveStackEndActions { SaveStack = false }), Is.False);
            Assert.That(LiveStackEndService.ShouldRun(Start, null, true, new LiveStackEndActions()), Is.True,
                "saving the stack is on by default");
        });
    }

    [Test]
    public void LiveEnd_MapsOntoTheSharedTeardown() {
        var t = new LiveStackEndActions { HomeMount = true, WarmCamera = true, ShutdownHost = true }.ToTeardown();
        Assert.Multiple(() => {
            Assert.That(t.HomeMount && t.WarmCamera && t.ShutdownHost, Is.True);
            Assert.That(t.ParkMount, Is.False);
            Assert.That(t.NeedsCaptureStopped, Is.True, "a slew home or a power-off stops the capture first");
        });
    }

    [Test]
    public void Teardown_WarmingAloneLetsTheCaptureRun() {
        var t = new RigTeardownOptions { WarmCamera = true };
        Assert.Multiple(() => {
            Assert.That(t.Any, Is.True);
            Assert.That(t.NeedsCaptureStopped, Is.False);
            Assert.That(new RigTeardownOptions().Any, Is.False);
        });
    }

    [TestCase(1800, "30 min")]
    [TestCase(3600, "1 h")]
    [TestCase(5400, "1 h 30 min")]
    public void LiveEnd_NamesTheLimit(int seconds, string text) {
        Assert.That(LiveStackEndService.FormatMinutes(seconds), Is.EqualTo(text));
    }

    [Test]
    public void LiveEnd_SavedStackDefaultsOnAndTheRestOff() {
        var a = new LiveStackEndActions();
        Assert.Multiple(() => {
            Assert.That(a.SaveStack, Is.True);
            Assert.That(a.ToTeardown().Any, Is.False);
        });
    }

    [Test]
    public void ShutdownHost_IsAnAdvancedSequencerInstruction() {
        var doc = new SequenceDocument {
            Root = new SequentialContainer {
                Name = "End",
                Items = { new ParkMountInstruction { Name = "Park" }, new ShutdownHostInstruction { Name = "Off" } }
            }
        };
        var back = SequenceJson.Deserialize(SequenceJson.Serialize(doc));
        var items = ((SequentialContainer)back.Root).Items;
        Assert.Multiple(() => {
            Assert.That(items[1], Is.InstanceOf<ShutdownHostInstruction>());
            Assert.That(SequenceEntityJsonConverter.KnownTypes.Any(k => k.Type == "ShutdownHost"), Is.True);
        });
    }

    [Test]
    public async Task ShutdownHost_WithoutThePowerServiceFailsTheInstruction() {
        var ctx = new SequenceContext(null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.ThrowsAsync<InvalidOperationException>(
            () => new ShutdownHostInstruction().ExecuteAsync(ctx, CancellationToken.None));
        await Task.CompletedTask;
    }

    private static string Here([CallerFilePath] string p = "") => p;

    [Test]
    public void LiveTab_SaysTheLimitCountsTheWholeStack() {
        var html = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..",
            "src", "NINA.Polaris", "wwwroot", "index.html"));
        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("counted since the last Reset"));
            Assert.That(html, Does.Contain("liveStackTimeLeftText()"));
            Assert.That(html, Does.Contain("x-model=\"liveStackEndActions.shutdownHost\""));
            Assert.That(html, Does.Contain("x-model=\"endActions.shutdownHost\""));
        });
    }
}
