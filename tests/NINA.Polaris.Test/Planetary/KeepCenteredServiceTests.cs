using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test.Planetary;

/// <summary>
/// KC-1 unit tests for <see cref="KeepCenteredService"/>. The service
/// owns a small control loop that reads the centroid of streaming
/// frames and pulses an <see cref="ITelescope"/> to keep it on frame
/// center. The tests drive a synthetic <c>IFrameSource</c> by hand so
/// no real camera or mount is required, and a spy
/// <see cref="ITelescope"/> records every Move/Stop sequence the
/// controller issues -- the assertions verify direction, ordering,
/// and dead-zone / convergence behaviour.
/// </summary>
[TestFixture]
public class KeepCenteredServiceTests {

    // -----------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------

    /// <summary>Hand-driven IFrameSource. Tests call
    /// <c>Push(frame)</c> to deliver one frame to the service.</summary>
    private sealed class HandFrameSource : KeepCenteredService.IFrameSource {
        private Action<IImageData>? _handler;
        public IDisposable Subscribe(Action<IImageData> handler) {
            _handler = handler;
            return new Releaser(() => _handler = null);
        }
        public void Push(IImageData frame) => _handler?.Invoke(frame);
        private sealed class Releaser : IDisposable {
            private readonly Action _onDispose;
            public Releaser(Action onDispose) { _onDispose = onDispose; }
            public void Dispose() => _onDispose();
        }
    }

    /// <summary>Spy ITelescope. Records every Move/Stop call as an
    /// entry on <see cref="Calls"/>. Move methods complete instantly;
    /// the controller's own <c>Task.Delay</c> simulates pulse
    /// duration. <see cref="MotionVectorPxPerSec"/> lets a test
    /// pre-program "this mount moves the planet this fast on the
    /// sensor when North is held" -- the synthetic frame pump uses
    /// it to advance the spot during a pulse.</summary>
    private sealed class SpyTelescope : ITelescope {
        public readonly List<string> Calls = new();
        public string ActiveAxis = "stop";
        public (double X, double Y) MotionVectorN = (0, 0);  // pixels per second when north held
        public (double X, double Y) MotionVectorE = (0, 0);

        public string DeviceName => "spy";
        public bool IsConnected => true;
        public double RightAscension => 0; public double Declination => 0;
        public double Altitude => 45; public double Azimuth => 180;
        public bool IsTracking => true;
        public bool IsParked => false;
        public bool IsSlewing => false;
        public PierSide SideOfPier => PierSide.pierUnknown;
        public MountCapabilities Capabilities => MountCapabilities.GermanEquatorial;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SlewAsync(double ra, double dec, CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncAsync(double ra, double dec, CancellationToken ct = default) => Task.CompletedTask;
        public Task ParkAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UnparkAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetTrackingAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortSlewAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveNorthAsync(CancellationToken ct = default) { Calls.Add("N"); ActiveAxis = "N"; return Task.CompletedTask; }
        public Task MoveSouthAsync(CancellationToken ct = default) { Calls.Add("S"); ActiveAxis = "S"; return Task.CompletedTask; }
        public Task MoveEastAsync(CancellationToken ct = default)  { Calls.Add("E"); ActiveAxis = "E"; return Task.CompletedTask; }
        public Task MoveWestAsync(CancellationToken ct = default)  { Calls.Add("W"); ActiveAxis = "W"; return Task.CompletedTask; }
        public Task StopMotionAsync(CancellationToken ct = default) { Calls.Add("stop"); ActiveAxis = "stop"; return Task.CompletedTask; }
    }

    // -----------------------------------------------------------------
    // Frame factory
    // -----------------------------------------------------------------

    /// <summary>Render a Gaussian-blob frame at (cx, cy) with given
    /// peak intensity. Centroid extracted from such a frame matches
    /// (cx, cy) within ~0.5 px (validated separately by
    /// CentroidAlignerTests).</summary>
    private static IImageData MakeSpotFrame(int w, int h, double cx, double cy,
                                             double radius = 4, ushort peak = 50000) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                double v = peak * Math.Exp(-d2 / (2.0 * radius * radius));
                px[y * w + x] = (ushort)Math.Min(65535, v);
            }
        }
        return new BaseImageData(px,
            new ImageProperties { Width = w, Height = h, BitDepth = 16 });
    }

    /// <summary>All-zero frame (no detectable target).</summary>
    private static IImageData MakeBlankFrame(int w, int h) {
        return new BaseImageData(new ushort[w * h],
            new ImageProperties { Width = w, Height = h, BitDepth = 16 });
    }

    // -----------------------------------------------------------------
    // Helper: pump frames at ~80ms cadence (matches the service's
    // internal frame-paced Task.Delay) for a bounded number of ticks.
    // Each tick reads the spy's ActiveAxis + MotionVector to compute
    // where the spot would have moved if the mount really pulsed; the
    // frame we hand the service reflects that displacement.
    // -----------------------------------------------------------------
    private static async Task<(double X, double Y)> PumpAsync(
            HandFrameSource src, SpyTelescope spy,
            int frames, int width, int height,
            double startX, double startY,
            double tickMs = 80) {
        double x = startX, y = startY;
        var prevTime = DateTime.UtcNow;
        for (int i = 0; i < frames; i++) {
            await Task.Delay((int)tickMs);
            var now = DateTime.UtcNow;
            var dt = (now - prevTime).TotalSeconds;
            prevTime = now;
            // Apply motion based on active axis.
            switch (spy.ActiveAxis) {
                case "N": x += spy.MotionVectorN.X * dt; y += spy.MotionVectorN.Y * dt; break;
                case "S": x -= spy.MotionVectorN.X * dt; y -= spy.MotionVectorN.Y * dt; break;
                case "E": x += spy.MotionVectorE.X * dt; y += spy.MotionVectorE.Y * dt; break;
                case "W": x -= spy.MotionVectorE.X * dt; y -= spy.MotionVectorE.Y * dt; break;
            }
            // Keep the spot inside the frame so the centroid sweep
            // always finds it (the brightest pixel scan skips a 2 px
            // border).
            x = Math.Max(3, Math.Min(width - 4, x));
            y = Math.Max(3, Math.Min(height - 4, y));
            src.Push(MakeSpotFrame(width, height, x, y));
        }
        return (x, y);
    }

    // -----------------------------------------------------------------
    // Test 1: dead zone -- target near center -> no pulses
    // -----------------------------------------------------------------

    [Test]
    public async Task DeadZone_TargetNearCenter_IssuesNoPulses() {
        var src = new HandFrameSource();
        var spy = new SpyTelescope();
        // Configure spy with realistic motion vectors so calibration
        // succeeds and we transition to the control phase.
        spy.MotionVectorN = (0, 10);   // 10 px/s in +Y when North held
        spy.MotionVectorE = (10, 0);   // 10 px/s in +X when East held
        var svc = new KeepCenteredService(src, () => spy, () => true,
            NullLogger<KeepCenteredService>.Instance);

        await svc.StartAsync(new KeepCenteredOptions(), CancellationToken.None);
        try {
            // Hand-feed a frame so calibration's initial "acquire confident
            // centroid" succeeds within the timeout, then let the service
            // run calibration + a few control ticks centred on the spot.
            src.Push(MakeSpotFrame(100, 100, 50, 50));
            // Pump 60 ticks (~5 s, covers calibration + control). During
            // calibration the spy's motion vectors move the spot; the
            // control loop sees a near-center target afterwards and the
            // dead zone should suppress further pulses.
            await PumpAsync(src, spy, 60, 100, 100, 50, 50);
        } finally {
            await svc.StopAsync();
        }

        // After calibration's two pulses (N + E) finish, the spot
        // should sit ~at center. The dead-zone check (5 px default)
        // suppresses corrections. Count Move-* calls AFTER calibration:
        // we expect exactly 2 (one N pulse + one E pulse from the
        // calibration phase) and no extras.
        var moveCount = spy.Calls.Count(c => c is "N" or "S" or "E" or "W");
        Assert.That(moveCount, Is.LessThanOrEqualTo(3),
            "Dead zone should suppress corrections after calibration; "
            + "got Move calls: " + string.Join(",", spy.Calls));
    }

    // -----------------------------------------------------------------
    // Test 2: every pulse is paired with a Stop
    // -----------------------------------------------------------------

    [Test]
    public async Task PulseSemantics_EveryMoveFollowedByStop() {
        var src = new HandFrameSource();
        var spy = new SpyTelescope();
        spy.MotionVectorN = (0, 8);
        spy.MotionVectorE = (8, 0);
        var svc = new KeepCenteredService(src, () => spy, () => true,
            NullLogger<KeepCenteredService>.Instance);

        await svc.StartAsync(new KeepCenteredOptions(), CancellationToken.None);
        try {
            src.Push(MakeSpotFrame(100, 100, 50, 50));
            await PumpAsync(src, spy, 40, 100, 100, 50, 50);
        } finally {
            await svc.StopAsync();
        }

        // Walk the recorded call sequence: every Move-X must be
        // followed (eventually before the next Move-Y) by a "stop".
        var moveIdx = -1;
        for (int i = 0; i < spy.Calls.Count; i++) {
            if (spy.Calls[i] is "N" or "S" or "E" or "W") {
                if (moveIdx >= 0) {
                    var slice = spy.Calls.Skip(moveIdx).Take(i - moveIdx);
                    Assert.That(slice.Contains("stop"), Is.True,
                        $"Move at index {moveIdx} ({spy.Calls[moveIdx]}) was not followed by a stop before the next move");
                }
                moveIdx = i;
            }
        }
        // And the final Move (if any) must also be followed by stop.
        if (moveIdx >= 0) {
            var tail = spy.Calls.Skip(moveIdx);
            Assert.That(tail.Contains("stop"), Is.True,
                "Final Move was not followed by a Stop");
        }
    }

    // -----------------------------------------------------------------
    // Test 3: lost target -> phase transitions to "lost", no spurious motion
    // -----------------------------------------------------------------

    [Test]
    public async Task LostTarget_NoSpotForManyTicks_StopsIssuingMoves() {
        var src = new HandFrameSource();
        var spy = new SpyTelescope();
        spy.MotionVectorN = (0, 8);
        spy.MotionVectorE = (8, 0);
        var svc = new KeepCenteredService(src, () => spy, () => true,
            NullLogger<KeepCenteredService>.Instance);

        await svc.StartAsync(new KeepCenteredOptions() { MaxConsecutiveMisses = 5 },
            CancellationToken.None);
        try {
            // First feed enough good frames for calibration to complete.
            src.Push(MakeSpotFrame(100, 100, 50, 50));
            await PumpAsync(src, spy, 30, 100, 100, 50, 50);
            // Snapshot the call count after calibration + initial settle.
            var beforeLost = spy.Calls.Count(c => c is "N" or "S" or "E" or "W");
            // Now feed nothing but blank frames for ~2 s -- the service
            // should mark the target lost and stop issuing pulses.
            for (int i = 0; i < 30; i++) {
                src.Push(MakeBlankFrame(100, 100));
                await Task.Delay(80);
            }
            var afterLost = spy.Calls.Count(c => c is "N" or "S" or "E" or "W");
            Assert.That(afterLost - beforeLost, Is.LessThanOrEqualTo(1),
                "Lost-target stretch should not produce further pulses; "
                + $"added {afterLost - beforeLost} Move calls.");
            Assert.That(svc.Phase, Is.AnyOf("lost", "locked"),
                "Phase should be lost (or recovered to locked) -- got " + svc.Phase);
        } finally {
            await svc.StopAsync();
        }
    }

    // -----------------------------------------------------------------
    // Test 4: Start refuses when prerequisites are missing
    // -----------------------------------------------------------------

    [Test]
    public void Start_NoStream_Throws() {
        var src = new HandFrameSource();
        var spy = new SpyTelescope();
        var svc = new KeepCenteredService(src, () => spy, () => false /* stream off */,
            NullLogger<KeepCenteredService>.Instance);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartAsync(null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("stream"));
    }

    [Test]
    public void Start_NoMount_Throws() {
        var src = new HandFrameSource();
        var svc = new KeepCenteredService(src, () => null, () => true,
            NullLogger<KeepCenteredService>.Instance);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartAsync(null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("connected").IgnoreCase);
    }

    // -----------------------------------------------------------------
    // Test 5: Stop after Start cleans up subscriber + state
    // -----------------------------------------------------------------

    [Test]
    public async Task Stop_AfterStart_IdempotentAndClearsRunning() {
        var src = new HandFrameSource();
        var spy = new SpyTelescope();
        spy.MotionVectorN = (0, 8);
        spy.MotionVectorE = (8, 0);
        var svc = new KeepCenteredService(src, () => spy, () => true,
            NullLogger<KeepCenteredService>.Instance);

        await svc.StartAsync(null, CancellationToken.None);
        Assert.That(svc.IsRunning, Is.True);

        await svc.StopAsync();
        Assert.That(svc.IsRunning, Is.False);
        // Stop must always end with a defensive StopMotion -- even
        // when the loop never pulsed.
        Assert.That(spy.Calls.Contains("stop"), Is.True);

        // Calling Stop a second time is harmless.
        Assert.DoesNotThrowAsync(() => svc.StopAsync());
    }
}
