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

        // INDIAUTO: should the assistant open on its own? Cheap on purpose --
        // a sysfs read plus one small indi-web call per profile. The scan above
        // pulls the ~420-entry installed-driver list, which is far too much to
        // run on every client boot just to find out the answer is usually no.
        group.MapGet("/needed", async (UsbScanService usb, IndiWebManagerService web,
                                       CancellationToken ct) => {
            var scan = usb.Scan();
            if (!scan.Supported)
                return Results.Ok(new { suggest = false, reason = scan.UnsupportedReason });
            // A host whose indi-web is down has a different problem, and the
            // assistant could not create a profile anyway. Say nothing.
            if (!web.Running)
                return Results.Ok(new { suggest = false, reason = "indi-web is not running" });

            var names = await web.GetProfileNamesAsync(ct);
            var profiles = new List<object>();
            bool configured = false;
            foreach (var name in names) {
                var labels = await web.GetProfileDriverLabelsAsync(name, ct);
                // A profile of nothing but simulators is what a host that has
                // never been set up looks like: indi-web ships one, so the mere
                // EXISTENCE of a profile proves nothing. One real driver
                // anywhere means somebody has been here already, and the
                // assistant should stay out of the way.
                var real = labels.Where(l => !IsSimulator(l)).ToList();
                if (real.Count > 0) configured = true;
                profiles.Add(new { name, drivers = labels, simulatorOnly = real.Count == 0 });
            }

            // Nothing plugged in means the assistant would open on an empty
            // list, which is worse than not opening. Serial ports do not count:
            // they are unidentifiable on their own and every host has some.
            var identifiable = scan.Devices
                .Count(d => IndiDeviceCatalog.Identify(d).Confidence != "unknown");

            return Results.Ok(new {
                suggest = !configured && identifiable > 0,
                configured,
                profiles,
                deviceCount = scan.Devices.Count,
                identifiableCount = identifiable,
                // Identifies THIS set of hardware, so a dismissal can be scoped
                // to it: plugging in a different camera next month is a new
                // question, and "not now" today should not answer it forever.
                fingerprint = Fingerprint(scan),
                reason = configured ? "an INDI profile with real drivers already exists"
                       : identifiable == 0 ? "no recognisable equipment is plugged in"
                       : null,
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

            // Select + start the profile we just wrote. Without this the
            // assistant stops one step short of useful: the profile exists but
            // indiserver is still running the old one (or nothing at all), so
            // no device actually comes up. Reported separately from the
            // creation result -- a profile that exists but failed to start is a
            // partial success the operator can finish by hand, not a reason to
            // claim the whole operation failed.
            var started = await web.StartServerAsync(req.Name, ct);
            return Results.Ok(new {
                status = replaced ? "updated" : "created",
                profile = req.Name,
                drivers = req.Drivers,
                started,
                startError = started ? null : web.LastError,
            });
        });
    }

    /// <summary>INDI's simulator drivers all carry "Simulator" in their label,
    /// which is exactly how a person tells them apart in the driver list.
    ///
    /// <para>The profile indi-web seeds into a fresh database is
    /// <c>Simulators</c> with <c>Telescope Simulator</c>, <c>CCD Simulator</c>
    /// and <c>Focuser Simulator</c> (read from its own database.py, not
    /// assumed), so this rule is what separates a host nobody has touched from
    /// one that is already set up.</para></summary>
    internal static bool IsSimulator(string label) =>
        label.Contains("Simulator", StringComparison.OrdinalIgnoreCase);

    /// <summary>A stable id for the hardware currently plugged in, so a "not
    /// now" can be remembered against THIS set of gear rather than for good.
    /// Sorted, because enumeration order is not stable across reboots.</summary>
    private static string Fingerprint(UsbScanResult scan) {
        var ids = scan.Devices
            .Select(d => $"{d.VendorId}:{d.ProductId}")
            .OrderBy(s => s, StringComparer.Ordinal);
        var joined = string.Join(",", ids);
        if (joined.Length == 0) return "";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <param name="Name">indi-web profile to create or update.</param>
    /// <param name="Drivers">Driver LABELS (not binaries), already settled by
    /// the operator -- ambiguous matches resolved, serial ports assigned.</param>
    public record CreateIndiProfileRequest(string Name, string[] Drivers);
}
