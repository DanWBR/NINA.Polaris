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

using NINA.Polaris.Services.Broadcast;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// The live broadcast: its configuration, and the two buttons.
///
/// <para>Nothing here starts on its own and nothing resumes across a restart.
/// A broadcast puts the operator's sky, their equipment list and their session
/// on the public internet, so it begins when somebody presses start and at no
/// other moment.</para>
/// </summary>
public static class BroadcastEndpoints {

    public static void MapBroadcastEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/broadcast");

        // The key is never in the response: GetPublic carries hasStreamKey.
        g.MapGet("/config", (BroadcastConfigService cfg) => Results.Ok(cfg.GetPublic()));

        g.MapPut("/config", (BroadcastConfigUpdate req, BroadcastConfigService cfg) => {
            try {
                cfg.Update(req);
                return Results.Ok(cfg.GetPublic());
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapGet("/status", (BroadcastService svc) => Results.Ok(svc.GetStatus()));

        g.MapPost("/start", async (BroadcastService svc) => {
            var reason = await svc.StartAsync();
            return reason == null
                ? Results.Ok(svc.GetStatus())
                : Results.BadRequest(new { error = reason });
        });

        g.MapPost("/stop", async (BroadcastService svc) => {
            await svc.StopAsync();
            return Results.Ok(svc.GetStatus());
        });

        /// Where ffmpeg was looked for and what it can do. Drives the "not
        /// installed" panel, which lists the paths so an operator with ffmpeg
        /// somewhere unusual can see why it was missed, the way the Siril and
        /// ASTAP cards do.
        g.MapGet("/encoders", async (FfmpegService ffmpeg) => {
            var caps = await ffmpeg.GetCapabilitiesAsync();
            var encoders = FfmpegEncoders.ParseEncoders(caps?.EncodersOutput);
            var usable = FfmpegEncoders.DropUnusable(encoders, File.Exists);
            var isArm = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                is System.Runtime.InteropServices.Architecture.Arm64
                or System.Runtime.InteropServices.Architecture.Arm;
            return Results.Ok(new {
                available = ffmpeg.IsAvailable,
                path = ffmpeg.BinaryPath,
                version = caps?.Version,
                searched = ffmpeg.EnumerateBinaryCandidates()
                    .Select(c => new { c.Description, c.Path, c.Exists }),
                chosen = FfmpegEncoders.Choose(usable, isArm),
                hardware = FfmpegEncoders.IsHardware(FfmpegEncoders.Choose(usable, isArm)),
                usable = usable.Where(e => e.Contains("264", StringComparison.Ordinal)).OrderBy(e => e),
                hasDrawText = FfmpegEncoders.HasFilter(caps?.FiltersOutput, "drawtext"),
                hasOverlay = FfmpegEncoders.HasFilter(caps?.FiltersOutput, "overlay")
            });
        });

        // A freshly installed ffmpeg should not need a restart to be found.
        g.MapPost("/rescan", (FfmpegService ffmpeg) => {
            ffmpeg.Invalidate();
            return Results.Ok(new { available = ffmpeg.IsAvailable, path = ffmpeg.BinaryPath });
        });
    }
}
