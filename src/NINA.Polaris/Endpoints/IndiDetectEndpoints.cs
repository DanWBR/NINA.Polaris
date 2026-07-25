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
/// INDI profile assistant. Scans USB, proposes drivers, and (only on an
/// explicit confirmation) creates the indi-web profile.
///
/// <para>This exists because setting up a new rig otherwise means picking
/// drivers by hand out of ~420 installed entries, with no hint of which ones
/// match the hardware actually plugged in.</para>
///
/// <para><b>It narrows, it does not divine.</b> Cameras and vendor-specific
/// accessories resolve cleanly; the ToupTek platform is sold under a dozen
/// brands sharing one USB id so it can only offer a list; and mounts/focusers
/// behind USB-serial bridges are not identifiable at all. The response says so
/// per device via <c>confidence</c>, and the UI must let the operator settle
/// anything that is not <c>resolved</c>.</para>
/// </summary>
public static class IndiDetectEndpoints {
    public static void MapIndiDetectEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/indi/detect");

        // Scan + propose. Read-only: nothing here touches the INDI setup.
        group.MapGet("/", async (UsbScanService usb, IndiWebManagerService web,
                                 CancellationToken ct) => {
            var scan = usb.Scan();
            if (!scan.Supported) {
                return Results.Json(new { error = scan.UnsupportedReason }, statusCode: 501);
            }

            // Only ever propose a driver this machine actually has. When
            // indi-web is down the list comes back empty -- in that case skip
            // the filter entirely and flag it, rather than silently returning
            // "nothing detected", which would look like a scan failure.
            var installed = await web.GetInstalledDriversAsync(ct);
            var installedLabels = installed.Select(d => d.Label)
                                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool canFilter = installedLabels.Count > 0;

            var devices = scan.Devices.Select(d => {
                var match = IndiDeviceCatalog.Identify(d);
                var candidates = canFilter
                    ? match.CandidateLabels.Where(installedLabels.Contains).ToList()
                    : match.CandidateLabels.ToList();
                // A device we recognised but whose driver is not installed is
                // worth saying out loud: the fix is apt-get, not a re-scan.
                var confidence = match.Confidence;
                if (canFilter && match.CandidateLabels.Count > 0 && candidates.Count == 0)
                    confidence = "driver-missing";
                return new {
                    path = d.Path,
                    vendorId = d.VendorId,
                    productId = d.ProductId,
                    manufacturer = d.Manufacturer,
                    product = d.Product,
                    speedMbps = d.SpeedMbps,
                    kind = match.Kind,
                    confidence,
                    candidates,
                    note = match.Note,
                };
            }).ToList();

            return Results.Ok(new {
                devices,
                // Serial ports carry no driver guess by design. The UI shows
                // them so the operator can attach the right driver to each.
                serialPorts = scan.SerialPorts.Select(p => new {
                    byId = p.ByIdName,
                    device = p.Device,
                }),
                // The full installed list rides along so the UI can offer a
                // driver picker for the serial ports (and for anything it had
                // to mark unknown) without a second round trip. ~420 entries of
                // two short strings, grouped by family on the client.
                installedDrivers = installed
                    .OrderBy(d => d.Family ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { label = d.Label, family = d.Family }),
                installedDriverCount = installed.Count,
                driverListAvailable = canFilter,
                indiWebRunning = web.Running,
                // So the review dialog can warn BEFORE the operator commits
                // that the name they picked would replace an existing profile.
                existingProfiles = await web.GetProfileNamesAsync(ct),
            });
        });

        // Apply. Creates/overwrites an indi-web profile with the driver labels
        // the operator confirmed. Never called by the detect path itself.
        group.MapPost("/profile", async (CreateIndiProfileRequest req,
                                         IndiWebManagerService web,
                                         CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(req?.Name))
                return Results.BadRequest(new { error = "profile name required" });
            if (req.Drivers == null || req.Drivers.Length == 0)
                return Results.BadRequest(new { error = "at least one driver label required" });

            // Reject labels this host does not have instead of letting indi-web
            // store a profile that can never start.
            var installed = (await web.GetInstalledDriversAsync(ct))
                .Select(d => d.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (installed.Count > 0) {
                var unknown = req.Drivers.Where(l => !installed.Contains(l)).ToArray();
                if (unknown.Length > 0) {
                    return Results.BadRequest(new {
                        error = "driver(s) not installed on this host",
                        drivers = unknown,
                    });
                }
            }

            var existing = await web.GetProfileNamesAsync(ct);
            bool replaced = existing.Contains(req.Name, StringComparer.OrdinalIgnoreCase);

            var ok = await web.CreateProfileAsync(req.Name, req.Drivers, ct);
            if (!ok) {
                return Results.Json(new { error = web.LastError ?? "profile creation failed" },
                                    statusCode: 500);
            }
            return Results.Ok(new {
                status = replaced ? "updated" : "created",
                profile = req.Name,
                drivers = req.Drivers,
            });
        });
    }

    /// <param name="Name">indi-web profile to create or update.</param>
    /// <param name="Drivers">Driver LABELS (not binaries), already settled by
    /// the operator -- ambiguous matches resolved, serial ports assigned.</param>
    public record CreateIndiProfileRequest(string Name, string[] Drivers);
}
