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

using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;
using NINA.Polaris.Endpoints;

namespace NINA.Polaris.Test;

/// <summary>
/// Anti-dew heater: how each backend reports it and what the card's on/off
/// switch writes. INDI has no standard property, so the three driver
/// families that publish one are read by name: TC_HEAT_CONTROL
/// (indi_toupbase), CCD_CONTROLS.AntiDewHeater (indi_asi_ccd) and
/// CCD_CONTROLS.LensHeaterPowerPerc (indi_playerone_ccd).
/// </summary>
[TestFixture]
public class AntiDewTests {

    private static IndiSwitchProperty ToupHeat(string on, params string[] elements) => new() {
        Name = IndiCamera.ToupHeatProperty,
        Values = elements.ToDictionary(e => e, e => e == on),
    };

    private static IndiNumberProperty Controls(string element, double value, double max) => new() {
        Name = "CCD_CONTROLS",
        Values = new() {
            ["Gain"] = new IndiNumberElement { Value = 100, Min = 0, Max = 600 },
            [element] = new IndiNumberElement { Value = value, Min = 0, Max = max },
        },
    };

    [Test]
    public void Toup_Levels_AreReadFromTheElementOn() {
        var off = IndiCamera.ReadAntiDew(ToupHeat("INDI_DISABLED", "INDI_DISABLED", "HEAT1", "HEAT2", "HEAT3"), null);
        Assert.That(off, Is.EqualTo(new CameraAntiDew(false, 0, 3)));
        var two = IndiCamera.ReadAntiDew(ToupHeat("HEAT2", "INDI_DISABLED", "HEAT1", "HEAT2", "HEAT3"), null);
        Assert.That(two, Is.EqualTo(new CameraAntiDew(true, 2, 3)));
        var single = IndiCamera.ReadAntiDew(ToupHeat("INDI_ENABLED", "INDI_DISABLED", "INDI_ENABLED"), null);
        Assert.That(single, Is.EqualTo(new CameraAntiDew(true, 1, 1)));
    }

    [Test]
    public void Asi_And_PlayerOne_AreReadFromCcdControls() {
        Assert.That(IndiCamera.ReadAntiDew(null, Controls("AntiDewHeater", 1, 1)),
            Is.EqualTo(new CameraAntiDew(true, 1, 1)));
        Assert.That(IndiCamera.ReadAntiDew(null, Controls("LensHeaterPowerPerc", 0, 100)),
            Is.EqualTo(new CameraAntiDew(false, 0, 100)));
    }

    [Test]
    public void NoHeater_IsNull() {
        Assert.That(IndiCamera.ReadAntiDew(null, null), Is.Null);
        var gainOnly = new IndiNumberProperty { Values = new() { ["Gain"] = new IndiNumberElement { Max = 600 } } };
        Assert.That(IndiCamera.ReadAntiDew(null, gainOnly), Is.Null);
        Assert.That(IndiCamera.AntiDewWrite(null, gainOnly, true, 0), Is.Null);
    }

    [Test]
    public void Toup_Write_PutsBackTheLastLevel_ElseTheTop() {
        var heat = ToupHeat("INDI_DISABLED", "INDI_DISABLED", "HEAT1", "HEAT2", "HEAT3");
        var last = IndiCamera.AntiDewWrite(heat, null, true, 2)!.Value.Switches!;
        Assert.That(last.Where(kv => kv.Value).Select(kv => kv.Key), Is.EqualTo(new[] { "HEAT2" }));
        Assert.That(last.Count, Is.EqualTo(4), "a one-of-many switch is sent whole");
        var top = IndiCamera.AntiDewWrite(heat, null, true, 0)!.Value.Switches!;
        Assert.That(top.Where(kv => kv.Value).Select(kv => kv.Key), Is.EqualTo(new[] { "HEAT3" }));
        var off = IndiCamera.AntiDewWrite(ToupHeat("HEAT3", "INDI_DISABLED", "HEAT1", "HEAT2", "HEAT3"), null, false, 3)!.Value.Switches!;
        Assert.That(off.Where(kv => kv.Value).Select(kv => kv.Key), Is.EqualTo(new[] { "INDI_DISABLED" }));
        var single = IndiCamera.AntiDewWrite(ToupHeat("INDI_DISABLED", "INDI_DISABLED", "INDI_ENABLED"), null, true, 0)!.Value.Switches!;
        Assert.That(single["INDI_ENABLED"], Is.True);
    }

    [Test]
    public void Controls_Write_OnlyTheHeaterElement() {
        var asi = IndiCamera.AntiDewWrite(null, Controls("AntiDewHeater", 0, 1), true, 0)!.Value.Numbers!;
        Assert.That(asi, Is.EqualTo(new Dictionary<string, double> { ["AntiDewHeater"] = 1 }));
        var poaDefault = IndiCamera.AntiDewWrite(null, Controls("LensHeaterPowerPerc", 0, 100), true, 0)!.Value.Numbers!;
        Assert.That(poaDefault["LensHeaterPowerPerc"], Is.EqualTo(50));
        var poaLast = IndiCamera.AntiDewWrite(null, Controls("LensHeaterPowerPerc", 0, 100), true, 30)!.Value.Numbers!;
        Assert.That(poaLast["LensHeaterPowerPerc"], Is.EqualTo(30));
        var poaOff = IndiCamera.AntiDewWrite(null, Controls("LensHeaterPowerPerc", 30, 100), false, 30)!.Value.Numbers!;
        Assert.That(poaOff["LensHeaterPowerPerc"], Is.EqualTo(0));
    }

    [Test]
    public void Native_OnLevels() {
        Assert.That(NINA.Camera.ToupTekSdk.ToupTekSdkCamera.HeatOnLevel(0, 3), Is.EqualTo(3));
        Assert.That(NINA.Camera.ToupTekSdk.ToupTekSdkCamera.HeatOnLevel(2, 3), Is.EqualTo(2));
        Assert.That(NINA.Camera.ToupTekSdk.ToupTekSdkCamera.HeatOnLevel(5, 3), Is.EqualTo(3), "a stale level above the top");
        Assert.That(NINA.Camera.AltairSdk.AltairSdkCamera.HeatOnLevel(1, 2), Is.EqualTo(1));
        Assert.That(NINA.Camera.PlayerOneSdk.PlayerOneSdkCamera.HeaterOnPower(0, 0), Is.EqualTo(50));
        Assert.That(NINA.Camera.PlayerOneSdk.PlayerOneSdkCamera.HeaterOnPower(0, 20), Is.EqualTo(20));
        Assert.That(NINA.Camera.PlayerOneSdk.PlayerOneSdkCamera.HeaterOnPower(35, 20), Is.EqualTo(35));
    }

    [Test]
    public void Default_IsNoHeater() {
        ICamera cam = new FakeCamera(connected: true, heater: false);
        Assert.That(cam.AntiDew, Is.Null);
        Assert.That(cam.SetAntiDewAsync(true).Result, Is.False);
    }

    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;

    [Test]
    public async Task Endpoint_SwitchesAHeater_AndRefusesWithoutOne() {
        var cam = new FakeCamera(connected: true, heater: true);
        Assert.That(Status(await CameraEndpoints.SetAntiDewAsync(cam, true)), Is.EqualTo(200));
        Assert.That(cam.AntiDew!.On, Is.True);
        Assert.That(Status(await CameraEndpoints.SetAntiDewAsync(cam, false)), Is.EqualTo(200));
        Assert.That(cam.AntiDew!.On, Is.False);

        Assert.That(Status(await CameraEndpoints.SetAntiDewAsync(new FakeCamera(true, false), true)), Is.EqualTo(400));
        Assert.That(Status(await CameraEndpoints.SetAntiDewAsync(new FakeCamera(false, true), true)), Is.EqualTo(400));
        Assert.That(Status(await CameraEndpoints.SetAntiDewAsync(null, true)), Is.EqualTo(400));
    }

    private sealed class FakeCamera : ICamera {
        private readonly bool _heater;
        private bool _on;
        public FakeCamera(bool connected, bool heater) { IsConnected = connected; _heater = heater; }
        public string DeviceName => "fake";
        public bool IsConnected { get; }
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
        public CameraAntiDew? AntiDew => _heater ? new CameraAntiDew(_on, _on ? 1 : 0, 1) : null;
        public Task<bool> SetAntiDewAsync(bool on, CancellationToken ct = default) {
            if (!_heater) return Task.FromResult(false);
            _on = on;
            return Task.FromResult(true);
        }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetBinningAsync(int x, int y, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetTemperatureAsync(double t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCoolerAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortExposureAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null, CancellationToken ct = default)
            => Task.FromResult<IImageData>(new BaseImageData(new ushort[1], new ImageProperties { Width = 1, Height = 1, BitDepth = 16 }));
    }
}
