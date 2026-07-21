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

/// <summary>Endpoints for the "USB drive detected" prompt (see
/// <see cref="UsbDriveWatcherService"/>): accept it as the capture home, or
/// decline. Both clear the pending state so the drive is not offered again.</summary>
public static class UsbEndpoints {
    public record UsbPathRequest(string Path);

    public static void MapUsbEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/usb");

        // Accept: point the capture home (ImageOutputDir) at the drive and stop
        // offering it. Path is validated the same way as the Studio-root setter.
        g.MapPost("/use", (UsbPathRequest req, FileBrowserService svc,
                           ProfileService profiles, UsbDriveWatcherService watcher) => {
            try {
                var full = svc.ResolveSafe(req.Path, mustExist: true);
                if (!Directory.Exists(full))
                    return Results.BadRequest(new { error = "Path is not a directory" });
                profiles.Active.ImageOutputDir = full;
                profiles.Save();
                watcher.Dismiss(req.Path);
                return Results.Ok(new { ok = true, imageOutputDir = full });
            } catch (UnauthorizedAccessException uae) {
                return Results.Json(new { error = uae.Message }, statusCode: StatusCodes.Status403Forbidden);
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Decline: keep the current home, stop offering this drive.
        g.MapPost("/dismiss", (UsbPathRequest req, UsbDriveWatcherService watcher) => {
            watcher.Dismiss(req.Path);
            return Results.Ok(new { ok = true });
        });

        // Revert: the drive holding the capture home was unplugged; move the home
        // back to the default folder (home/files), creating it if it is missing.
        g.MapPost("/revert", (ProfileService profiles, UsbDriveWatcherService watcher) => {
            try {
                var def = UsbDriveWatcherService.DefaultImageDir();
                if (string.IsNullOrEmpty(def)) return Results.BadRequest(new { error = "no default home" });
                Directory.CreateDirectory(def);
                profiles.Active.ImageOutputDir = def;
                profiles.Save();
                watcher.ClearRevert();
                return Results.Ok(new { ok = true, imageOutputDir = def });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Keep the (now dead) home on the removed drive; just stop asking.
        g.MapPost("/revert-dismiss", (UsbDriveWatcherService watcher) => {
            watcher.ClearRevert();
            return Results.Ok(new { ok = true });
        });
    }
}
