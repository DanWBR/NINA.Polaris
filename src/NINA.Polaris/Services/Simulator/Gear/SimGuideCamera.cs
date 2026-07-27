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

// Part of the built-in gear simulator (star field ported from PHD2 under
// BSD-3-Clause). This is the ICamera adapter: each CaptureAsync advances the
// shared SimGearState's error model and renders the star field, so frames track
// pulse guides issued through the paired SimMount.

using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.Simulator.Gear;

/// <summary>Simulated guide camera backed by the shared <see cref="SimGearState"/>.</summary>
public sealed class SimGuideCamera : ICamera {
    private readonly SimGearService _gear;
    private readonly Random _rng = new(12345);
    private volatile bool _connected;
    private int _bin = 1;
    private int _gain = 50;

    public SimGuideCamera(SimGearService gear) {
        _gear = gear;
    }

    public string DeviceName => "Simulator";
    public bool IsConnected => _connected;

    /// <summary>The simulator renders a mono star field, so say so rather
    /// than leaving the live stacker to infer it from an absent CFA.</summary>
    public bool? IsColorSensor => _connected ? false : null;

    public CameraStates State { get; private set; } = CameraStates.Idle;

    public double Temperature => -10.0;
    public bool CoolerOn => false;
    public double CoolerPower => 0;
    public int BinX => _bin;
    public int BinY => _bin;
    public int BitDepth => 16;
    public int MaxX => _gear.Params.Width;
    public int MaxY => _gear.Params.Height;
    public double PixelSizeX => 3.75;
    public double PixelSizeY => 3.75;
    public int Gain => _gain;
    public int GainMin => 0;
    public int GainMax => 100;

    /// <summary>Plausible bounds for a fake camera: 0.1 ms to 1 h. Nothing in
    /// the renderer enforces them, they just give the UI a range to bound its
    /// exposure picker with instead of leaving it unbounded.</summary>
    public double? MinExposureSeconds => _connected ? 0.0001 : null;
    public double? MaxExposureSeconds => _connected ? 3600.0 : null;
    public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
    public int SelectedIso => 0;

    public CameraCapabilities Capabilities => CameraCapabilities.Astro;

    public Task ConnectAsync(CancellationToken ct = default) {
        _connected = true;
        State = CameraStates.Idle;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default) {
        _connected = false;
        return Task.CompletedTask;
    }

    public async Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                               CancellationToken ct = default) {
        if (opts?.Gain is int g) _gain = g;
        int bin = opts?.BinX is int bx && bx > 0 ? bx : _bin;
        bin = Math.Clamp(bin, 1, 4);

        // Simulate the exposure passing so PE/drift/seeing evolve in real time.
        State = CameraStates.Exposing;
        if (exposureSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(exposureSeconds, 10.0)), ct);
        State = CameraStates.Download;

        var p = _gear.Params;
        int outW = p.Width / bin, outH = p.Height / bin;
        var buf = new ushort[(long)outW * outH];

        var (sx, sy) = _gear.State.AdvanceAndComputeShift(_gear.State.NowSec(), _rng);
        bool pierWest = _gear.State.PierSide == PierSide.pierWest;
        // Map the camera gain (0..100) onto the render brightness multiplier.
        double renderGain = p.StarGain * (0.4 + _gain / 100.0);
        _gear.StarField.FillImage(buf, outW, outH, bin, sx, sy, pierWest,
                                  exposureSeconds, renderGain, _rng);

        var props = new ImageProperties { Width = outW, Height = outH, BitDepth = 16 };
        var meta = new ImageMetaData();
        meta.Camera.Name = DeviceName;
        meta.Camera.Gain = _gain;
        meta.Camera.PixelSizeX = PixelSizeX;
        meta.Camera.PixelSizeY = PixelSizeY;
        State = CameraStates.Idle;
        return new BaseImageData(buf, props, meta);
    }

    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        _bin = Math.Clamp(binX <= 0 ? 1 : binX, 1, 4);
        return Task.CompletedTask;
    }

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetCoolerAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;
    public Task AbortExposureAsync(CancellationToken ct = default) {
        State = CameraStates.Idle;
        return Task.CompletedTask;
    }
    // ROI is a no-op: the native guider already works on full frames and the
    // simulator's centroid search is in software.
    public Task SetSubframeAsync(int x, int y, int width, int height, CancellationToken ct = default)
        => Task.CompletedTask;
}