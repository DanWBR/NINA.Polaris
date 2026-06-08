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

using System.Runtime.Versioning;
using NINA.Core.Enum;
using NINA.Image.Interfaces;

namespace NINA.Camera.NikonSdk;

/// <summary>
/// Skeleton <see cref="ICamera"/> implementation for Nikon DSLR /
/// mirrorless bodies. Wired into <c>EquipmentManager.SelectCamera</c>
/// and the Camera-card UI exactly like <c>CanonEdsdkCamera</c>, but
/// the SDK calls themselves throw <see cref="NotImplementedException"/>
/// because the actual MAID / Imaging-SDK binding work is open.
///
/// See <c>docs/dslr-windows-nikon.md</c> and
/// <see cref="NikonSdkRegistry"/> for the recommended path
/// (vendor the MIT-licensed
/// <a href="https://github.com/meklarian/MekNikon">MekNikon</a>
/// MAID bindings, or write fresh against the Nikon Imaging SDK
/// for Z-series).
/// </summary>
[SupportedOSPlatform("windows")]
public class NikonSdkCamera : ICamera {
    private readonly string _deviceId;

    public NikonSdkCamera(string deviceId) {
        _deviceId = deviceId;
    }

    public string DeviceName => _deviceId;
    public bool IsConnected => false;
    public CameraStates State => CameraStates.NoState;
    public double Temperature => double.NaN;
    public bool CoolerOn => false;
    public double CoolerPower => 0;
    public int BinX => 1;
    public int BinY => 1;
    public int BitDepth => 14;
    public int MaxX => 0;
    public int MaxY => 0;
    public double PixelSizeX => 0;
    public double PixelSizeY => 0;
    public int Gain => SelectedIso;
    public IReadOnlyList<int> IsoOptions { get; } = new[] {
        64, 100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600, 51200, 102400
    };
    public int SelectedIso { get; private set; } = 800;
    public CameraCapabilities Capabilities => CameraCapabilities.Dslr;

    public Task ConnectAsync(CancellationToken ct = default)
        => Throw();
    public Task DisconnectAsync(CancellationToken ct = default)
        => Task.CompletedTask;
    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task SetTemperatureAsync(double t, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task SetCoolerAsync(bool on, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task SetIsoAsync(int iso, CancellationToken ct = default) {
        SelectedIso = iso;
        return Task.CompletedTask;
    }
    public Task AbortExposureAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IImageData> CaptureAsync(double exposureSeconds,
            CaptureOptions? opts = null, CancellationToken ct = default)
        => Throw<IImageData>();

    private static Task Throw() => Task.FromException(MakeException());
    private static Task<T> Throw<T>() => Task.FromException<T>(MakeException());

    private static NotImplementedException MakeException()
        => new("Nikon SDK integration is a skeleton in this build. " +
               "Implementation pending, see docs/dslr-windows-nikon.md.");
}