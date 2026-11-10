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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Turns tracking off and on again when a guided mount keeps falling behind
/// the sky while its driver still reports tracking.
///
/// <para>SV503 rig, ZWO AM3, 2026-11-09: after the meridian flip the RA axis
/// lost 60 to 80" every 15 to 160 s while the firmware and the driver both
/// said it was tracking. The guider caught each loss with full-length pulses.
/// Turning tracking off and on brought it back for a while; on ASIAIR the
/// mount behaved the same.</para>
///
/// <para>The mount's reported RA shows it. Guide pulses move the axis and the
/// reported RA follows them, but the losses do not show. A mount that tracks
/// properly needs guide corrections in both directions, so its reported RA of
/// date stays put within a few seconds of time. On the AM3 it walked 14 s of
/// time in 3 minutes, all one way: the corrections that made up for the
/// losses. So while guiding, a reported RA that moves steadily more than
/// <see cref="MinDriftSeconds"/> of time across about two minutes means the
/// mount is not doing what it says.</para>
/// </summary>
public sealed class MountStallWatchService : BackgroundService {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    /// <summary>Span the drift is judged over.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(120);
    /// <summary>Shortest span that can decide.</summary>
    internal static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(90);
    /// <summary>RA drift (seconds of time) that counts as falling behind. 6 s
    /// is 90" at the equator. The AM3's periodic error needs about 3 s of
    /// correction in two minutes at most; the fault gave 9 to 12.</summary>
    internal const double MinDriftSeconds = 6.0;
    /// <summary>Guide pulses at 1x sidereal move the axis 1 s of time per
    /// second; a bigger step between two samples is a sync or a jog, and the
    /// window starts again.</summary>
    internal const double MaxGuideRateSecondsPerSecond = 1.0;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NotifyEvery = TimeSpan.FromMinutes(15);

    private readonly EquipmentManager _equip;
    private readonly ActiveGuiderProvider _guiders;
    private readonly MeridianFlipService _meridian;
    private readonly NotificationService _notify;
    private readonly ILogger<MountStallWatchService> _logger;
    private readonly bool _enabled;
    private readonly List<(DateTime AtUtc, double RaHours)> _samples = new();
    private DateTime _cooldownUntil = DateTime.MinValue;
    private DateTime _lastNotifyUtc = DateTime.MinValue;
    private int _reengagedSinceNotify;

    /// <summary>True while this service has tracking off on purpose, so the
    /// safety guard does not report it as an unexpected stop.</summary>
    public bool Reengaging { get; private set; }
    public int ReengageCount { get; private set; }

    public MountStallWatchService(EquipmentManager equip, ActiveGuiderProvider guiders,
            MeridianFlipService meridian, NotificationService notify, IConfiguration config,
            ILogger<MountStallWatchService> logger) {
        _equip = equip;
        _guiders = guiders;
        _meridian = meridian;
        _notify = notify;
        _logger = logger;
        _enabled = config.GetValue("MountStallWatch:Enabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!_enabled) return;
        while (!stoppingToken.IsCancellationRequested) {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Mount stall watch tick failed (non-fatal)"); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct) {
        var scope = _equip.Telescope;
        var g = _guiders.Active;
        bool watch = scope is { IsConnected: true } && scope.Capabilities.SupportsTrackingToggle
            && scope.IsTracking && !scope.IsSlewing && !SafeParked(scope)
            && _meridian.State == MeridianFlipState.Idle
            && g.IsConnected && g.IsGuiding && !g.IsPaused && !g.IsCalibrating
            && DateTime.UtcNow >= _cooldownUntil;
        if (!watch) { _samples.Clear(); return; }

        var (raHours, _) = scope!.PointingOfDate();
        if (double.IsNaN(raHours)) { _samples.Clear(); return; }

        var now = DateTime.UtcNow;
        Add(_samples, now, raHours);
        var drift = DriftSeconds(_samples);
        if (drift == null || Math.Abs(drift.Value) < MinDriftSeconds) return;

        _samples.Clear();
        _cooldownUntil = now + Cooldown;
        await ReengageAsync(scope, drift.Value, ct);
    }

    private static bool SafeParked(ITelescope scope) {
        try { return scope.IsParked; } catch { return false; }
    }

    /// <summary>Append a sample, dropping the window when the step from the
    /// last one is faster than guiding can move the axis (a sync or a jog),
    /// and trimming samples older than <see cref="Window"/>.</summary>
    internal static void Add(List<(DateTime AtUtc, double RaHours)> samples, DateTime at, double raHours) {
        if (samples.Count > 0) {
            var last = samples[^1];
            double dt = (at - last.AtUtc).TotalSeconds;
            double step = Math.Abs(RaDeltaSeconds(last.RaHours, raHours));
            // 1 s of slack for the 1 s resolution of the reported RA.
            if (dt <= 0 || step > 1.0 + MaxGuideRateSecondsPerSecond * dt) samples.Clear();
        }
        samples.Add((at, raHours));
        while (samples.Count > 0 && at - samples[0].AtUtc > Window) samples.RemoveAt(0);
    }

    /// <summary>Net RA change across the window in seconds of time, or null
    /// when the samples do not span <see cref="MinSpan"/> yet.</summary>
    internal static double? DriftSeconds(IReadOnlyList<(DateTime AtUtc, double RaHours)> samples) {
        if (samples.Count < 2) return null;
        if (samples[^1].AtUtc - samples[0].AtUtc < MinSpan) return null;
        return RaDeltaSeconds(samples[0].RaHours, samples[^1].RaHours);
    }

    internal static double RaDeltaSeconds(double fromHours, double toHours) {
        double d = toHours - fromHours;
        while (d > 12) d -= 24;
        while (d < -12) d += 24;
        return d * 3600.0;
    }

    private async Task ReengageAsync(ITelescope scope, double driftSeconds, CancellationToken ct) {
        ReengageCount++;
        _logger.LogWarning(
            "MOUNT FALLING BEHIND while reporting tracking: reported RA moved {Drift:+0.0;-0.0} s of time in about {Window:F0} s "
            + "of guiding. Turning tracking off and on (re-engagement #{Count}).",
            driftSeconds, Window.TotalSeconds, ReengageCount);
        Reengaging = true;
        try {
            await scope.SetTrackingAsync(false, ct);
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            try { await scope.SetTrackingAsync(true, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                _logger.LogWarning(ex, "Mount stall watch: tracking on failed, retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                await scope.SetTrackingAsync(true, ct);
            }
            // Give the driver a poll to echo the new state before the guard looks.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        } finally {
            Reengaging = false;
        }

        _reengagedSinceNotify++;
        var now = DateTime.UtcNow;
        if (now - _lastNotifyUtc < NotifyEvery) return;
        _notify.Push("warn",
            _reengagedSinceNotify == 1
                ? "The mount was falling behind the sky while reporting tracking. Polaris turned tracking off and on again."
                : $"The mount fell behind the sky {_reengagedSinceNotify} times while reporting tracking. "
                  + "Polaris turned tracking off and on again each time.",
            30000);
        _lastNotifyUtc = now;
        _reengagedSinceNotify = 0;
    }
}
