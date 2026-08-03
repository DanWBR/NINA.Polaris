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

using System.Collections.Concurrent;
using CosineKitty;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// "Center on Sun / Moon / planet" orchestrator — the <b>solve-near-and-offset</b>
/// strategy (Mode A) for solar-system objects that plate solving can't handle
/// directly.
///
/// <para>
/// Plate solving is star-pattern matching: the Moon/Sun wash the field out and a
/// planet shot has no background stars (tiny FOV, ms exposures), so a solve over
/// the object itself always fails. This service sidesteps that: it never tries to
/// solve the object. Instead it
/// </para>
///
/// <list type="number">
///   <item>computes the object's <b>topocentric apparent</b> position from the
///     <c>CosineKitty</c> ephemeris (J2000 frame, to match the catalog/solver
///     epoch the rest of the slew-and-center pipeline already uses);</item>
///   <item>slews a few degrees off to a <b>nearby star field</b> and runs the
///     normal <see cref="SlewCenterService"/> there — that solve + sync corrects
///     the mount's pointing model right next to the target;</item>
///   <item>recomputes the ephemeris (the Moon moves ~0.5°/h, so re-snap right
///     before the final hop) and does a precise relative GoTo onto the object.</item>
/// </list>
///
/// <para>
/// Because the model was just corrected next to the target, the final GoTo lands
/// the object in the frame without ever solving it. For the Moon/Sun it then
/// engages lunar/solar tracking (when the mount supports it) so the object stays
/// centred. Job-shaped like <see cref="SlewCenterService"/>: <see cref="StartJob"/>
/// returns immediately, the UI polls status, <see cref="CancelJob"/> aborts.
/// </para>
/// </summary>
public class SolarSystemCenterService {
    private readonly EquipmentManager _equip;
    private readonly SlewCenterService _slewCenter;
    private readonly ProfileService _profiles;
    private readonly ILogger<SolarSystemCenterService> _logger;

    private readonly ConcurrentDictionary<string, SolarSystemCenterJob> _jobs = new();

    public SolarSystemCenterService(EquipmentManager equip, SlewCenterService slewCenter,
            ProfileService profiles, ILogger<SolarSystemCenterService> logger) {
        _equip = equip;
        _slewCenter = slewCenter;
        _profiles = profiles;
        _logger = logger;
    }

    /// <summary>The solar-system bodies we can center on. Sun is included but the
    /// UI must warn about a solar filter; Pluto is excluded (not all ephemeris
    /// branches converge, and it's never imaged with a GoTo-center workflow).</summary>
    public static readonly IReadOnlyList<string> SupportedBodies = new[] {
        "Sun", "Moon", "Mercury", "Venus", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune"
    };

    public SolarSystemCenterJob StartJob(string body, double offsetDeg = 4.0,
            double toleranceArcsec = 30.0) {
        var job = new SolarSystemCenterJob {
            Id = Guid.NewGuid().ToString("N"),
            Body = body,
            OffsetDeg = offsetDeg <= 0 ? 4.0 : Math.Clamp(offsetDeg, 1.0, 15.0),
            ToleranceArcsec = toleranceArcsec,
            State = SolarSystemCenterState.Pending,
            CreatedAt = DateTime.UtcNow
        };
        _jobs[job.Id] = job;
        JobRetention.TrimFinished(_jobs, j => j.CreatedAt,
            j => j.State is SolarSystemCenterState.Done or SolarSystemCenterState.Failed
                         or SolarSystemCenterState.Cancelled);
        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunJobAsync(job, job.Cts.Token));
        return job;
    }

    public SolarSystemCenterJob? GetJob(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public void CancelJob(string jobId) {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.Cts?.Cancel();
        job.State = SolarSystemCenterState.Cancelled;
        // Cancel the inner solve-and-center pass (which also aborts the slew),
        // then best-effort abort any final-hop slew that's already in flight.
        if (!string.IsNullOrEmpty(job.InnerJobId)) {
            try { _slewCenter.CancelJob(job.InnerJobId!); } catch { }
        }
        try { _equip.Telescope?.AbortSlewAsync(); } catch (Exception ex) {
            _logger.LogWarning(ex, "AbortSlew during solar-system center cancel failed");
        }
    }

    private async Task RunJobAsync(SolarSystemCenterJob job, CancellationToken ct) {
        try {
            if (!TryParseBody(job.Body, out var body)) {
                Fail(job, $"Unknown body '{job.Body}'");
                return;
            }
            var tel = _equip.Telescope;
            if (tel == null || !tel.IsConnected) {
                Fail(job, "No telescope connected");
                return;
            }

            var p = _profiles.Active;
            var observer = new Observer(p.Latitude, p.Longitude, p.Altitude);

            // Phase 1: ephemeris snapshot (used to derive the offset field).
            job.State = SolarSystemCenterState.ComputingEphemeris;
            var t0 = ComputeApparent(body, observer, DateTime.UtcNow);
            job.TargetRa = t0.ra;
            job.TargetDec = t0.dec;
            var (offRa, offDec) = ComputeOffsetField(t0.ra, t0.dec, job.OffsetDeg);
            job.OffsetRa = offRa;
            job.OffsetDec = offDec;
            _logger.LogInformation(
                "Center on {Body}: ephemeris RA={Ra:F4}h Dec={Dec:F3}°, offset field RA={ORa:F4}h Dec={ODec:F3}° ({Off}° toward equator)",
                job.Body, t0.ra, t0.dec, offRa, offDec, job.OffsetDeg);

            // Phase 2: solve + sync at the nearby star field (corrects the model).
            job.State = SolarSystemCenterState.CorrectingModel;
            var inner = _slewCenter.StartJob(offRa, offDec, job.ToleranceArcsec);
            job.InnerJobId = inner.Id;
            try { if (inner.Task != null) await inner.Task; } catch { /* state inspected below */ }
            ct.ThrowIfCancellationRequested();

            job.SolveErrorArcsec = inner.ErrorArcsec;
            if (inner.State != SlewCenterState.Centered) {
                Fail(job, "Could not center the offset star field, so the model "
                    + "wasn't corrected: " + (inner.Error ?? inner.State.ToString())
                    + ". Try a clearer patch of sky (raise the offset) or check the solver.");
                return;
            }

            // Phase 3: re-snap the ephemeris (the object moved during the solve)
            // and do the precise relative GoTo onto it.
            job.State = SolarSystemCenterState.SlewingToTarget;
            var t1 = ComputeApparent(body, observer, DateTime.UtcNow);
            job.TargetRa = t1.ra;
            job.TargetDec = t1.dec;
            _logger.LogInformation("Slewing onto {Body}: RA={Ra:F4}h Dec={Dec:F3}°",
                job.Body, t1.ra, t1.dec);
            await tel.SlewAsync(t1.ra, t1.dec, ct);
            await WaitForSlewComplete(ct);
            ct.ThrowIfCancellationRequested();

            // Phase 4: engage the right tracking rate so the object stays put.
            // Sidereal lets the Moon/Sun drift out of frame within minutes;
            // planets are slow enough that sidereal is fine for an imaging run.
            try {
                if (tel.Capabilities.SupportsTrackingModes) {
                    if (body == Body.Moon) {
                        await tel.SetTrackingModeAsync(TrackingMode.Lunar, ct);
                        job.TrackingMode = "lunar";
                    } else if (body == Body.Sun) {
                        await tel.SetTrackingModeAsync(TrackingMode.Solar, ct);
                        job.TrackingMode = "solar";
                    }
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Could not set {Body} tracking rate (continuing)", job.Body);
            }

            job.State = SolarSystemCenterState.Done;
            _logger.LogInformation("Center on {Body} complete", job.Body);
        } catch (OperationCanceledException) {
            job.State = SolarSystemCenterState.Cancelled;
            _logger.LogInformation("Center on {Body} cancelled", job.Body);
        } catch (Exception ex) {
            Fail(job, ex.Message);
            _logger.LogError(ex, "Center on {Body} failed", job.Body);
        }
    }

    private void Fail(SolarSystemCenterJob job, string error) {
        job.Error = error;
        job.State = SolarSystemCenterState.Failed;
    }

    /// <summary>Topocentric apparent position of <paramref name="body"/> at
    /// <paramref name="utc"/> in the <b>J2000</b> frame (aberration corrected).
    /// J2000 to match the catalog/solver epoch the slew-and-center pipeline runs
    /// in, so the offset solve is self-consistent and the final GoTo coordinates
    /// line up with what a plate solve would report.</summary>
    private static (double ra, double dec) ComputeApparent(Body body, Observer observer, DateTime utc) {
        var time = new AstroTime(utc);
        var eq = Astronomy.Equator(body, time, observer, EquatorEpoch.J2000, Aberration.Corrected);
        return (eq.ra, eq.dec);
    }

    /// <summary>Pick the nearby star field to solve: keep RA, push Dec by
    /// <paramref name="offsetDeg"/> toward the celestial equator (higher star
    /// density, lower airmass), clamped to a slewable declination. Pure for unit
    /// testing.</summary>
    public static (double ra, double dec) ComputeOffsetField(double raHours, double decDeg, double offsetDeg) {
        double newDec;
        if (Math.Abs(decDeg) < 0.001) {
            newDec = offsetDeg; // on the equator: nudge north arbitrarily
        } else {
            newDec = decDeg - Math.Sign(decDeg) * offsetDeg;
        }
        newDec = Math.Clamp(newDec, -89.0, 89.0);
        return (raHours, newDec);
    }

    /// <summary>Map a friendly body name (case-insensitive) to a CosineKitty
    /// <see cref="Body"/>. Pure for unit testing.</summary>
    public static bool TryParseBody(string? name, out Body body) {
        body = Body.Invalid;
        if (string.IsNullOrWhiteSpace(name)) return false;
        switch (name.Trim().ToLowerInvariant()) {
            case "sun": body = Body.Sun; return true;
            case "moon": body = Body.Moon; return true;
            case "mercury": body = Body.Mercury; return true;
            case "venus": body = Body.Venus; return true;
            case "mars": body = Body.Mars; return true;
            case "jupiter": body = Body.Jupiter; return true;
            case "saturn": body = Body.Saturn; return true;
            case "uranus": body = Body.Uranus; return true;
            case "neptune": body = Body.Neptune; return true;
            default: return false;
        }
    }

    private async Task WaitForSlewComplete(CancellationToken ct) {
        if (_equip.Telescope == null) return;
        for (int i = 0; i < 300; i++) {
            ct.ThrowIfCancellationRequested();
            if (!_equip.Telescope.IsSlewing) return;
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Slew did not complete within 5 minutes");
    }
}

public class SolarSystemCenterJob {
    public string Id { get; set; } = "";
    public string Body { get; set; } = "";
    public double OffsetDeg { get; set; }
    public double ToleranceArcsec { get; set; }
    public SolarSystemCenterState State { get; set; }
    /// <summary>Object's apparent position (J2000) — updated to the fresh snapshot
    /// right before the final slew.</summary>
    public double TargetRa { get; set; }
    public double TargetDec { get; set; }
    /// <summary>The nearby star field that gets solved to correct the model.</summary>
    public double OffsetRa { get; set; }
    public double OffsetDec { get; set; }
    /// <summary>Residual pointing error (arcsec) from the offset-field solve.</summary>
    public double? SolveErrorArcsec { get; set; }
    /// <summary>"lunar" / "solar" when a non-sidereal rate was engaged, else null.</summary>
    public string? TrackingMode { get; set; }
    public string? InnerJobId { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }

    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Task { get; set; }
}

public enum SolarSystemCenterState {
    Pending,
    ComputingEphemeris,
    CorrectingModel,
    SlewingToTarget,
    Done,
    Failed,
    Cancelled
}
