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

namespace NINA.Polaris.Services.Sequencer.Containers;

/// <summary>
/// A sequential container scoped to a specific deep-sky target. Slews and
/// plate-solve-centers on the target before running the children, so the
/// children can take exposures, do filter changes, etc. without re-pointing.
///
/// Target rotation (PA) is record-keeping only today, when a rotator is
/// added in a later release the container will rotate to <see cref="Rotation"/>
/// after centering.
/// </summary>
public class DeepSkyObjectContainer : SequenceContainer {
    public override string Type => "DeepSkyObject";

    /// <summary>Target display name (free text, "M31", "NGC 7000 west panel").</summary>
    public string Target { get; set; } = "";

    /// <summary>J2000 right ascension in decimal hours.</summary>
    public double RaHours { get; set; }

    /// <summary>J2000 declination in decimal degrees.</summary>
    public double DecDeg { get; set; }

    /// <summary>Target rotation angle (PA) in degrees. 0 = north up.</summary>
    public double Rotation { get; set; }

    /// <summary>
    /// If true the container performs Slew &amp; Center via plate-solving
    /// before running children. If false it assumes the mount is already
    /// pointed at the target (useful for re-runs after a flip).
    /// </summary>
    public bool CenterOnStart { get; set; } = true;

    public override IReadOnlyList<string> Validate() {
        var errors = new List<string>(base.Validate());
        if (string.IsNullOrWhiteSpace(Target))
            errors.Add("Target name is empty");
        if (RaHours < 0 || RaHours >= 24)
            errors.Add($"RA hours out of range: {RaHours}");
        if (DecDeg < -90 || DecDeg > 90)
            errors.Add($"Dec out of range: {DecDeg}");
        return errors;
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (CenterOnStart) {
            ctx.Logger.LogInformation("DSO container '{Target}': Slew & Center → RA={Ra}h Dec={Dec}°",
                Target, RaHours, DecDeg);
            var job = ctx.SlewCenter.StartJob(RaHours, DecDeg);
            while (true) {
                ct.ThrowIfCancellationRequested();
                var status = ctx.SlewCenter.GetJob(job.Id);
                if (status == null) throw new InvalidOperationException("Slew & Center job vanished");
                if (status.State == SlewCenterState.Centered) break;
                if (status.State == SlewCenterState.Failed)
                    throw new InvalidOperationException($"Slew & Center failed: {status.Error}");
                if (status.State == SlewCenterState.Cancelled)
                    throw new OperationCanceledException("Slew & Center cancelled");
                await Task.Delay(500, ct);
            }
        }

        // From here on it behaves exactly like a sequential container: the
        // shared helper handles triggers (including ancestor cascade), per-step
        // retry + error policy, and the IsLoop + conditions loop.
        await RunChildrenSequentialAsync(ctx, ct);
    }
}