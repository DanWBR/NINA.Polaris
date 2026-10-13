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
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Endpoints;

/// <summary>HTTP surface for the on-demand native camera-SDK pack downloader,
/// surfaced in the Software Updates card. Mirrors the ncnn model-pack endpoints.</summary>
public static class CameraSdkPackEndpoints {
    public static void MapCameraSdkPackEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/camera/sdk-pack");

        g.MapGet("/status", (CameraSdkPackService pack) => Results.Ok(pack.GetStatus()));

        g.MapPost("/download", (CameraSdkPackService pack) => {
            var started = pack.Start();
            return started
                ? Results.Accepted(value: pack.GetStatus())
                : Results.Conflict(new { error = "A camera SDK pack download is already running" });
        });

        g.MapPost("/cancel", (CameraSdkPackService pack) => {
            pack.Cancel();
            return Results.Ok(new { ok = true });
        });

        // Canon EDSDK from what the operator downloaded from Canon (the zip, or
        // EDSDK.dll + EdsImage.dll out of its EDSDK_64\Dll folder), into the
        // writable SDK folder the Canon backend searches. Canon's licence keeps
        // EDSDK out of the camera-SDK pack, hence an upload instead of a download.
        app.MapPost("/api/camera/canon-edsdk/install", async (HttpRequest http, ProfileService profiles,
                                                          CancellationToken ct) => {
            if (!OperatingSystem.IsWindows())
                return Results.BadRequest(new { error = "The Canon EDSDK runs on Windows only. On Linux, Canon cameras work through INDI (gphoto)." });
            if (!http.HasFormContentType)
                return Results.BadRequest(new { error = "Multipart form expected." });
            var form = await http.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file uploaded." });

            var uploads = new List<(string, byte[])>();
            foreach (var f in form.Files) {
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct);
                uploads.Add((f.FileName, ms.ToArray()));
            }
            var dest = NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.InstallDir
                       ?? Path.Combine(CameraSdkPackService.PackDir(profiles.DataDir),
                                       NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.PackFolder);
            var result = CanonEdsdkInstaller.Install(uploads, dest);
            if (!result.Ok) return Results.BadRequest(new { error = result.Message });

            // Look again now, so the driver list turns available without a restart.
            NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.ResetProbe();
            var available = NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.IsAvailable;
            return Results.Ok(new {
                ok = true, available, installedTo = dest,
                loadedFrom = NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.LoadedFrom,
                message = available
                    ? result.Message
                    : result.Message + " The SDK did not initialise; restart Polaris and check the log."
            });
        }).DisableAntiforgery();
    }
}
