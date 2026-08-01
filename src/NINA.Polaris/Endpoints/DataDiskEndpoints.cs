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
/// STORAGE-1/2: the capture-disk surface. Separate from
/// <see cref="StorageEndpoints"/>, which owns the network-push settings under
/// the same /api/storage prefix: mapping that one twice is what made every
/// /api/storage/config request throw AmbiguousMatchException.
/// </summary>
public static class DataDiskEndpoints {
    public static void MapDataDiskEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/storage");

        // What is plugged in, what the captures are on, and what could be set
        // up. Read-only; the client polls it once per session.
        group.MapGet("/survey", (StorageSetupService storage) => {
            var s = storage.Look();
            return Results.Ok(new {
                supported             = s.Supported,
                reason                = s.Reason,
                captureRoot           = s.CaptureRoot,
                captureRootOnBootDisk = s.CaptureRootOnBootDisk,
                candidates = s.Candidates.Select(c => new {
                    device = c.Device, uuid = c.Uuid, fsType = c.FsType, label = c.Label,
                    sizeBytes = c.SizeBytes, model = c.Model, mountPoint = c.MountPoint,
                    removable = c.Removable
                }),
                formattable = s.Formattable.Select(d => new {
                    device = d.Device, model = d.Model,
                    // The identity the confirmation has to echo back. Falls back
                    // to the size for the USB bridges that report no serial.
                    identity = string.IsNullOrEmpty(d.Serial) ? d.SizeBytes.ToString() : d.Serial,
                    sizeBytes = d.SizeBytes, removable = d.Removable,
                    inUse = d.InUse, blank = d.Blank, contents = d.Contents
                })
            });
        });

        // Mount a filesystem that already exists. Non-destructive.
        group.MapPost("/prepare", async (PrepareRequest req, StorageSetupService storage,
                                         CancellationToken ct) => {
            if (req == null) return Results.BadRequest(new { error = "missing body" });
            var r = await storage.PrepareAsync(req.Uuid ?? "", req.MoveExisting, ct);
            return r.Ok
                ? Results.Ok(new { ok = true, mountPoint = r.MountPoint, captureDir = r.CaptureDir, log = r.Log })
                : Results.BadRequest(new { ok = false, error = r.Error, log = r.Log });
        });

        // ERASES THE DISK. The body has to name the device, echo back the
        // identity the survey reported for it, and spell out the confirmation;
        // the service re-surveys and re-checks all three before anything runs,
        // and the privileged script checks them again independently.
        group.MapPost("/format", async (FormatRequest req, StorageSetupService storage,
                                        CancellationToken ct) => {
            if (req == null) return Results.BadRequest(new { error = "missing body" });
            var r = await storage.FormatAsync(req.Device ?? "", req.Identity ?? "",
                                              req.Confirm ?? "", req.MoveExisting, ct);
            return r.Ok
                ? Results.Ok(new { ok = true, mountPoint = r.MountPoint, captureDir = r.CaptureDir, log = r.Log })
                : Results.BadRequest(new { ok = false, error = r.Error, log = r.Log });
        });
    }

    public record PrepareRequest(string? Uuid, bool MoveExisting = false);

    public record FormatRequest(string? Device, string? Identity, string? Confirm,
                                bool MoveExisting = false);
}
