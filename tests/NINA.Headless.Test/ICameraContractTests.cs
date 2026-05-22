using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Test;

/// <summary>
/// Pins the <see cref="ICamera"/> abstraction the EquipmentManager,
/// capture endpoints and sequencer rely on. The interface lives in
/// NINA.Image.Portable and is the seam every new camera backend
/// (Canon EDSDK, Nikon SDK, Sony Camera Remote SDK) plugs into, so a
/// regression here cascades through the whole capture stack.
/// </summary>
[TestFixture]
public class ICameraContractTests {

    [Test]
    public void Capabilities_AstroPreset_EnablesCoolerAndBinning() {
        var c = CameraCapabilities.Astro;
        Assert.That(c.SupportsCooler, Is.True);
        Assert.That(c.SupportsBinning, Is.True);
        Assert.That(c.SupportsIso, Is.False,
            "Astronomy cameras report analogue gain, not ISO");
    }

    [Test]
    public void Capabilities_DslrPreset_EnablesIsoAndBulb() {
        var c = CameraCapabilities.Dslr;
        Assert.That(c.SupportsIso, Is.True);
        Assert.That(c.SupportsBulb, Is.True);
        Assert.That(c.SupportsCooler, Is.False,
            "DSLRs / mirrorless don't ship with active cooling");
        Assert.That(c.SupportsBinning, Is.False);
    }

    [Test]
    public void CaptureAsync_LegacyOverload_DelegatesToFullSignature() {
        // Default-interface-method overload must hand-off to the
        // 3-arg version so old callers (sequence engine, capture
        // endpoints) keep working.
        var fake = new FakeCamera();
        var ct = new CancellationTokenSource().Token;
        _ = ((ICamera)fake).CaptureAsync(5.0, ct);
        Assert.That(fake.LastExposureSeconds, Is.EqualTo(5.0));
        Assert.That(fake.LastOpts, Is.Null,
            "Legacy overload must pass opts=null to the full signature");
    }

    [Test]
    public void CaptureOptions_AllNullsByDefault() {
        var opts = new CaptureOptions();
        Assert.That(opts.Gain, Is.Null);
        Assert.That(opts.Iso, Is.Null);
        Assert.That(opts.BinX, Is.Null);
        Assert.That(opts.BinY, Is.Null);
        Assert.That(opts.ImageType, Is.Null);
    }

    private sealed class FakeCamera : ICamera {
        public string DeviceName => "fake";
        public bool IsConnected => false;
        public CameraStates State => CameraStates.Idle;
        public double Temperature => double.NaN;
        public bool CoolerOn => false;
        public double CoolerPower => 0;
        public int BinX => 1;
        public int BinY => 1;
        public int BitDepth => 16;
        public int MaxX => 0;
        public int MaxY => 0;
        public double PixelSizeX => 0;
        public double PixelSizeY => 0;
        public int Gain => 0;
        public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
        public int SelectedIso => 0;
        public CameraCapabilities Capabilities => CameraCapabilities.Astro;

        public double LastExposureSeconds { get; private set; } = double.NaN;
        public CaptureOptions? LastOpts { get; private set; } = new();   // sentinel non-null

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetBinningAsync(int x, int y, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetTemperatureAsync(double t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCoolerAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortExposureAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null, CancellationToken ct = default) {
            LastExposureSeconds = exposureSeconds;
            LastOpts = opts;
            var props = new ImageProperties { Width = 1, Height = 1, BitDepth = 16 };
            return Task.FromResult<IImageData>(new BaseImageData(new ushort[1], props));
        }
    }
}
