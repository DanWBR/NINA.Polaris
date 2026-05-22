using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Headless.Services;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Client;

namespace NINA.Headless.Test;

/// <summary>
/// Tests focus on the parts of CameraStreamService that don't require
/// a live INDI / camera connection — config plumbing, default state,
/// graceful behaviour when no camera is connected. Full loop-driven
/// frame relay needs an actual ICamera (covered by integration tests
/// against a mock or simulator).
/// </summary>
[TestFixture]
public class CameraStreamServiceTests {

    private CameraStreamService MakeService() {
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance);
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        return new CameraStreamService(equip, relay, NullLogger<CameraStreamService>.Instance);
    }

    [Test]
    public void InitialState_IsIdleNotRunning() {
        var svc = MakeService();
        Assert.That(svc.IsRunning, Is.False);
        Assert.That(svc.Mode, Is.EqualTo("idle"));
        Assert.That(svc.FrameCount, Is.EqualTo(0));
        Assert.That(svc.Fps, Is.EqualTo(0));
        Assert.That(svc.LastError, Is.Null);
    }

    [Test]
    public void Start_NoCameraConnected_ThrowsInvalidOperation() {
        var svc = MakeService();
        var ex = Assert.Throws<InvalidOperationException>(
            () => svc.Start(new StreamConfig(ExposureSeconds: 0.1)));
        Assert.That(ex!.Message, Does.Contain("No camera"));
    }

    [Test]
    public async Task StopAsync_NotRunning_IsNoop() {
        var svc = MakeService();
        // Should not throw, should not change observable state.
        await svc.StopAsync();
        Assert.That(svc.IsRunning, Is.False);
        Assert.That(svc.Mode, Is.EqualTo("idle"));
    }

    [Test]
    public void Fps_WhenNotRunning_IsZero() {
        var svc = MakeService();
        Assert.That(svc.Fps, Is.EqualTo(0));
    }

    [Test]
    public void Dispose_DoesNotThrowWhenIdle() {
        var svc = MakeService();
        Assert.DoesNotThrow(() => svc.Dispose());
    }
}

/// <summary>
/// Validates the ICamera defaults the interface ships with — vendor SDK
/// cameras (Canon EDSDK / Nikon / Sony) and Alpaca cameras inherit these
/// without writing native-streaming code.
/// </summary>
[TestFixture]
public class ICameraDefaultsTests {

    private sealed class StubCamera : ICamera {
        public string DeviceName => "stub";
        public bool IsConnected => false;
        public NINA.Core.Enum.CameraStates State => NINA.Core.Enum.CameraStates.Idle;
        public double Temperature => 0;
        public bool CoolerOn => false;
        public double CoolerPower => 0;
        public int BinX => 1;
        public int BinY => 1;
        public int BitDepth => 16;
        public int MaxX => 1000;
        public int MaxY => 1000;
        public double PixelSizeX => 3.76;
        public double PixelSizeY => 3.76;
        public int Gain => 0;
        public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
        public int SelectedIso => 0;
        public CameraCapabilities Capabilities => CameraCapabilities.Astro;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IImageData> CaptureAsync(double exp, CaptureOptions? opts = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task SetBinningAsync(int bx, int by, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetTemperatureAsync(double t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCoolerAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortExposureAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // Note: default interface members in C# are only accessible via
    // the interface type, not the concrete one — these tests cast to
    // ICamera explicitly to exercise the defaults.

    [Test]
    public void DefaultIsStreaming_IsFalse() {
        ICamera cam = new StubCamera();
        Assert.That(cam.IsStreaming, Is.False);
    }

    [Test]
    public async Task DefaultStopVideoStreamAsync_DoesNotThrow() {
        ICamera cam = new StubCamera();
        await cam.StopVideoStreamAsync();
        // No state change to assert — just confirms the default Task.CompletedTask
        Assert.Pass();
    }

    [Test]
    public void DefaultStartVideoStreamAsync_ThrowsNotSupported() {
        ICamera cam = new StubCamera();
        Assert.ThrowsAsync<NotSupportedException>(
            async () => await cam.StartVideoStreamAsync(null));
    }

    [Test]
    public void DefaultSubscribeVideoFrames_ReturnsNoopDisposable() {
        ICamera cam = new StubCamera();
        var sub = cam.SubscribeVideoFrames(_ => { });
        Assert.That(sub, Is.Not.Null);
        Assert.DoesNotThrow(() => sub.Dispose());
    }

    [Test]
    public void CameraCapabilities_AstroPreset_SupportsVideoStreamIsFalseByDefault() {
        Assert.That(CameraCapabilities.Astro.SupportsVideoStream, Is.False,
            "Astro preset doesn't presume video stream support — IndiCamera flips it per-instance via CCD_VIDEO_STREAM probe");
    }

    [Test]
    public void CameraCapabilities_DslrPreset_SupportsVideoStreamIsFalse() {
        Assert.That(CameraCapabilities.Dslr.SupportsVideoStream, Is.False,
            "Vendor SDK DSLRs don't expose CCD_VIDEO_STREAM — capability defaults off");
    }
}
