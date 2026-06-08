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

using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Read-only optical diagnostics for a single FITS frame, used by the
/// STUDIO "Analyze" tool (Tilt + Aberration). Synchronous: one frame
/// detects + analyses in ~1-2 s on a Pi 5, so there's no async-job dance
/// like GraXpert. Nothing is written to disk, so no FrameLibrary rescan.
/// </summary>
public static class AnalysisEndpoints {
    public static void MapAnalysisEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/analysis");

        g.MapPost("/frame", (FrameAnalysisService svc, AnalysisRequest req) => {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            try {
                return Results.Ok(svc.Analyze(req.Path));
            } catch (FileNotFoundException) {
                return Results.NotFound(new { error = "File not found" });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    public record AnalysisRequest(string Path);
}