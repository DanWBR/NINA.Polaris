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
using NINA.Polaris.Services.Focus;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// FOCUS tab's Manual Assist subtab endpoints. Currently only
/// hosts the Bahtinov mask analyser; future sub-features (donut
/// metric, FWHM gaussian fit) land here too.
/// </summary>
public static class FocusEndpoints {
    public static void MapFocusEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/focus");

        // POST /api/focus/bahtinov [{ starX, starY }]
        // Analyses the last frame ImageRelayService cached. The
        // client-side Manual Assist loop ensures a fresh capture
        // lands first; piggybacking on the cache avoids forcing a
        // second exposure per tick (which would halve fps).
        group.MapPost("/bahtinov", (BahtinovRequest? req, ImageRelayService relay) => {
            var img = relay.LatestImage;
            if (img == null) {
                return Results.Json(new {
                    ok = false,
                    error = "no recent frame; trigger a capture first via Start loop or Snap once"
                });
            }
            var pixels = img.PixelData.ToArray();
            var result = BahtinovAnalyzer.Analyze(
                pixels, img.Width, img.Height,
                starX: req?.StarX, starY: req?.StarY);
            return Results.Ok(result);
        });
    }

    public record BahtinovRequest(int? StarX, int? StarY);
}