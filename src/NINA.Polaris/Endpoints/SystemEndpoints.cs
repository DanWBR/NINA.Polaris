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

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class SystemEndpoints {
    public static void MapSystemEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/system");

        // Anonymous instance-identify endpoint for the mobile app's discovery
        // FALLBACK. On hotspot networks (the SBC's own AP, or the phone
        // tethering the SBC) mDNS multicast frequently never reaches the
        // phone, so ZeroConf finds nothing even though the server is one hop
        // away — the app then probes candidate addresses (known origins +
        // well-known hotspot gateways) with a plain fetch. CORS-open on
        // purpose: the Capacitor shell runs on https://localhost and must be
        // able to READ this reply cross-origin. Exposes only what the mDNS
        // TXT record already broadcasts to the whole LAN — no secrets, and
        // auth still gates everything else (exempted in AuthMiddleware).
        app.MapGet("/api/identify", (ProfileService profiles, MdnsService mdns, HttpContext ctx) => {
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            var friendly = profiles.Active.DeviceFriendlyName;
            if (string.IsNullOrWhiteSpace(friendly)) friendly = mdns.InstanceName;
            return Results.Ok(new {
                app = "polaris",
                instance = mdns.InstanceName,
                friendly,
                hostname = Environment.MachineName
            });
        });

        group.MapGet("/geocode", async (string query, int? limit, GeocodingService geo) => {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest(new { error = "query parameter required" });
            try {
                var results = await geo.SearchAsync(query, limit ?? 5);
                return Results.Ok(new {
                    query,
                    count = results.Count,
                    results
                });
            } catch (TimeoutException ex) {
                return Results.Problem(ex.Message, statusCode: 504);
            } catch (InvalidOperationException ex) {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        group.MapGet("/relay", (RelayClient relay) => Results.Ok(new {
            state = relay.State.ToString().ToLowerInvariant(),
            hostname = relay.AssignedHostname,
            lastError = relay.LastError
        }));

        // Which SBC config TUIs are installed, so the UI can offer an
        // "Optimize SBC" launcher (runs them via the Remote Terminal over SSH
        // to localhost; root is the user's own sudo). Linux-only; cheap path
        // probe, no subprocess.
        group.MapGet("/sbc-tools", () => {
            bool Has(params string[] paths) =>
                OperatingSystem.IsLinux() && paths.Any(System.IO.File.Exists);
            return Results.Ok(new {
                raspiConfig = Has("/usr/bin/raspi-config", "/usr/sbin/raspi-config"),
                armbianConfig = Has("/usr/bin/armbian-config", "/usr/sbin/armbian-config"),
                kind = HostInfo.Current.Kind
            });
        });

        group.MapGet("/status", (EquipmentManager equip) => {
            var process = Process.GetCurrentProcess();
            // PA-7: surface the auto-incrementing 0.1.{days}.{seconds/2}
            // version that NINA.Polaris.csproj computes at build time.
            // GetExecutingAssembly() returns this DLL, the AssemblyVersion
            // and InformationalVersion attributes are both set to the
            // same VersionPrefix in csproj. UI banner reads `version`.
            var asm = Assembly.GetExecutingAssembly();
            var asmVer = asm.GetName().Version?.ToString() ?? "0.0.0.0";
            var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? asmVer;
            // PA-7b: belt-and-suspenders, the csproj sets
            // IncludeSourceRevisionInInformationalVersion=false, but
            // some SDK versions / source-link configurations still
            // append "+{git-sha}". Strip anything past '+' so the UI
            // badge stays compact (the build hash is recoverable from
            // git log when needed).
            var plus = infoVer.IndexOf('+');
            if (plus > 0) infoVer = infoVer.Substring(0, plus);
            return Results.Ok(new {
                version = infoVer,
                versionParts = asmVer,
                platform = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                memoryMb = process.WorkingSet64 / (1024 * 1024),
                uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(@"d\.hh\:mm\:ss"),
                dotnetVersion = RuntimeInformation.FrameworkDescription,
                equipment = equip.GetEquipmentStatus()
            });
        });

        // GX-10: surface the HTTPS listener + cert metadata so the
        // Settings UI can render a "use this URL for WebGPU" banner +
        // a fingerprint the user can verify against Chrome's
        // cert-details dialog. Doesn't include anything sensitive
        // (the cert is self-signed; the PFX stays on the server).
        group.MapGet("/https-info",
            (IConfiguration cfg,
             SelfSignedCertService certSvc,
             HttpRequest req) => {
            var httpsEnabled = cfg.GetValue("Server:Https:Enabled", true);
            var httpPort  = cfg.GetValue("Server:Http:Port",  5000);
            var httpsPort = cfg.GetValue("Server:Https:Port", 5001);
            // Suggest concrete URLs the client can click on by mixing
            // the SAN-list names with the configured ports. We surface
            // the host the request came in on first (most relevant),
            // then a couple of LAN-friendly aliases.
            var sans = certSvc.SanEntries();
            string Decorate(string host, bool secure) {
                // IPv6 addresses need brackets in URL form. ":" present
                // and not at end → IPv6 literal.
                if (host.Contains(":") && !host.EndsWith(":")) host = "[" + host + "]";
                var port = secure ? httpsPort : httpPort;
                var defaultPort = secure ? 443 : 80;
                return (secure ? "https://" : "http://") + host
                    + (port == defaultPort ? "" : ":" + port);
            }
            return Results.Ok(new {
                httpsEnabled,
                httpPort,
                httpsPort,
                fingerprint = httpsEnabled ? certSvc.Fingerprint : null,
                // GX-12q2: SHA-256 is what modern browsers actually show
                // in their cert-details dialog. SHA-1 kept for legacy
                // tooling that might still query it.
                fingerprintSha256 = httpsEnabled ? certSvc.Fingerprint256 : null,
                // Names baked into the cert. Client picks the one
                // that matches what they typed into the address bar.
                hostnames = sans,
                requestHost = req.Host.Host,
                // Convenience: ready-to-click URLs for the top few hosts.
                exampleHttpUrls  = sans.Take(6).Select(s => Decorate(s, false)).ToArray(),
                exampleHttpsUrls = httpsEnabled
                    ? sans.Take(6).Select(s => Decorate(s, true)).ToArray()
                    : Array.Empty<string>()
            });
        });

        // GX-12q: Download the server's public certificate (PEM-encoded
        // DER, the format that Windows / macOS / iOS / Linux all accept
        // in their "import a root CA" dialogs). Stream it as
        // application/x-x509-ca-cert with a Content-Disposition so the
        // browser pops the save-or-install dialog instead of rendering
        // text. Public bytes only, the PFX with the private key
        // stays on the server and is never exposed.
        group.MapGet("/server-cert", (SelfSignedCertService certSvc) => {
            var cert = certSvc.GetOrCreate();
            var derBytes = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
            // PEM wrapper makes desktop "double-click to install" work
            // reliably on every OS; raw DER would also work but Windows
            // sometimes opens it in Notepad instead of certmgr.
            var b64 = Convert.ToBase64String(derBytes,
                Base64FormattingOptions.InsertLineBreaks);
            var pem = "-----BEGIN CERTIFICATE-----\n"
                + b64 + "\n-----END CERTIFICATE-----\n";
            var bytes = System.Text.Encoding.ASCII.GetBytes(pem);
            return Results.File(bytes, "application/x-x509-ca-cert",
                fileDownloadName: "polaris-root.crt");
        });

        // Profiles
        group.MapGet("/profiles", (ProfileService profiles) => {
            var list = profiles.ListProfiles();
            return Results.Ok(new {
                active = profiles.Active.Name,
                profiles = list
            });
        });

        group.MapGet("/profile", (ProfileService profiles) => {
            return Results.Ok(profiles.Active);
        });

        group.MapPut("/profile", (UserProfile update, ProfileService profiles) => {
            profiles.UpdateSettings(p => {
                p.Latitude = update.Latitude;
                p.Longitude = update.Longitude;
                p.Altitude = update.Altitude;
                p.SensorWidthMm = update.SensorWidthMm;
                p.SensorHeightMm = update.SensorHeightMm;
                p.FocalLengthMm = update.FocalLengthMm;
                p.SensorPixelsX = update.SensorPixelsX;
                p.SensorPixelsY = update.SensorPixelsY;
                p.DefaultExposure = update.DefaultExposure;
                p.DefaultGain = update.DefaultGain;
                p.DefaultBinning = update.DefaultBinning;
                p.IndiHost = update.IndiHost;
                p.IndiPort = update.IndiPort;
                p.AutoConnectOnStartup = update.AutoConnectOnStartup;
                p.AstapPath = update.AstapPath;
                p.SolveToleranceArcsec = update.SolveToleranceArcsec;
                p.ImageOutputDir = update.ImageOutputDir;
                p.ImageNamePattern = update.ImageNamePattern;
                p.ImageFormat = update.ImageFormat;
                p.PreferAdvancedSequencer = update.PreferAdvancedSequencer;
                // DBGLOG-9: opt-in disk persistence for the debug log.
                p.LogToDisk = update.LogToDisk;
                // External-tool path overrides. Empty/null = auto-detect.
                p.SirilPath = update.SirilPath;
                p.SirilScriptsDir = update.SirilScriptsDir;
                p.GraXpertPath = update.GraXpertPath;
                p.GraXpertBgeSmoothing = update.GraXpertBgeSmoothing;
                p.GraXpertBgeCorrection = update.GraXpertBgeCorrection
                                              ?? p.GraXpertBgeCorrection;
                p.GraXpertDeconStrength = update.GraXpertDeconStrength;
                p.GraXpertDeconPsfSize = update.GraXpertDeconPsfSize;
                p.GraXpertDenoiseStrength = update.GraXpertDenoiseStrength;
                // GX-1b: ONNX in-browser inference settings.
                p.OnnxModelsPath = update.OnnxModelsPath ?? p.OnnxModelsPath;
                p.OnnxModelsBucketUrl = update.OnnxModelsBucketUrl ?? p.OnnxModelsBucketUrl;
                p.OnnxLicenseAcknowledged = update.OnnxLicenseAcknowledged;
                p.OnnxDefaultDenoiseVersion = update.OnnxDefaultDenoiseVersion
                                                  ?? p.OnnxDefaultDenoiseVersion;
                p.OnnxPreferCli = update.OnnxPreferCli;
                // UI language: only overwrite when the client actually sent a
                // value, so a settings save from a page that omits it doesn't
                // reset the stored preference.
                if (!string.IsNullOrWhiteSpace(update.UiLanguage))
                    p.UiLanguage = update.UiLanguage;
            });
            return Results.Ok(new { message = "Profile saved" });
        });

        // Dedicated UI-language setter. Separate from PUT /profile (which binds
        // a full UserProfile and would clobber every other setting) so the
        // language picker can persist just this one field. The browser's
        // localStorage stays the source of truth; this only seeds a fresh
        // browser / the Android wrapper.
        group.MapPut("/ui-language", (UiLanguageRequest req, ProfileService profiles) => {
            var lang = (req?.Language ?? "").Trim();
            var allowed = new[] { "en", "pt-BR", "es", "fr", "de" };
            if (!allowed.Contains(lang)) return Results.BadRequest(new { error = "unsupported language" });
            profiles.UpdateSettings(p => p.UiLanguage = lang);
            return Results.Ok(new { language = lang });
        });

        group.MapPost("/profile/save-as", (SaveAsRequest request, ProfileService profiles) => {
            profiles.SaveAs(request.Name);
            return Results.Ok(new { message = $"Profile saved as '{request.Name}'" });
        });

        group.MapPost("/profile/load/{id}", (string id, ProfileService profiles) => {
            if (profiles.LoadProfile(id))
                return Results.Ok(new { message = "Profile loaded", name = profiles.Active.Name });
            return Results.NotFound(new { error = "Profile not found" });
        });

        // Factory reset: wipe ALL configuration back to a fresh
        // install so the operator can ship a clean distribution image
        // with none of their rigs, location, password, camera quirks,
        // or test data. The active profile (+ every named profile and
        // the auth sessions) is reset to defaults, and the sibling
        // cache dirs (studio frame index, on-disk debug logs, editor
        // sidecars) are removed best-effort. Captured images in the
        // operator's output folder are intentionally left alone --
        // that is data, not config. The client clears its own
        // localStorage + reloads after this returns.
        group.MapPost("/factory-reset", (ProfileService profiles, ILogger<ProfileService> logger) => {
            int removed;
            try {
                removed = profiles.FactoryReset();
            } catch (Exception ex) {
                logger.LogError(ex, "Factory reset: profile wipe failed");
                return Results.Problem("Factory reset failed: " + ex.Message);
            }

            // Best-effort wipe of sibling cache directories. Derive the
            // app root from the profile dir (.../NINA.Polaris/profiles
            // -> .../NINA.Polaris) and remove the studio index + logs.
            // Per-dir errors are swallowed so a locked file doesn't
            // abort the reset -- the profile is already gone by here.
            try {
                var appRoot = Directory.GetParent(profiles.DataDir)?.FullName;
                if (!string.IsNullOrEmpty(appRoot)) {
                    foreach (var sub in new[] { "studio", "logs" }) {
                        var dir = Path.Combine(appRoot, sub);
                        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                        catch (Exception ex) { logger.LogWarning(ex, "Factory reset: could not remove {Dir}", dir); }
                    }
                }
            } catch (Exception ex) {
                logger.LogWarning(ex, "Factory reset: cache cleanup skipped");
            }

            // Editor sidecars live under a separate LocalAppData root.
            try {
                var sidecars = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Polaris", "sidecars");
                if (Directory.Exists(sidecars)) Directory.Delete(sidecars, true);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Factory reset: could not remove editor sidecars");
            }

            logger.LogWarning("Factory reset done ({Count} config file(s) removed); "
                + "app is now at first-run defaults.", removed);
            return Results.Ok(new {
                message = "Factory reset complete. Reloading to first-run state.",
                filesRemoved = removed
            });
        });

        // CLOCK-1: client-driven wall-clock sync. The /clock GET is
        // cheap status (used by Settings + the activity-bar chip);
        // POST /clock/sync writes the client's UTC into the system
        // clock via timedatectl (Linux only, polkit-allowed for the
        // polaris user). Both are gated by AuthMiddleware like every
        // other /api/* route.
        group.MapGet("/clock", (ClockSyncService clock) => {
            return Results.Ok(new {
                serverUtcNow = clock.ServerUtcNow().ToString("o"),
                supported = clock.IsSupported
            });
        });

        group.MapPost("/clock/sync", async (ClockSyncService clock,
                ClockSyncRequest req, CancellationToken ct) => {
            if (req == null || string.IsNullOrWhiteSpace(req.ClientUtc)) {
                return Results.BadRequest(new { error = "clientUtc is required (ISO-8601)" });
            }
            // DateTimeStyles.RoundtripKind is MUTUALLY EXCLUSIVE with
            // AssumeUniversal / AdjustToUniversal -- the runtime throws
            // ArgumentException when you combine them. Pre-fix the
            // call crashed every clock-sync request with a 500 because
            // of that. Drop RoundtripKind: AssumeUniversal +
            // AdjustToUniversal handles both ISO-8601 forms we care
            // about (the JS toISOString() always emits a trailing Z;
            // AssumeUniversal also covers the rare case where the
            // client sends a timezone-less string).
            if (!DateTime.TryParse(req.ClientUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed)) {
                return Results.BadRequest(new { error = "clientUtc must be ISO-8601" });
            }
            // Short-circuit on platforms that physically can't do it
            // (Windows / macOS). 501 Not Implemented is the right code:
            // the route exists but the host can't honour it. Previously
            // returned 500 which surfaced as a generic crash in the
            // browser console.
            if (!clock.IsSupported) {
                return Results.Json(new {
                    ok = false,
                    error = "Clock sync is Linux-only on this host. "
                          + "Use the OS clock settings or enable NTP."
                }, statusCode: 501);
            }
            var result = await clock.SetUtcAsync(parsed, ct);
            if (!result.Ok) {
                return Results.Json(new {
                    ok = false,
                    error = result.Error,
                    serverUtcNow = result.ServerUtcNow.ToString("o")
                }, statusCode: 500);
            }
            return Results.Ok(new {
                ok = true,
                serverUtcNow = result.ServerUtcNow.ToString("o"),
                residualSkewSeconds = result.ResidualSkewSeconds
            });
        });

        // ---- Power / lifecycle (PowerService) ----
        // Restart the Polaris process, reboot the whole device, and (on
        // Windows) toggle boot auto-start. All gated by AuthMiddleware like
        // every /api/* route; the actual restart/reboot happens ~700ms after
        // the response flushes so the browser sees the ack first.
        group.MapGet("/power", (PowerService power) => Results.Ok(power.GetInfo()));

        // OCL: SBC GPU (OpenCL) capability + which compute backend is active.
        // available/enabled/loaderPresent come from the cheap runtime probe;
        // backend/hardware reflect the resolved IGpuCompute (CPU vs OpenCL).
        group.MapGet("/gpu", (NINA.Image.Gpu.IGpuCompute gpu) => {
            // Force the lazy OpenCL init so first bring-up sees a real result:
            // device name on success, or the failure reason (incl. the OpenCL
            // build log) on failure.
            bool initialized = false;
            string? device = null, initError = null;
            bool? unifiedMemory = null;
            string[]? offloadedOps = null;
            if (gpu is NINA.Polaris.Services.OpenCl.OpenClGpuCompute ocl) {
                initialized = ocl.EnsureInitialized();
                device = ocl.Device;
                initError = ocl.InitError;
                unifiedMemory = ocl.HostUnifiedMemory;
                // Some kernels run on the CPU instead when the per-op probe at
                // init measured the GPU as slower (true on a discrete GPU, and
                // also on unified-memory stacks like Adreno that copy buffers);
                // expose which ops actually offload.
                offloadedOps = ocl.OffloadedOps.Select(o => o.ToString()).ToArray();
            }
            bool userEnabled = gpu is NINA.Polaris.Services.OpenCl.OpenClGpuCompute o2 ? o2.Enabled : false;
            return Results.Ok(new {
                available = NINA.Polaris.Services.OpenCl.OpenClRuntime.IsAvailable,
                enabled = NINA.Polaris.Services.OpenCl.OpenClRuntime.Enabled,
                loaderPresent = NINA.Polaris.Services.OpenCl.OpenClRuntime.LoaderPresent,
                userEnabled,
                backend = gpu.BackendName,
                hardware = gpu.IsHardware,
                initialized,
                device,
                initError,
                unifiedMemory,
                offloadedOps,
                diagnostics = NINA.Polaris.Services.OpenCl.OpenClRuntime.Diagnostics
            });
        });

        // OCL: enable/disable GPU (OpenCL) use at runtime + persist the choice.
        // Takes effect immediately (the backend gates each call on it); on boot
        // it's restored from UserProfile.UseGpuOpenCl.
        group.MapPost("/gpu", (NINA.Image.Gpu.IGpuCompute gpu, ProfileService profiles, GpuToggleRequest req) => {
            profiles.Active.UseGpuOpenCl = req.Enabled;
            profiles.Save();
            if (gpu is NINA.Polaris.Services.OpenCl.OpenClGpuCompute ocl) ocl.Enabled = req.Enabled;
            return Results.Ok(new { ok = true, enabled = req.Enabled, hardware = gpu.IsHardware });
        });

        // OCL: in-process GPU-vs-CPU kernel validation. Runs every kernel on
        // this machine's GPU and diffs against the CPU reference, so a board
        // with only the installed .deb (no test project) can still confirm its
        // GPU produces correct output. Returns per-kernel maxDiff + pass/fail.
        group.MapGet("/gpu/selftest", (NINA.Image.Gpu.IGpuCompute gpu) => {
            var results = NINA.Polaris.Services.OpenCl.GpuSelfTest.Run(gpu);
            return Results.Ok(new {
                backend = gpu.BackendName,
                hardware = gpu.IsHardware,
                allOk = results.All(r => r.Ok),
                kernels = results
            });
        });

        group.MapPost("/restart-app", (PowerService power) => {
            var r = power.ScheduleRestart();
            return r.Ok
                ? Results.Ok(new { ok = true, message = r.Message })
                : Results.Json(new { ok = false, error = r.Message }, statusCode: r.StatusCode);
        });

        group.MapPost("/reboot", (PowerService power) => {
            var r = power.ScheduleReboot();
            return r.Ok
                ? Results.Ok(new { ok = true, message = r.Message })
                : Results.Json(new { ok = false, error = r.Message }, statusCode: r.StatusCode);
        });

        group.MapPost("/shutdown", (PowerService power) => {
            var r = power.ScheduleShutdown();
            return r.Ok
                ? Results.Ok(new { ok = true, message = r.Message })
                : Results.Json(new { ok = false, error = r.Message }, statusCode: r.StatusCode);
        });

        group.MapPost("/autostart", (AutoStartRequest req, PowerService power) => {
            var r = power.SetAutoStart(req?.Enable ?? false);
            return r.Ok
                ? Results.Ok(new { ok = true, message = r.Message, enabled = power.GetInfo().AutoStartEnabled })
                : Results.Json(new { ok = false, error = r.Message }, statusCode: r.StatusCode);
        });

        // Legacy settings (redirect to profile)
        group.MapGet("/settings", (ProfileService profiles) => {
            var p = profiles.Active;
            return Results.Ok(new {
                observatoryLatitude = p.Latitude,
                observatoryLongitude = p.Longitude,
                observatoryAltitude = p.Altitude,
                sensorWidthMm = p.SensorWidthMm,
                sensorHeightMm = p.SensorHeightMm,
                focalLengthMm = p.FocalLengthMm,
                imageFormat = p.ImageFormat,
                plateSolver = "ASTAP",
                indiHost = p.IndiHost,
                indiPort = p.IndiPort,
                // DBGLOG-9: surface the toggle so the Settings UI hydrates correctly.
                logToDisk = p.LogToDisk,
                // Remote-terminal opt-in (persisted runtime override of the
                // appsettings Terminal:Enabled gate).
                terminalEnabled = p.TerminalEnabled
            });
        });

        // Enable / disable the in-browser SSH remote terminal at runtime.
        // Persisted on the profile so it survives restarts. The Settings UI
        // gates this behind a risk-acknowledgement modal; full shell access
        // to the host is a serious capability, so the default stays OFF.
        group.MapPost("/terminal/enabled", (TerminalEnableRequest req, ProfileService profiles) => {
            profiles.UpdateSettings(p => p.TerminalEnabled = req.Enabled);
            return Results.Ok(new { enabled = req.Enabled });
        });

        // Friendly device name shown when this Pi is discovered on the
        // network. Lets the owner of a cloned SD-card image label each Pi
        // ("Telescope on the balcony"). Re-announces mDNS so the new name
        // shows up without a restart.
        group.MapGet("/device-name", (ProfileService profiles, MdnsService mdns) =>
            Results.Ok(new {
                friendlyName = profiles.Active.DeviceFriendlyName,
                mdnsName = mdns.InstanceName
            }));

        group.MapPost("/device-name", (DeviceNameRequest req, ProfileService profiles, MdnsService mdns) => {
            profiles.Active.DeviceFriendlyName = (req.Name ?? "").Trim();
            profiles.Save();
            mdns.Republish();
            return Results.Ok(new {
                friendlyName = profiles.Active.DeviceFriendlyName,
                mdnsName = mdns.InstanceName
            });
        });
    }

    record SaveAsRequest(string Name);
    record ClockSyncRequest(string ClientUtc);
    record DeviceNameRequest(string? Name);
    record AutoStartRequest(bool Enable);
    record GpuToggleRequest(bool Enabled);
    record TerminalEnableRequest(bool Enabled);
    record UiLanguageRequest(string? Language);
}