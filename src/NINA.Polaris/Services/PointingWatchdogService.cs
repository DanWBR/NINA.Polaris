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

namespace NINA.Polaris.Services;

/// <summary>
/// Puts back a sync the mount dropped on its own during live stacking.
///
/// <para>A ZWO AM5 synced to a plate solve at 19:29 reported Dec -25.29 until
/// about 19:55, then -26.41 with nothing sent to it: the exact correction of
/// the first sync, undone. The stacked frames kept aligning a few dither
/// steps apart, so the telescope had not moved; the mount had only gone back
/// to its own model, 1.1 degrees off the sky, and SKY showed it there. The
/// AM3 and AM5 do this under other software too.</para>
///
/// <para>The live stack knows where each frame sat; the mount says where it
/// points. When the mount's position moves by many arcminutes between two
/// stacked frames whose field barely moved, the position is the thing that
/// changed, and the mount is synced back to where it said it was one frame
/// earlier.</para>
/// </summary>
public sealed class PointingWatchdogService : IHostedService {
    /// <summary>Smallest reported move treated as a jump, arcsec. A dither
    /// is tens of arcseconds; the AM5's dropped sync was over an hour of arc.</summary>
    internal const double MinJumpArcsec = 600;

    /// <summary>Field shift (px) accepted when the pixel scale is unknown.</summary>
    internal const double MaxFieldShiftPxWithoutScale = 150;

    private static readonly TimeSpan MinTimeBetweenSyncs = TimeSpan.FromMinutes(5);

    private readonly LiveStackingService _live;
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<PointingWatchdogService> _logger;
    private readonly bool _enabled;
    private readonly object _gate = new();

    private Sample? _prev;
    private DateTime _lastSyncUtc = DateTime.MinValue;

    private sealed record Sample(int Frame, double RaHours, double DecDeg, double Tx, double Ty, bool Flipped);

    public PointingWatchdogService(LiveStackingService live, EquipmentManager equip, ProfileService profiles,
            NotificationService notify, IConfiguration config, ILogger<PointingWatchdogService> logger) {
        _live = live;
        _equip = equip;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
        _enabled = config.GetValue("PointingWatchdog:Enabled", true);
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (_enabled) _live.FrameAligned += OnFrameAligned;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _live.FrameAligned -= OnFrameAligned;
        return Task.CompletedTask;
    }

    private void OnFrameAligned(LiveFrameAlignment frame) {
        _ = Task.Run(async () => {
            try { await CheckAsync(frame); }
            catch (Exception ex) { _logger.LogDebug(ex, "Pointing watchdog check failed"); }
        });
    }

    private async Task CheckAsync(LiveFrameAlignment frame) {
        var scope = _equip.Telescope;
        if (scope == null || !scope.IsConnected || scope.IsSlewing || !scope.IsTracking) {
            lock (_gate) _prev = null;
            return;
        }
        double ra = scope.RightAscension, dec = scope.Declination;
        if (double.IsNaN(ra) || double.IsNaN(dec)) return;
        var now = new Sample(frame.FrameCount, ra, dec, frame.Tx, frame.Ty, frame.Flipped);

        Sample? prev;
        lock (_gate) {
            prev = _prev;
            _prev = now;
        }
        // A new stack, or an orientation change (a meridian flip moves the
        // mount legitimately and turns the field): start over from here.
        if (prev == null || frame.FrameCount <= prev.Frame || frame.Flipped != prev.Flipped) return;

        double mountMove = SeparationArcsec(prev.RaHours, prev.DecDeg, ra, dec);
        double fieldPx = Math.Sqrt(Math.Pow(frame.Tx - prev.Tx, 2) + Math.Pow(frame.Ty - prev.Ty, 2));
        double? scale = PixelScaleArcsec();
        if (!IsCoordinateJump(mountMove, fieldPx, scale)) return;

        if (DateTime.UtcNow - _lastSyncUtc < MinTimeBetweenSyncs) {
            _logger.LogWarning(
                "Pointing watchdog: the mount's position moved {Move:F0}\" while the field moved {Field:F0} px, "
                + "but it was re-synced less than {Min} min ago; leaving it alone", mountMove, fieldPx,
                MinTimeBetweenSyncs.TotalMinutes);
            return;
        }

        _logger.LogWarning(
            "Pointing watchdog: the mount's reported position jumped {Move:F0}\" (RA {Ra0:F4}h Dec {Dec0:F3} -> "
            + "RA {Ra1:F4}h Dec {Dec1:F3}) between stacked frames {F0} and {F1}, while the field moved only "
            + "{Field:F0} px. Syncing it back to RA {Ra0:F4}h Dec {Dec0:F3}.",
            mountMove, prev.RaHours, prev.DecDeg, ra, dec, prev.Frame, frame.FrameCount, fieldPx);
        try {
            await scope.SyncAsync(prev.RaHours, prev.DecDeg);
            _lastSyncUtc = DateTime.UtcNow;
            lock (_gate) _prev = now with { RaHours = prev.RaHours, DecDeg = prev.DecDeg };
            _notify.Push("warn",
                $"The mount's reported position jumped {mountMove / 60:F0}′ while the camera stayed on the same field "
                + "(the mount dropped its sync). Polaris synced it back.", 15000);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Pointing watchdog: re-sync failed");
        }
    }

    /// <summary>
    /// Did the mount's coordinates change without the telescope moving?
    /// <paramref name="fieldShiftPx"/> is how far the stacked field moved
    /// between the same two frames; <paramref name="scaleArcsecPerPx"/>
    /// turns it into arcseconds when known.
    /// </summary>
    internal static bool IsCoordinateJump(double mountMoveArcsec, double fieldShiftPx, double? scaleArcsecPerPx) {
        if (mountMoveArcsec < MinJumpArcsec) return false;
        if (scaleArcsecPerPx is double s && s > 0) {
            double fieldArcsec = fieldShiftPx * s;
            return mountMoveArcsec > 5 * fieldArcsec + 120;
        }
        return fieldShiftPx < MaxFieldShiftPxWithoutScale && mountMoveArcsec >= 1.5 * MinJumpArcsec;
    }

    private double? PixelScaleArcsec() {
        double fl = _profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0;
        double px = _equip.Camera?.PixelSizeX ?? 0;
        int bin = Math.Max(1, _equip.Camera?.BinX ?? 1);
        return fl > 0 && px > 0 ? px * bin * 206.265 / fl : null;
    }

    /// <summary>Angular distance between two RA (hours) / Dec (degrees)
    /// positions, arcsec.</summary>
    internal static double SeparationArcsec(double ra1H, double dec1D, double ra2H, double dec2D) {
        const double D2R = Math.PI / 180;
        double a1 = ra1H * 15 * D2R, a2 = ra2H * 15 * D2R, d1 = dec1D * D2R, d2 = dec2D * D2R;
        double c = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(a1 - a2);
        return Math.Acos(Math.Clamp(c, -1, 1)) / D2R * 3600;
    }
}
