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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The camera can only be in one pair of hands. The gate serializes captures,
/// but a NATIVE video stream never calls CaptureAsync at all: the SDK pushes
/// frames on its own thread. So an autofocus exposed into a camera that was
/// mid-stream, the ZWO SDK stopped answering, and the camera had to be power
/// cycled, with nothing anywhere saying why the frame failed.
///
/// The gate is process-wide state, so this fixture is not parallelizable and
/// every test hands the camera back.
/// </summary>
[TestFixture]
[NonParallelizable]
public class CameraCaptureGateTests {

    [TearDown]
    public void ReleaseAnyLease() {
        // A failed assertion must not leave the camera owned for the next test.
        _lease?.Dispose();
        _lease = null;
        Assert.That(CameraCaptureGate.ExclusiveOwner, Is.Null, "the camera was left claimed");
    }

    private IDisposable? _lease;

    [Test]
    public void NoOwnerByDefault_AndACaptureRuns() {
        Assert.That(CameraCaptureGate.ExclusiveOwner, Is.Null);
        Assert.That(CameraCaptureGate.RunAsync(() => Task.FromResult(42)).Result, Is.EqualTo(42));
    }

    [Test]
    public void WhileTheStreamOwnsTheCamera_EveryOtherCaptureIsRefused() {
        _lease = CameraCaptureGate.TryAcquireExclusive("video stream", out var refusal);
        Assert.That(_lease, Is.Not.Null, refusal);
        Assert.That(CameraCaptureGate.ExclusiveOwner, Is.EqualTo("video stream"));

        var ex = Assert.ThrowsAsync<CameraBusyException>(
            () => CameraCaptureGate.RunAsync(() => Task.FromResult(1)));
        Assert.That(ex!.Message, Does.Contain("video stream"),
            "the message has to name what is holding the camera");
        Assert.That(ex.Message, Does.Contain("Stop it"));
    }

    [Test]
    public void TheOwnerItselfStillCaptures() {
        _lease = CameraCaptureGate.TryAcquireExclusive("video stream", out _);
        Assert.That(_lease, Is.Not.Null);
        // Loop mode takes its frames through the gate as the owner.
        Assert.That(CameraCaptureGate.RunAsync(() => Task.FromResult(7), asOwner: "video stream").Result,
                    Is.EqualTo(7));
    }

    [Test]
    public void ReleasingTheLeaseGivesTheCameraBack() {
        var lease = CameraCaptureGate.TryAcquireExclusive("video stream", out _);
        Assert.That(lease, Is.Not.Null);
        lease!.Dispose();
        Assert.That(CameraCaptureGate.ExclusiveOwner, Is.Null);
        Assert.DoesNotThrowAsync(() => CameraCaptureGate.RunAsync(() => Task.CompletedTask));
        lease.Dispose();   // idempotent
    }

    [Test]
    public void ASecondClaimIsRefused() {
        _lease = CameraCaptureGate.TryAcquireExclusive("video stream", out _);
        var second = CameraCaptureGate.TryAcquireExclusive("something else", out var refusal);
        Assert.That(second, Is.Null);
        Assert.That(refusal, Does.Contain("video stream"));
    }

    [Test]
    public async Task AStreamCannotStartOnTopOfARunningCapture() {
        // The other half of the rule: starting a stream while an exposure is
        // in flight wedges the driver the same way round.
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var capture = CameraCaptureGate.RunAsync(async () => {
            started.SetResult();
            await release.Task;
        });
        await started.Task;

        var lease = CameraCaptureGate.TryAcquireExclusive("video stream", out var refusal);
        Assert.That(lease, Is.Null, "must not claim the camera mid exposure");
        Assert.That(refusal, Does.Contain("capture is in progress"));

        release.SetResult();
        await capture;

        // And once the exposure is done, the stream may start.
        _lease = CameraCaptureGate.TryAcquireExclusive("video stream", out _);
        Assert.That(_lease, Is.Not.Null);
    }
}
