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
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The video autofocus end to end: a stream in loop mode on a camera whose
/// frames blur with the distance of a fake focuser from its focus point. The
/// run must find that point from the stream's frames alone, while the stream
/// keeps the camera.
/// </summary>
[TestFixture]
public class VideoAutoFocusRunTests {
    private const int FocusPoint = 5000;
    private const string CameraName = "video-af-test-cam";
    private const string FocuserName = "video-af-test-focuser";

    private string _dir = "";
    private DateTime _startedAt;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-videoaf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _startedAt = DateTime.Now.AddSeconds(-1);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        // A finished run writes its log under the user's AF log folder. Remove
        // the ones this fixture wrote, and only those.
        try {
            var logs = NINA.Polaris.Services.Logging.AutoFocusRunLog.Dir;
            if (Directory.Exists(logs)) {
                foreach (var f in new DirectoryInfo(logs).EnumerateFiles("af_*.json")) {
                    if (f.LastWriteTime < _startedAt) continue;
                    if (File.ReadAllText(f.FullName).Contains(FocuserName)) f.Delete();
                }
            }
        } catch { }
    }

    private (AutoFocusService Af, CameraStreamService Stream, FakeFocuser Focuser) Build(int focuserStart) {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = _dir })
            .Build();
        var profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        var equip = new EquipmentManager(new IndiClient("localhost", 7624), NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var focuser = new FakeFocuser(focuserStart);
        typeof(EquipmentManager).GetProperty(nameof(EquipmentManager.Camera))!.SetValue(equip, new BlurCamera(focuser));
        typeof(EquipmentManager).GetProperty(nameof(EquipmentManager.Focuser))!.SetValue(equip, focuser);
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var stream = new CameraStreamService(equip, relay, NullLogger<CameraStreamService>.Instance,
            new CaptureProgressService());
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var native = new NativeGuider(equip, profile, NullLogger<NativeGuider>.Instance);
        var guiders = new ActiveGuiderProvider(profile, phd2, native);
        var af = new AutoFocusService(equip, relay, guiders, profile, NullLogger<AutoFocusService>.Instance, stream);
        return (af, stream, focuser);
    }

    [Test]
    public void Start_WithoutAStream_SaysToStartIt() {
        var (af, _, _) = Build(FocusPoint);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            af.Start(new AutoFocusRequest { FromStream = true }));
        Assert.That(ex!.Message, Does.Contain("video stream"));
    }

    [Test, CancelAfter(120_000)]
    public async Task Run_FindsTheFocusPointFromTheStream() {
        var (af, stream, focuser) = Build(FocusPoint + 260);
        stream.Start(new StreamConfig(ExposureSeconds: 0.005, ForceLoop: true));
        try {
            af.Start(new AutoFocusRequest {
                FromStream = true, StepSize = 100, OffsetSteps = 4,
                StreamFramesPerPoint = 3, RecordFilterMemory = false,
                BacklashIn = 0, BacklashOut = 0,
                RegionX = 0.25, RegionY = 0.25, RegionW = 0.5, RegionH = 0.5
            });
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (af.State == AutoFocusState.Running && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.That(af.State, Is.EqualTo(AutoFocusState.Idle), "the run didn't finish");
            var r = af.LastResult!;
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.FinalPosition, Is.EqualTo(FocusPoint).Within(60));
            Assert.That(focuser.Position, Is.EqualTo(r.FinalPosition));
            // The stream kept the camera for the whole run.
            Assert.That(stream.IsRunning, Is.True);
            Assert.That(af.Progress.FromStream, Is.True);
            // Ended sharper than it started (fit space is minus the log contrast).
            Assert.That(r.FinalMeasuredHfr, Is.LessThan(r.InitialHfr));
        } finally {
            af.Abort();
            await stream.StopAsync();
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task NextFrame_SkipsFramesFromBeforeTheMove() {
        var (_, stream, _) = Build(FocusPoint);
        stream.Start(new StreamConfig(ExposureSeconds: 0.005, ForceLoop: true));
        try {
            var notBefore = DateTime.UtcNow.AddMilliseconds(200);
            var (w, arrived) = await stream.NextFrameAsync(notBefore, f => f.Properties.Width,
                TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.That(arrived, Is.GreaterThan(notBefore));
            Assert.That(w, Is.EqualTo(BlurCamera.Size));
        } finally {
            await stream.StopAsync();
        }
    }

    [Test]
    public async Task NextFrame_WithoutAStream_Throws() {
        var (_, stream, _) = Build(FocusPoint);
        await Task.Yield();
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            stream.NextFrameAsync(DateTime.UtcNow, f => 0, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    // ─── Fakes ────────────────────────────────────────────────────

    private sealed class FakeFocuser : IFocuser {
        public FakeFocuser(int start) { Position = start; }
        public string DeviceName => FocuserName;
        public bool IsConnected => true;
        public int Position { get; private set; }
        public int MaxPosition => 20000;
        public double Temperature => double.NaN;
        public bool IsMoving => false;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveAbsoluteAsync(int position, CancellationToken ct = default) { Position = position; return Task.CompletedTask; }
        public Task MoveRelativeAsync(int steps, CancellationToken ct = default) { Position += steps; return Task.CompletedTask; }
        public Task AbortAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A crater field, Gaussian-blurred by the focuser's distance
    /// from <see cref="FocusPoint"/>, with a little noise.</summary>
    private sealed class BlurCamera : ICamera {
        public const int Size = 96;
        private readonly FakeFocuser _focuser;
        private readonly double[] _scene;
        private readonly Random _rng = new(3);

        public BlurCamera(FakeFocuser focuser) {
            _focuser = focuser;
            _scene = new double[Size * Size];
            var rng = new Random(11);
            for (int i = 0; i < _scene.Length; i++) _scene[i] = 800;
            for (int c = 0; c < 18; c++) {
                int cx = rng.Next(Size), cy = rng.Next(Size), r = rng.Next(2, 7);
                for (int y = Math.Max(0, cy - r - 2); y < Math.Min(Size, cy + r + 2); y++)
                    for (int x = Math.Max(0, cx - r - 2); x < Math.Min(Size, cx + r + 2); x++) {
                        double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        if (d < r) _scene[y * Size + x] = 1400;
                        else if (d < r + 1.5) _scene[y * Size + x] = 300;
                    }
            }
        }

        public string DeviceName => CameraName;
        public bool IsConnected => true;
        public NINA.Core.Enum.CameraStates State => NINA.Core.Enum.CameraStates.Idle;
        public double Temperature => 0;
        public bool CoolerOn => false;
        public double CoolerPower => 0;
        public int BinX => 1;
        public int BinY => 1;
        public int BitDepth => 16;
        public int MaxX => Size;
        public int MaxY => Size;
        public double PixelSizeX => 3.76;
        public double PixelSizeY => 3.76;
        public int Gain => 0;
        public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
        public int SelectedIso => 0;
        public CameraCapabilities Capabilities => CameraCapabilities.Astro;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<IImageData> CaptureAsync(double exp, CaptureOptions? opts = null, CancellationToken ct = default) {
            await Task.Delay(5, ct);
            double sigma = 0.4 + Math.Abs(_focuser.Position - FocusPoint) / 80.0;
            var img = GaussianBlur(_scene, Size, Size, sigma);
            var px = new ushort[img.Length];
            lock (_rng) {
                for (int i = 0; i < px.Length; i++)
                    px[i] = (ushort)Math.Clamp(Math.Round(img[i] + (_rng.NextDouble() - 0.5) * 6), 0, 65535);
            }
            return new BaseImageData(px, new ImageProperties { Width = Size, Height = Size, BitDepth = 16 });
        }

        private static double[] GaussianBlur(double[] src, int w, int h, double sigma) {
            int r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var k = new double[2 * r + 1];
            double s = 0;
            for (int i = -r; i <= r; i++) { k[i + r] = Math.Exp(-i * i / (2 * sigma * sigma)); s += k[i + r]; }
            for (int i = 0; i < k.Length; i++) k[i] /= s;
            var tmp = new double[w * h];
            var dst = new double[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    double a = 0;
                    for (int i = -r; i <= r; i++) a += k[i + r] * src[y * w + Math.Clamp(x + i, 0, w - 1)];
                    tmp[y * w + x] = a;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    double a = 0;
                    for (int i = -r; i <= r; i++) a += k[i + r] * tmp[Math.Clamp(y + i, 0, h - 1) * w + x];
                    dst[y * w + x] = a;
                }
            return dst;
        }

        public Task SetBinningAsync(int bx, int by, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetTemperatureAsync(double t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCoolerAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortExposureAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
