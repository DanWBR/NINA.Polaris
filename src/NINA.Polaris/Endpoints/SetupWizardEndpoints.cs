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
/// First-run equipment connection wizard.
///
/// <para>The Linux flow probes hardware by actually starting drivers: it
/// creates a TEMPORARY indi-web profile carrying every installed
/// hardware-enumerating driver from the major camera/wheel/focuser
/// manufacturers plus whatever the USB scan resolved, starts it, and lets the
/// devices that really exist publish themselves. Cameras, filter wheels and
/// USB focusers only appear when the hardware is present, so what shows up IS
/// what is plugged in. Mounts hide behind generic USB-serial bridges and their
/// drivers publish a device whether or not a mount is attached, so those are
/// offered as a guided choice per detected serial port instead of being
/// probed. Once the operator confirms, the temporary profile is deleted and a
/// final profile with only the confirmed drivers is created with autostart +
/// autoconnect and started.</para>
///
/// <para>The Windows flow has no INDI side; the front-end fans out to the
/// existing ASCOM/Alpaca discovery routes and only the completion flag lives
/// here.</para>
/// </summary>
public static class SetupWizardEndpoints {

    /// <summary>The throwaway probe profile. Constant so an abandoned run
    /// (browser closed mid-wizard) is cleaned up by the next run instead of
    /// leaking one profile per attempt.</summary>
    internal const string TempProfileName = "polaris-wizard-tmp";

    // Installed-driver labels containing any of these are safe to start
    // blind: they enumerate real hardware and publish nothing when none is
    // present. Deliberately excludes serial-bridge families (EQMod, OnStep,
    // ...), whose drivers publish a device with or without hardware.
    private static readonly string[] ProbeBrandFragments = [
        "ZWO", "Toupcam", "Player One", "PlayerOne", "SVBONY", "QHY", "Altair",
    ];

    // Installed-driver labels containing any of these are offered as the
    // shortlist for detected serial ports (mounts/focusers behind CH340/FTDI
    // bridges). The full installed list rides along for anything exotic.
    private static readonly string[] SerialBrandFragments = [
        "EQMod", "SynScan", "SkyWatcher", "Sky-Watcher", "OnStep", "Gemini",
        "Celestron", "iOptron", "Losmandy", "StarGo", "AM5", "LX200",
        "Pegasus", "MoonLite", "AstroTrac", "Rainbow",
    ];

    public static void MapSetupWizardEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/setup-wizard");

        // What kind of host is this and does onboarding still apply? Cheap:
        // no USB scan, no installed-driver pull.
        group.MapGet("/info", async (IndiWebManagerService web, ProfileService profiles,
                                     CancellationToken ct) => {
            var platform = OperatingSystem.IsWindows() ? "windows"
                         : OperatingSystem.IsLinux() ? "linux"
                         : OperatingSystem.IsMacOS() ? "macos" : "other";

            var rig = profiles.ActiveEquipmentProfile;
            bool rigConfigured = !string.IsNullOrWhiteSpace(rig?.Camera)
                              || !string.IsNullOrWhiteSpace(rig?.Telescope);

            // On Linux, "already set up" also means an indi-web profile with a
            // real (non-simulator) driver exists — same rule as INDIAUTO.
            bool indiConfigured = false;
            if (web.IsSupportedOs && web.Running) {
                foreach (var name in await web.GetProfileNamesAsync(ct)) {
                    if (string.Equals(name, TempProfileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var labels = await web.GetProfileDriverLabelsAsync(name, ct);
                    if (labels.Any(l => !IndiDetectEndpoints.IsSimulator(l))) {
                        indiConfigured = true;
                        break;
                    }
                }
            }

            return Results.Ok(new {
                platform,
                indiSupported = web.IsSupportedOs,
                indiWebInstalled = web.Installed,
                indiWebRunning = web.Running,
                wizardCompletedUtc = profiles.Active.SetupWizardCompletedUtc,
                rigConfigured,
                indiConfigured,
            });
        });

        // Linux probe: build the temporary profile and start it. The devices
        // themselves are read by the front-end from /api/equipment/devices
        // (which now carries decoded roles) after connecting to INDI.
        group.MapPost("/indi/probe", async (UsbScanService usb, IndiWebManagerService web,
                                            CancellationToken ct) => {
            if (!web.IsSupportedOs)
                return Results.Json(new { error = "INDI is not supported on this host OS" },
                                    statusCode: 501);
            if (!web.Running && web.Installed)
                await web.StartAsync(ct);
            if (!web.Running)
                return Results.Json(new { error = web.Installed
                        ? "indi-web could not be started"
                        : "indi-web is not installed on this host" },
                    statusCode: 503);

            var scan = usb.Scan();
            if (!scan.Supported)
                return Results.Json(new { error = scan.UnsupportedReason }, statusCode: 501);

            var installed = await web.GetInstalledDriversAsync(ct);
            var installedLabels = installed.Select(d => d.Label)
                                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Same per-device projection as /api/indi/detect so the UI can
            // show what the USB bus says while drivers come up.
            var usbDevices = scan.Devices.Select(d => {
                var match = IndiDeviceCatalog.Identify(d);
                var candidates = installedLabels.Count > 0
                    ? match.CandidateLabels.Where(installedLabels.Contains).ToList()
                    : match.CandidateLabels.ToList();
                var confidence = match.Confidence;
                if (installedLabels.Count > 0 && match.CandidateLabels.Count > 0 && candidates.Count == 0)
                    confidence = "driver-missing";
                return new {
                    path = d.Path,
                    vendorId = d.VendorId,
                    productId = d.ProductId,
                    manufacturer = d.Manufacturer,
                    product = d.Product,
                    kind = match.Kind,
                    confidence,
                    candidates,
                    note = match.Note,
                };
            }).ToList();

            var probeDrivers = BuildProbeDrivers(
                installed.Select(d => d.Label),
                usbDevices.Select(d => (IReadOnlyList<string>)d.candidates));

            bool started = false;
            string? startError = null;
            if (probeDrivers.Count > 0) {
                var created = await web.CreateProfileAsync(TempProfileName, probeDrivers, ct);
                if (!created)
                    return Results.Json(new { error = web.LastError ?? "temporary profile creation failed" },
                                        statusCode: 500);
                started = await web.StartServerAsync(TempProfileName, ct);
                startError = started ? null : web.LastError;
            }

            return Results.Ok(new {
                tempProfile = TempProfileName,
                probeDrivers,
                started,
                startError,
                devices = usbDevices,
                serialPorts = scan.SerialPorts.Select(p => new {
                    byId = p.ByIdName,
                    device = p.Device,
                }),
                suggestedSerialDrivers = SelectByFragments(installed, SerialBrandFragments)
                    .Select(d => new { label = d.Label, family = d.Family }),
                installedDrivers = installed
                    .OrderBy(d => d.Family ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { label = d.Label, family = d.Family }),
            });
        });

        // Swap the temporary probe profile for the confirmed final one.
        group.MapPost("/indi/finalize", async (FinalizeWizardRequest req,
                                               IndiWebManagerService web,
                                               ProfileService profiles,
                                               CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(req?.Name))
                return Results.BadRequest(new { error = "profile name required" });
            if (req.Drivers == null || req.Drivers.Length == 0)
                return Results.BadRequest(new { error = "at least one driver label required" });
            if (string.Equals(req.Name.Trim(), TempProfileName, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "that profile name is reserved" });
            if (!web.IsSupportedOs)
                return Results.Json(new { error = "INDI is not supported on this host OS" },
                                    statusCode: 501);

            var installed = (await web.GetInstalledDriversAsync(ct))
                .Select(d => d.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (installed.Count > 0) {
                var unknown = req.Drivers.Where(l => !installed.Contains(l)).ToArray();
                if (unknown.Length > 0)
                    return Results.BadRequest(new {
                        error = "driver(s) not installed on this host",
                        drivers = unknown,
                    });
            }

            // Tear the probe down. Best-effort: a temp profile that would not
            // stop or delete must not block the final profile from existing.
            await web.StopServerAsync(ct);
            await web.DeleteProfileAsync(TempProfileName, ct);

            var existing = await web.GetProfileNamesAsync(ct);
            bool replaced = existing.Contains(req.Name, StringComparer.OrdinalIgnoreCase);

            // CreateProfileAsync sets autostart + autoconnect, which is
            // exactly the "default profile that starts and loads on its own"
            // the wizard promises.
            var ok = await web.CreateProfileAsync(req.Name, req.Drivers, ct);
            if (!ok)
                return Results.Json(new { error = web.LastError ?? "profile creation failed" },
                                    statusCode: 500);

            var started = await web.StartServerAsync(req.Name, ct);

            profiles.Active.SetupWizardCompletedUtc = DateTime.UtcNow;
            profiles.Save();

            return Results.Ok(new {
                status = replaced ? "updated" : "created",
                profile = req.Name,
                drivers = req.Drivers,
                started,
                startError = started ? null : web.LastError,
            });
        });

        // Clean up an abandoned probe. Only stops indiserver when it is
        // running OUR temp profile — never an operator's real profile.
        group.MapPost("/indi/abort", async (IndiWebManagerService web, CancellationToken ct) => {
            if (!web.IsSupportedOs) return Results.Ok(new { cleaned = false });
            var active = await web.GetActiveProfileAsync(ct);
            if (string.Equals(active, TempProfileName, StringComparison.OrdinalIgnoreCase))
                await web.StopServerAsync(ct);
            var deleted = await web.DeleteProfileAsync(TempProfileName, ct);
            return Results.Ok(new { cleaned = deleted });
        });

        // Mark onboarding done without touching INDI: the Windows path and
        // the explicit "skip" both land here.
        group.MapPost("/complete", (ProfileService profiles) => {
            profiles.Active.SetupWizardCompletedUtc = DateTime.UtcNow;
            profiles.Save();
            return Results.Ok(new { completed = true });
        });
    }

    /// <summary>The driver labels the temporary probe profile starts: every
    /// installed hardware-enumerating brand driver plus whatever the USB scan
    /// resolved to an installed label (DSLRs, QHY, anything the catalog
    /// pinned). Simulators never make the list.</summary>
    internal static List<string> BuildProbeDrivers(IEnumerable<string> installedLabels,
                                                   IEnumerable<IReadOnlyList<string>> usbCandidates) {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var label in installedLabels) {
            if (string.IsNullOrWhiteSpace(label) || IndiDetectEndpoints.IsSimulator(label))
                continue;
            if (ProbeBrandFragments.Any(f => label.Contains(f, StringComparison.OrdinalIgnoreCase))
                && seen.Add(label)) {
                result.Add(label);
            }
        }

        // Unambiguous USB matches (a single installed candidate) join the
        // probe even outside the brand list — the catalog already did the
        // identification, and one candidate means there is nothing to ask.
        foreach (var candidates in usbCandidates) {
            if (candidates is { Count: 1 } && !IndiDetectEndpoints.IsSimulator(candidates[0])
                && seen.Add(candidates[0])) {
                result.Add(candidates[0]);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static IEnumerable<IndiInstalledDriver> SelectByFragments(
            IEnumerable<IndiInstalledDriver> installed, string[] fragments) {
        return installed
            .Where(d => !IndiDetectEndpoints.IsSimulator(d.Label)
                     && fragments.Any(f => d.Label.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(d => d.Family ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="Name">Final indi-web profile name.</param>
    /// <param name="Drivers">Driver LABELS the operator confirmed.</param>
    public record FinalizeWizardRequest(string Name, string[] Drivers);
}
