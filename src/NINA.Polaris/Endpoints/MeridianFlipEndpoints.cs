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

using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class MeridianFlipEndpoints {
    public static void MapMeridianFlipEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/meridianflip");

        group.MapGet("/settings", (MeridianFlipService mf) => Results.Ok(mf.Settings));

        group.MapPut("/settings", (MeridianFlipSettings settings, MeridianFlipService mf) => {
            mf.UpdateSettings(settings);
            return Results.Ok(mf.Settings);
        });

        group.MapGet("/status", (MeridianFlipService mf, EquipmentManager equip, ProfileService profile) => {
            double? timeToMeridianHours = null;
            double? hourAngle = null;
            double? lst = null;
            double? timeToFlipHours = null;

            if (equip.Telescope != null && equip.Telescope.IsConnected) {
                var raHours = equip.Telescope.RightAscension;
                lst = MeridianFlipService.ComputeLstHours(DateTime.UtcNow, profile.Active.Longitude);
                hourAngle = lst.Value - raHours;
                while (hourAngle > 12) hourAngle -= 24;
                while (hourAngle < -12) hourAngle += 24;
                timeToMeridianHours = MeridianFlipService.HoursUntilMeridian(raHours, DateTime.UtcNow, profile.Active.Longitude);
                timeToFlipHours = MeridianFlipService.HoursUntilFlip(
                    raHours, DateTime.UtcNow, profile.Active.Longitude, mf.Settings.MinutesAfterMeridian);
            }

            return Results.Ok(new {
                state = mf.State.ToString().ToLowerInvariant(),
                settings = mf.Settings,
                flipsCompleted = mf.FlipsCompleted,
                lastFlipAt = mf.LastFlipAt,
                lastFlipError = mf.LastFlipError,
                lstHours = lst,
                hourAngleHours = hourAngle,
                timeToMeridianHours = timeToMeridianHours,
                timeToMeridianMinutes = timeToMeridianHours.HasValue ? timeToMeridianHours * 60 : null,
                timeToFlipHours = timeToFlipHours,
                timeToFlipMinutes = timeToFlipHours.HasValue ? timeToFlipHours * 60 : null
            });
        });

        group.MapPost("/trigger", async (TriggerRequest request, MeridianFlipService mf) => {
            if (mf.State != MeridianFlipState.Idle)
                return Results.Conflict(new { error = $"Flip already in progress (state={mf.State})" });

            var ok = await mf.ExecuteFlipAsync(request.Ra, request.Dec);
            return Results.Ok(new { success = ok, state = mf.State.ToString().ToLowerInvariant() });
        });

        // One-click "flip now" from the LIVE stacking panel. The mount is
        // already pointing at the target, so we read its current RA/Dec and
        // re-slew to the same coordinates -- the mount firmware flips when
        // the re-slew crosses its meridian limit, then we recenter on the
        // same spot. No coordinates from the client, nothing to type.
        group.MapPost("/trigger-current", async (MeridianFlipService mf, EquipmentManager equip) => {
            if (equip.Telescope == null || !equip.Telescope.IsConnected)
                return Results.BadRequest(new { error = "No mount connected" });
            if (mf.State != MeridianFlipState.Idle)
                return Results.Conflict(new { error = $"Flip already in progress (state={mf.State})" });

            var ra = equip.Telescope.RightAscension;
            var dec = equip.Telescope.Declination;
            var ok = await mf.ExecuteFlipAsync(ra, dec);
            return Results.Ok(new {
                success = ok,
                state = mf.State.ToString().ToLowerInvariant(),
                ra, dec,
                error = mf.LastFlipError
            });
        });

        group.MapPost("/abort", (MeridianFlipService mf) => {
            mf.Abort();
            return Results.Ok(new { state = mf.State.ToString().ToLowerInvariant() });
        });
    }

    public record TriggerRequest(double Ra, double Dec);
}