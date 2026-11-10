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
/// Warns when the stacked field drifts steadily while guiding is on: the
/// guider is holding something that does not move with the sky.
///
/// <para>SV503 rig, 2026-11-09: 60 s frames, the field walked 10 to 20 px a
/// minute along RA and every star came out a 7 to 8 px streak, while the
/// guide log showed the star held within 0.2 px. The guider had picked 5
/// "stars" on a guide frame that showed 407 an hour earlier, and was pushing
/// the mount steadily against an error that never went away. Nothing in the
/// guiding numbers gave it away; the live stack's alignment did.</para>
///
/// <para>Each stacked frame reports where it sat against the reference. A
/// guided field should stay put between dithers, so a steady motion in one
/// direction over several frames means the guider is not following the sky
/// (a guide star that is a hot pixel or a reflection, a dewed guide scope
/// leaving only a few such points, a guide scope that is not rigid with the
/// main one). Dithers are single jumps in random directions, so the drift is
/// read from the median frame-to-frame velocity and only counted when most
/// frame pairs agree with it.</para>
/// </summary>
public sealed class GuideDriftWatchService : IHostedService {
    /// <summary>Frame pairs the drift is judged over.</summary>
    internal const int Window = 6;
    /// <summary>Pairs that must move with the median velocity.</summary>
    internal const int MinAgreeing = 4;
    /// <summary>Drift that counts, arcsec per minute (when the scale is known).</summary>
    internal const double MinDriftArcsecPerMin = 3.0;
    /// <summary>Drift that counts, px per minute, when the scale is not known.</summary>
    internal const double MinDriftPxPerMinWithoutScale = 3.0;
    /// <summary>Pairs further apart than this are not consecutive exposures.</summary>
    private static readonly TimeSpan MaxPairGap = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(30);

    private readonly LiveStackingService _live;
    private readonly ActiveGuiderProvider _guiders;
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<GuideDriftWatchService> _logger;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();
    private DateTime _lastWarnUtc = DateTime.MinValue;

    /// <summary>One stacked frame: where it sat (px), when, and whether the
    /// guider was guiding (not paused, calibrating or settling) at that point.</summary>
    internal sealed record Sample(int Frame, double Tx, double Ty, bool Flipped, DateTime AtUtc, bool Guiding);

    /// <summary>A steady drift: median velocity in px/min along the frame's
    /// x and y, and how many pairs agreed with it.</summary>
    internal sealed record Drift(double VxPxPerMin, double VyPxPerMin, int Agreeing, int Pairs) {
        public double SpeedPxPerMin => Math.Sqrt(VxPxPerMin * VxPxPerMin + VyPxPerMin * VyPxPerMin);
    }

    public GuideDriftWatchService(LiveStackingService live, ActiveGuiderProvider guiders, EquipmentManager equip,
            ProfileService profiles, NotificationService notify, IConfiguration config,
            ILogger<GuideDriftWatchService> logger) {
        _live = live;
        _guiders = guiders;
        _equip = equip;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
        _enabled = config.GetValue("GuideDriftWatch:Enabled", true);
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
        try { Check(frame); }
        catch (Exception ex) { _logger.LogDebug(ex, "Guide drift watch failed"); }
    }

    private void Check(LiveFrameAlignment frame) {
        var g = _guiders.Active;
        bool guiding = g.IsConnected && g.IsGuiding && !g.IsPaused && !g.IsCalibrating;
        var now = new Sample(frame.FrameCount, frame.Tx, frame.Ty, frame.Flipped, frame.AtUtc, guiding);

        List<Sample> window;
        lock (_gate) {
            // A new stack starts the count again.
            if (_samples.Count > 0 && frame.FrameCount <= _samples[^1].Frame) _samples.Clear();
            _samples.Add(now);
            while (_samples.Count > Window + 1) _samples.RemoveAt(0);
            window = new List<Sample>(_samples);
        }

        var drift = Measure(window);
        if (drift == null) return;
        double? scale = PixelScaleArcsec();
        if (!IsSignificant(drift, scale)) return;
        if (DateTime.UtcNow - _lastWarnUtc < RepeatAfter) return;
        _lastWarnUtc = DateTime.UtcNow;

        var message = Describe(drift, scale, g.RmsTotal);
        _logger.LogWarning("Guide drift watch: {Message} (velocity x {Vx:F1} y {Vy:F1} px/min, {Agree}/{Pairs} frame pairs agree, frames {F0}-{F1})",
            message, drift.VxPxPerMin, drift.VyPxPerMin, drift.Agreeing, drift.Pairs, window[0].Frame, window[^1].Frame);
        _notify.Push("warn", message, 30000);
    }

    /// <summary>
    /// The steady drift across <paramref name="samples"/> (oldest first), or
    /// null when there are not enough guided, consecutive frame pairs to tell.
    /// Only pairs with both frames guided, on the same pier side and close in
    /// time count.
    /// </summary>
    internal static Drift? Measure(IReadOnlyList<Sample> samples) {
        var v = new List<(double X, double Y)>();
        for (int i = 1; i < samples.Count; i++) {
            var a = samples[i - 1];
            var b = samples[i];
            if (!a.Guiding || !b.Guiding || a.Flipped != b.Flipped) continue;
            var dt = b.AtUtc - a.AtUtc;
            if (dt <= TimeSpan.Zero || dt > MaxPairGap) continue;
            double min = dt.TotalMinutes;
            v.Add(((b.Tx - a.Tx) / min, (b.Ty - a.Ty) / min));
        }
        if (v.Count < MinAgreeing + 1) return null;

        double mx = Median(v.Select(p => p.X)), my = Median(v.Select(p => p.Y));
        double speed = Math.Sqrt(mx * mx + my * my);
        if (speed <= 0) return new Drift(mx, my, 0, v.Count);
        // A pair agrees when it moved at least half the median speed in the
        // median direction: a dither jump in another direction does not.
        int agreeing = v.Count(p => (p.X * mx + p.Y * my) / speed >= 0.5 * speed);
        return new Drift(mx, my, agreeing, v.Count);
    }

    internal static bool IsSignificant(Drift drift, double? scaleArcsecPerPx) {
        if (drift.Agreeing < MinAgreeing) return false;
        return scaleArcsecPerPx is double s && s > 0
            ? drift.SpeedPxPerMin * s >= MinDriftArcsecPerMin
            : drift.SpeedPxPerMin >= MinDriftPxPerMinWithoutScale;
    }

    internal static string Describe(Drift drift, double? scaleArcsecPerPx, double guideRmsArcsec) {
        string rate = scaleArcsecPerPx is double s && s > 0
            ? $"{drift.SpeedPxPerMin * s:0.#}\"/min"
            : $"{drift.SpeedPxPerMin:0.#} px/min";
        string axis = Math.Abs(drift.VxPxPerMin) >= 2 * Math.Abs(drift.VyPxPerMin) ? "along the camera's X axis"
            : Math.Abs(drift.VyPxPerMin) >= 2 * Math.Abs(drift.VxPxPerMin) ? "along the camera's Y axis"
            : "diagonally";
        string rms = guideRmsArcsec > 0 ? $" (guiding RMS {guideRmsArcsec:0.0#}\")" : "";
        return $"The image drifts {rate} {axis} while guiding is on{rms}, so stars streak: the guider is not following the sky. "
            + "Check the guide scope for dew or something in front of it, look at the guide image for a hot pixel or "
            + "reflection picked as a star (select another star), and check that the guide scope is rigid with the main one.";
    }

    private static double Median(IEnumerable<double> values) {
        var a = values.OrderBy(x => x).ToArray();
        int n = a.Length;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2;
    }

    private double? PixelScaleArcsec() {
        double fl = _profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0;
        double px = _equip.Camera?.PixelSizeX ?? 0;
        int bin = Math.Max(1, _equip.Camera?.BinX ?? 1);
        return fl > 0 && px > 0 ? px * bin * 206.265 / fl : null;
    }
}
