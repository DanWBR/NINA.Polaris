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

/// <summary>
/// The unified global dither configuration (<c>/api/dither</c>) — one source of
/// truth for LIVE, AUTORUN, ADV and the multi-camera barrier, replacing the old
/// per-mode dither controls. GET migrates the legacy per-mode settings on first
/// read and persists them; PUT stores the edited global config. A manual one-off
/// dither still lives at <c>POST /api/guider/dither</c>; live status is the
/// <c>ditherSync</c> WS block plus the AUTORUN counters.
/// </summary>
public static class DitherEndpoints {
    public static void MapDitherEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/dither");

        group.MapGet("/", (ProfileService profiles) => {
            var rig = profiles.ActiveEquipmentProfile;
            if (rig == null) return Results.Ok(new DitherSettings());
            var d = rig.EffectiveDither;
            // Persist the migration once so DitherProfile becomes the stored
            // source (EffectiveDither is otherwise recomputed every read).
            if (rig.DitherProfile == null)
                profiles.UpdateEquipmentProfile(rig.Id, r => r.DitherProfile = d);
            return Results.Ok(d);
        });

        group.MapPut("/", (ProfileService profiles, DitherSettings body) => {
            var rig = profiles.ActiveEquipmentProfile;
            if (rig == null) return Results.BadRequest(new { error = "No active rig" });
            var clean = new DitherSettings {
                Enabled = body.Enabled,
                Pixels = Math.Clamp(body.Pixels, 0.1, 100),
                EveryNFrames = Math.Max(1, body.EveryNFrames),
                RaOnly = body.RaOnly,
                SettlePixels = Math.Clamp(body.SettlePixels, 0.1, 100),
                SettleTime = Math.Max(0, body.SettleTime),
                SettleTimeout = Math.Max(1, body.SettleTimeout),
                CadenceStrategy = (body.CadenceStrategy ?? "slowest").Trim().ToLowerInvariant() switch {
                    "main" => "main",
                    "independent" => "independent",
                    _ => "slowest",
                },
            };
            profiles.UpdateEquipmentProfile(rig.Id, r => r.DitherProfile = clean);
            return Results.Ok(clean);
        });

        // Manual one-off dither, routed through the multi-camera barrier so it
        // waits for every active imaging camera to finish the sub it is currently
        // exposing before dithering the shared mount (never mid-sub). Replaces the
        // old direct hit on the guider for the "Dither now" button.
        group.MapPost("/now", async (DitherBarrier barrier) => {
            var dithered = await barrier.RequestManualDitherAsync();
            return dithered
                ? Results.Ok(new { dithered = true })
                : Results.Ok(new { dithered = false, reason = "guider not guiding or a dither is already in flight" });
        });
    }
}
