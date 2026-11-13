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

using Microsoft.Extensions.Hosting;

namespace NINA.Polaris.Services;

/// <summary>
/// Keeps the main camera's sensor size on the active rig, so a night can be
/// planned at a desk with nothing connected and still get the field of view
/// of the camera that rig carries.
///
/// <para>The size used to live only on the profile, as one value for every
/// rig: with two rigs and two cameras, planning with nothing connected took
/// the active rig's focal length and the sensor of whichever camera had been
/// connected last, on any rig.</para>
///
/// <para>Server side and polled rather than done in the browser, so the rig
/// learns its camera even when no page is open during the connect.</para>
/// </summary>
public sealed class RigSensorMemoryService : BackgroundService {
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    /// <summary>A driver's rounding is not a different camera.</summary>
    internal const double ToleranceMm = 0.05;

    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly ILogger<RigSensorMemoryService> _logger;

    public RigSensorMemoryService(EquipmentManager equip, ProfileService profiles,
                                  ILogger<RigSensorMemoryService> logger) {
        _equip = equip;
        _profiles = profiles;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try { Check(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Rig sensor memory check failed"); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Check() {
        var cam = _equip.Camera;
        if (cam == null || !cam.IsConnected) return;
        var (w, h) = SensorMm(cam.MaxX, cam.MaxY, cam.PixelSizeX, cam.PixelSizeY);
        if (w <= 0 || h <= 0) return;
        var rig = _profiles.ActiveEquipmentProfile;
        if (rig == null || !NeedsUpdate(rig.SensorWidthMm, rig.SensorHeightMm, w, h)) return;
        _profiles.UpdateEquipmentProfile(rig.Id, r => {
            r.SensorWidthMm = Math.Round(w, 2);
            r.SensorHeightMm = Math.Round(h, 2);
        });
        _logger.LogInformation("Rig {Rig}: camera sensor {W:F2} x {H:F2} mm remembered for planning",
            rig.Name, w, h);
    }

    /// <summary>Sensor size from the resolution and the pixel size, or 0 when
    /// the driver has not reported either.</summary>
    internal static (double w, double h) SensorMm(int maxX, int maxY, double pxX, double pxY) {
        if (double.IsNaN(pxX) || double.IsNaN(pxY)) return (0, 0);
        if (pxY <= 0) pxY = pxX;
        var w = maxX > 0 && pxX > 0 ? maxX * pxX / 1000.0 : 0;
        var h = maxY > 0 && pxY > 0 ? maxY * pxY / 1000.0 : 0;
        return (w, h);
    }

    /// <summary>The sensor that planning uses: the connected camera when it
    /// reports its geometry, else the size remembered on the rig, else the
    /// profile-wide fallback. <see cref="PlanningSensor.Source"/> says which,
    /// so the UI can tell a remembered size from a guess.</summary>
    public static PlanningSensor Resolve(NINA.Image.Interfaces.ICamera? cam, EquipmentProfile? rig, UserProfile? profile) {
        if (cam != null && cam.IsConnected) {
            double px = 0, py = 0; int mx = 0, my = 0;
            try { px = cam.PixelSizeX; py = cam.PixelSizeY; mx = cam.MaxX; my = cam.MaxY; } catch { }
            var (w, h) = SensorMm(mx, my, px, py);
            if (w > 0 && h > 0) return new PlanningSensor(w, h, px > 0 ? px : py, "camera");
        }
        if (rig != null && rig.SensorWidthMm > 0 && rig.SensorHeightMm > 0)
            return new PlanningSensor(rig.SensorWidthMm, rig.SensorHeightMm,
                rig.CameraPixelSizeUm > 0 ? rig.CameraPixelSizeUm : 0, "rig");
        return new PlanningSensor(profile?.SensorWidthMm > 0 ? profile.SensorWidthMm : 23.5,
                                  profile?.SensorHeightMm > 0 ? profile.SensorHeightMm : 15.7, 0, "default");
    }

    internal static bool NeedsUpdate(double storedW, double storedH, double w, double h) =>
        Math.Abs(storedW - w) > ToleranceMm || Math.Abs(storedH - h) > ToleranceMm;
}

/// <summary>Sensor size for planning, in millimetres, the pixel size in µm
/// when known (0 otherwise), and where it came from: "camera", "rig" or
/// "default".</summary>
public sealed record PlanningSensor(double WidthMm, double HeightMm, double PixelUm, string Source) {
    /// <summary>Field of view in degrees at a focal length.</summary>
    public (double widthDeg, double heightDeg) FovDeg(double focalMm) => (
        2 * Math.Atan(WidthMm / (2 * focalMm)) * 180 / Math.PI,
        2 * Math.Atan(HeightMm / (2 * focalMm)) * 180 / Math.PI);
}
