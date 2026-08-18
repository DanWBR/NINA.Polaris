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

using System.Globalization;
using NINA.Polaris.Endpoints;
using NINA.Polaris.Middleware;
using NINA.Polaris.Services;
using NINA.Polaris.WebSocket;
using NINA.INDI.Client;
using Yarp.ReverseProxy.Forwarder;

// Force English exception messages + invariant number/date formatting
// regardless of the host's locale. The rest of the UI is English-only,
// so localized SocketException / IOException strings (e.g. "Nenhuma
// conexão pôde ser feita..." on pt-BR systems) leaking into the
// debug-log panel breaks the consistent reading experience. Setting
// DefaultThreadCurrentUICulture covers any thread that doesn't
// explicitly opt in to a different culture; the existing thread's
// own culture is also overridden because Program.cs runs on the
// process's main thread.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

// WINEXIT: capture a process-fatal crash SYNCHRONOUSLY. The normal JSONL log is
// flushed asynchronously (~2 s) by LogRotatorService, so a hard teardown -- e.g.
// a corrupted-state exception (AccessViolationException / SEHException) raised by
// a flaky ASCOM COM driver during in-process polling, which .NET tears the
// process down for regardless of try/catch -- loses the last buffered lines and
// leaves the user's log ending with no error ("Polaris just closed after a
// minute"). These handlers write the reason to a crash file the moment it
// happens, so a field crash the user can't reproduce for us is still
// diagnosable (and Canopus's read_logs/read_file can surface it).
{
    var crashDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA.Polaris", "logs");
    void WriteCrash(string kind, Exception? ex, bool terminating) {
        try {
            Directory.CreateDirectory(crashDir);
            var file = Path.Combine(crashDir, $"polaris_crash_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
            File.AppendAllText(file,
                $"[{DateTimeOffset.Now:o}] {kind} (terminating={terminating})\n{ex}\n\n");
            Console.Error.WriteLine($"[FATAL] {kind}: {ex?.Message}");
            Console.Error.Flush();
        } catch { /* last-ditch; a crash handler must never throw */ }
    }
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        WriteCrash("UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);
    TaskScheduler.UnobservedTaskException += (_, e) =>
        WriteCrash("UnobservedTaskException", e.Exception, false);
}

// Sub-process entry: when the parent server spawns us with
// `--ascom-setup <ProgID>` we run the ASCOM SetupDialog and exit,
// skipping all of the HTTP/Kestrel boot. See AscomSetupRunner +
// AscomEndpoints for the rationale (driver AVE isolation — keeps a
// crashing ZWO ASCOM driver from killing the main API server).
if (args.Length >= 2
    && args[0] == "--ascom-setup"
    && OperatingSystem.IsWindows()) {
    return NINA.Polaris.AscomSetupRunner.Run(args[1]);
}

// Sub-process entry: `--ascom-com-host` runs one ASCOM filter-wheel driver in
// this minimal child (no Kestrel/DI) and serves it over a stdin/stdout JSON
// protocol. The driver misbehaves in the loaded server process but works in a
// clean child, so the app hosts it here and marshals every call. A driver
// crash kills only this child; the API server survives. See AscomComHostRunner.
if (args.Length >= 1
    && args[0] == "--ascom-com-host"
    && OperatingSystem.IsWindows()) {
    return await NINA.Ascom.Com.AscomComHostRunner.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// GX-10: HTTPS self-signed cert. Constructed eagerly here (not via DI)
// because Kestrel's ConfigureKestrel callback needs the cert *before*
// builder.Build() runs, and we want the SAME instance shared with the
// rest of the app so the Settings UI fingerprint matches what Kestrel
// is actually serving. Register the constructed singleton so endpoints
// + the status feed can pick it up by injection.
//
// GX-10b: defaults changed so port 5000 is the HTTPS-on-LAN port (what
// users actually want to type) and HTTP gets demoted to a loopback-only
// service port for the Relay tunnel + curl-from-the-host scripts. Net
// effect: any LAN device can ONLY reach Polaris via HTTPS, so WebGPU
// + multi-thread WASM "just work" on the URL the user naturally tries.
// Backwards-compat: legacy `Server:Http:Port` config still honoured;
// users can override either side or disable HTTPS entirely.
var httpsEnabled = builder.Configuration.GetValue("Server:Https:Enabled", true);
var httpEnabled  = builder.Configuration.GetValue("Server:Http:Enabled",  true);
var httpPort     = builder.Configuration.GetValue("Server:Http:Port",  5080);
var httpsPort    = builder.Configuration.GetValue("Server:Https:Port", 5000);
// Loopback-only HTTP keeps plaintext OFF the LAN. Power-users who
// need HTTP exposed to the LAN (legacy integrations, no-TLS-stack
// clients) flip Server:Http:Bind = "any".
var httpBindAny  = builder.Configuration.GetValue("Server:Http:Bind", "loopback")
                       .Equals("any", StringComparison.OrdinalIgnoreCase);
// Redirect plaintext HTTP to HTTPS (default on). When enabled with HTTPS, the
// HTTP listener is exposed on the LAN too, but it only ever issues 307s to the
// HTTPS endpoint -- no real content is served over plaintext -- so a user who
// browses http://<host>:<httpPort> lands on https automatically instead of a
// "connection refused" / "not found".
var httpRedirect = httpsEnabled &&
    builder.Configuration.GetValue("Server:Http:RedirectToHttps", true);
var certService = new NINA.Polaris.Services.SelfSignedCertService(
    builder.Configuration,
    Microsoft.Extensions.Logging.Abstractions.NullLogger<NINA.Polaris.Services.SelfSignedCertService>.Instance);
builder.Services.AddSingleton(certService);

// Drop any inherited URL list before configuring the endpoints below.
// ASPNETCORE_URLS (and launchSettings' applicationUrl in dev) set addresses
// that the explicit ListenAnyIP/ListenLocalhost calls then override, and
// Kestrel logs a WARN about it on EVERY startup:
//   "Overriding address(es) 'http://0.0.0.0:5000'. Binding to endpoints
//    defined via IConfiguration and/or UseKestrel() instead."
// Nothing is wrong when that appears, which is the problem: a warning that
// fires on every boot and never means anything trains everyone to ignore the
// warning level. Clearing the setting is safe precisely because those
// addresses were being discarded anyway.
builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);

builder.WebHost.ConfigureKestrel(options =>
{
    if (httpEnabled) {
        // Expose HTTP on the LAN when it's only acting as an HTTPS redirector,
        // otherwise honour the loopback-only default (plaintext off the LAN).
        if (httpBindAny || httpRedirect) options.ListenAnyIP(httpPort);
        else                             options.ListenLocalhost(httpPort);
    }
    if (httpsEnabled) {
        var cert = certService.GetOrCreate();
        options.ListenAnyIP(httpsPort, listen => listen.UseHttps(cert));
    }
    // GX-9: the /api/onnx/save endpoint round-trips raw uint16 pixel
    // bytes for the post-inference image, RGB masters from a modern
    // OSC sensor land around 150 MB (e.g. 6240×4160×3×2). The default
    // 30 MB cap rejects anything bigger than ~2 MP RGB. Also affects
    // /api/editor/upload (user-supplied PNG/TIFF), /api/files/upload
    // (drag-drop into STUDIO library), and /api/onnx/save (the one
    // we actually hit first). 1 GB hard ceiling, generous enough to
    // cover a 16k×16k RGB master uncompressed without being unbounded.
    options.Limits.MaxRequestBodySize = 1L * 1024 * 1024 * 1024;
});

// GX-9: ASP.NET's multipart form parser has its own ceiling
// (FormOptions.MultipartBodyLengthLimit, default 128 MB) layered on
// top of Kestrel's request body limit. Both have to grow together,
// the parser hits its cap first and surfaces a less obvious error
// ("Multipart body length limit exceeded") before Kestrel sees the
// stream. Match the 1 GB Kestrel ceiling.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1L * 1024 * 1024 * 1024;
    o.ValueLengthLimit = int.MaxValue;
});

// Defensive JSON hardening for every minimal-API response and
// WriteAsJsonAsync call. System.Text.Json throws mid-serialization on a
// non-finite double (NaN / +-Infinity), which a single garbage value (a
// plate-solve scale, a mount position, a stat) turns into a 500 for the whole
// endpoint. These converters emit JSON null for non-finite numbers instead,
// which is valid JSON and parses on the JS side. See NINA.Polaris.Json.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new NINA.Polaris.Json.NonFiniteDoubleConverter());
    o.SerializerOptions.Converters.Add(new NINA.Polaris.Json.NullableNonFiniteDoubleConverter());
    o.SerializerOptions.Converters.Add(new NINA.Polaris.Json.NonFiniteFloatConverter());
    o.SerializerOptions.Converters.Add(new NINA.Polaris.Json.NullableNonFiniteFloatConverter());
});

// WINEXIT: never let one BackgroundService take the whole host down. The .NET
// default (BackgroundServiceExceptionBehavior.StopHost) turns any exception that
// escapes a service's ExecuteAsync into a graceful whole-process shutdown -- the
// "Polaris just closed after a minute" field report. A degraded host with one
// service down (and the failure logged) beats a dead one mid-session. The
// escape is still logged by the framework + the crash handler above; the known
// one (the mount-guard COM race) is fixed at its site. NOTE: this does NOT stop
// a native corrupted-state exception, which tears the process down before any
// managed handler runs -- that needs driver-process isolation (see WINEXIT-2).
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Services
// DBGLOG-1/2: ring buffer + ILogger provider that mirrors every
// server-side log call into it. Registered FIRST so other singletons
// that resolve ILogger<T> in their constructor get the bridged logger.
builder.Services.AddSingleton<NINA.Polaris.Services.Logging.LogService>();
builder.Logging.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp =>
    new NINA.Polaris.Services.Logging.LogBufferLoggerProvider(
        sp.GetRequiredService<NINA.Polaris.Services.Logging.LogService>()));
// DBGLOG-9: opt-in disk persistence. Always registered as hosted; the
// service checks profile.LogToDisk per tick so toggling Settings takes
// effect without a restart.
builder.Services.AddHostedService<NINA.Polaris.Services.Logging.LogRotatorService>();
// ASIAIR-style per-session guiding logs: native → PHD2-compatible guide log,
// external PHD2 → copy of PHD2's own log. Opt-out via profile.SaveGuideLogs.
builder.Services.AddHostedService<NINA.Polaris.Services.Logging.GuideSessionLogService>();
// Filter labels belong to the rig, not to the driver: INDI wheels come back
// from a restart advertising "Filter 1..N". Restore them server-side so it
// happens headless and for every client, not only when a browser is open on
// the right page at the right moment.
builder.Services.AddHostedService<FilterNameRestoreService>();
builder.Services.AddSingleton<DiskInstallService>();
builder.Services.AddSingleton<ImageRelayService>();
builder.Services.AddSingleton<CameraStreamService>();
// Server-owned LIVE capture loop — now the only LIVE loop (the LIVE shutter
// always starts/stops this; the browser never drives repeated captures).
builder.Services.AddSingleton<LiveCaptureService>();
// Auxiliary (second) camera capture+save loop, runs alongside LIVE/AUTORUN.
builder.Services.AddSingleton<MultiImagerCaptureService>();
builder.Services.AddSingleton<AuxCaptureService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Planetary.VideoRecordingService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Planetary.PlanetaryStackerService>();
// KC-1: Keep Centered control loop. Toggled from the VIDEO sidebar
// while a planetary stream is running -- pulses N/S/E/W to fight
// drift and keep the planet on the frame center.
builder.Services.AddSingleton<NINA.Polaris.Services.Planetary.KeepCenteredService>();
// LSTR-3: subscribes to LiveStackingService.FrameIntegrated at construction.
// Eagerly resolved alongside PHD2ProfileSyncService below so the
// subscription wires at startup, not on first /api/livestack/triggers/* hit.
builder.Services.AddSingleton<LiveStackTriggersService>();
// REFSUG-1: trend-based refocus suggestion. Listens to the same
// FrameIntegrated event as LSTR-3 but only when RefocusEnabled is
// OFF — covers manual-focuser users who cannot be auto-fired.
// Eager-resolved below alongside LSTR so the subscription is wired
// before the first /api/livestack/start hit.
builder.Services.AddSingleton<RefocusSuggestionService>();
// Auto-shows live camera feed while mount is slewing (no-op when any
// capture surface is active). Singleton + hosted service so the
// background poll loop runs.
builder.Services.AddSingleton<SlewPreviewService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SlewPreviewService>());
// LSPP-2: per-frame calibration helper consumed by LiveStackingService.
// Singleton so the master cache (loaded ushort[] buffers, ~150MB peak)
// survives across all sessions in the process lifetime -- a single
// Reset clears it when LiveStackingService.Reset is called.
builder.Services.AddSingleton<LiveStackPreProcessor>();
builder.Services.AddSingleton<LiveStackingService>();
// Built-in gear simulator: one shared virtual-sky state so the simulated guide
// camera and mount (driver "sim") couple pulse guides to the star field.
builder.Services.AddSingleton<NINA.Polaris.Services.Simulator.Gear.SimGearService>();
builder.Services.AddSingleton<EquipmentManager>();
// Persists native-SDK camera control values (gain/offset/cooler/…) so they
// survive disconnect/reconnect and app restarts; re-applied on connect.
builder.Services.AddSingleton<NativeCameraControlStore>();
builder.Services.AddSingleton<SequenceEngine>();
builder.Services.AddSingleton<NINA.Polaris.Services.Sequencer.SequenceTemplateStore>();
builder.Services.AddSingleton<NINA.Polaris.Services.Workflow.WorkflowStore>();
builder.Services.AddSingleton<NINA.Polaris.Services.Sequencer.AdvancedSequenceEngine>();
builder.Services.AddSingleton<MosaicPlannerService>();
// PLAN mode: compiles ImagingPlans to sequence documents + a hosted runner that
// schedules the start, enforces the end condition, and runs end actions.
builder.Services.AddSingleton<NINA.Polaris.Services.Plan.PlanCompilerService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Plan.PlanRunnerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Polaris.Services.Plan.PlanRunnerService>());
builder.Services.AddSingleton<NINA.Polaris.Services.Plugins.PluginLoaderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Polaris.Services.Plugins.PluginLoaderService>());
builder.Services.AddSingleton<SkyCatalogService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.AstapSolver>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.PlateSolve3Solver>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.AstrometryNetOnlineSolver>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.AstrometryNetLocalSolver>();
builder.Services.AddSingleton<PlateSolveService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.PlateSolveProgressService>();
// Server-authoritative current-exposure tracker so the per-frame countdown
// ("Xs of Ys") survives a client reconnect across every capture context.
builder.Services.AddSingleton<CaptureProgressService>();
// Shared "wait for the main camera to be ready" gate, so AUTORUN / ADV / LIVE all
// pause for a driver-restart recovery instead of failing fast. Accessor form so
// it's testable and carries no hard edge to EquipmentManager.
builder.Services.AddSingleton(sp => new CameraReadyGate(
    () => sp.GetRequiredService<EquipmentManager>().Camera,
    sp.GetRequiredService<ILogger<CameraReadyGate>>()));
// Walks the cooler setpoint at a controlled °C/min, in both directions, so a
// fast plunge can't condense dew on the sensor window. Singleton: only one ramp
// may own the setpoint, and a new request has to cancel the one in flight
// rather than race it.
builder.Services.AddSingleton<CoolingRampService>();
// Server-side progress + ETA for the tiled classical RL deconvolution.
builder.Services.AddSingleton<DeconProgressService>();
builder.Services.AddSingleton<SlewCenterService>();
// "Center on Sun/Moon/planet" — solve-near-and-offset (Mode A) for
// solar-system objects plate solving can't handle directly.
builder.Services.AddSingleton<SolarSystemCenterService>();
builder.Services.AddSingleton<ProfileService>();
// AUTH-1: local-server auth (password + session store + rate limit).
// Middleware that consumes this is wired in AUTH-2.
builder.Services.AddSingleton<NINA.Polaris.Services.Auth.AuthService>();
// CLOCK-1: wraps `timedatectl set-time` so the browser can nudge the
// Pi's wall clock when the host is offline (no NTP) + has no RTC.
// Linux only; on Windows the service refuses gracefully + the UI
// banner explains.
builder.Services.AddSingleton<ClockSyncService>();
// Read-only host self-check behind GET /api/system/diagnostics. Composes the
// per-feature services above and adds the OS-level checks nobody else makes
// (units enabled, udev/polkit rules, root filesystem grown, device identity).
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddSingleton<PowerService>();
builder.Services.AddSingleton<ImageWriterService>();
// Auto-push saved images to network storage (SMB / SFTP / mounted path).
// Background consumer subscribes to ImageWriterService.ImageSaved; the
// factory hands out a fresh connection-owning adapter per connect cycle.
builder.Services.AddSingleton<NINA.Polaris.Services.Storage.IStorageTargetFactory,
    NINA.Polaris.Services.Storage.StorageTargetFactory>();
builder.Services.AddSingleton<StoragePushService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StoragePushService>());
builder.Services.AddSingleton<UsbDriveWatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UsbDriveWatcherService>());
builder.Services.AddSingleton<ScriptRuntimeService>();
builder.Services.AddSingleton<ScriptRunnerService>();
builder.Services.AddSingleton<PHD2Client>();
// Native in-process autoguider (drop-in alternative to PHD2, per-rig).
builder.Services.AddSingleton<NativeGuider>();
builder.Services.AddSingleton<ActiveGuiderProvider>();
// Multi-camera synchronized dither coordinator (main + aux on one mount).
builder.Services.AddSingleton<DitherBarrier>();
builder.Services.AddSingleton<PHD2ProcessManager>();
builder.Services.AddHostedService<PHD2AutoStartService>();

// SIM-2: built-in equipment simulator (indi_simulator_* on Linux/Mac,
// Alpaca Omni Simulator on Windows). Both backends register; the
// orchestrator picks the supported one at startup via IsSupported.
// AutoStart service handles the launch-on-boot toggle + periodic
// health probe.
// Both backends register; SimulatorService picks the first one that
// reports IsSupported = true on the current OS. Order matters only
// when two backends claim the same OS (none do today). Backends not
// matching the host OS still construct but their IsSupported short-
// circuits Launch / Detect into safe no-ops.
builder.Services.AddSingleton<NINA.Polaris.Services.Simulator.ISimulatorBackend,
    NINA.Polaris.Services.Simulator.IndiSimulatorBackend>();
builder.Services.AddSingleton<NINA.Polaris.Services.Simulator.ISimulatorBackend,
    NINA.Polaris.Services.Simulator.AscomSimulatorBackend>();
builder.Services.AddSingleton<NINA.Polaris.Services.Simulator.SimulatorService>();
builder.Services.AddHostedService<NINA.Polaris.Services.Simulator.SimulatorAutoStartService>();
// Listens to ProfileService.EquipmentProfileActivated; keep singleton so
// the event subscription survives request scopes.
builder.Services.AddSingleton<PHD2ProfileSyncService>();
builder.Services.AddSingleton<PHD2CalibrationOrchestrator>();
// xpra-hosted PHD2 GUI session (Linux only, service short-circuits on
// other OSes). Register as singleton AND hosted service so it shows up
// in DI for endpoint handlers + runs its background loop.
builder.Services.AddSingleton<Phd2GuiSessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Phd2GuiSessionService>());
// PH2VNC-1: Windows sibling of Phd2GuiSessionService. Detects
// TightVNC, monitors its Windows service + listening port, and
// powers the GUIDE tab's "PHD2 GUI" iframe on Windows hosts via
// the noVNC HTML5 client + the /phd2-vnc-ws TCP bridge. Idle no-op
// on non-Windows so the Linux build incurs zero overhead.
builder.Services.AddSingleton<Phd2VncSessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Phd2VncSessionService>());
// INDI-WEB-1: indi-web (indiwebmanager) lifecycle manager. Same
// dual-registration shape as Phd2GuiSession so endpoint handlers
// resolve the singleton AND the background loop (auto-start +
// health probe) runs.
builder.Services.AddSingleton<IndiWebManagerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndiWebManagerService>());
// INDI profile assistant: stateless sysfs reader behind /api/indi/detect.
// Singleton only because it has no per-request state; it holds no handles.
builder.Services.AddSingleton<UsbScanService>();
// Wedged-INDI-driver watchdog: on repeated BLOB timeouts, restart just that
// driver through indi-web (a device reconnect can't fix a stuck driver). Dual
// registration so the hosted StartAsync subscribes to IndiClient.BlobTimeout.
builder.Services.AddSingleton<IndiDriverWatchdogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndiDriverWatchdogService>());
// WIFI-1: NetworkManager-based WiFi mode switch (Hotspot ↔ Station).
// Same dual-registration shape as Phd2Gui / IndiWeb. Linux-only;
// gracefully short-circuits on Windows / macOS via IsSupportedOs.
builder.Services.AddSingleton<NetworkManagerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkManagerService>());
// CANOPUS: local "On this server (SBC)" assistant backend. The model/runtime
// downloader is a plain singleton; the server (llama-server + agent child
// processes) uses the same dual registration as IndiWeb so the endpoints resolve
// the singleton AND the hosted auto-start / health loop runs.
builder.Services.AddSingleton<NINA.Polaris.Services.External.CanopusModelService>();
builder.Services.AddSingleton<NINA.Polaris.Services.External.CanopusServerService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<NINA.Polaris.Services.External.CanopusServerService>());
// YARP direct forwarder, used by the /phd2-gui/* AND /indi-web/*
// reverse-proxies below to bridge browser ↔ embedded webapp.
// Includes WebSocket upgrade support, which xpra-html5 needs for
// the pixel stream and indi-web can use for live driver state.
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton<AutoFocusService>();
// UPDGATE: one place that knows whether the host is mid-session, so an
// action that restarts the process can refuse instead of finding out.
builder.Services.AddSingleton<HostActivityService>();
// STORAGE-1: find a data disk and move the captures onto it.
builder.Services.AddSingleton<StorageSetupService>();
// WIRE-1: a cold load is 33 HTML/CSS/JS assets, 5.18 MB uncompressed. Measured
// on a Q6A over its onboard USB wifi, that is 10.3 s of transfer; gzip takes
// the same bytes to 1.26 MB. Kestrel ships no compression by default, and
// EnableForHttps defaults to FALSE, so the browser's "Accept-Encoding: br,
// gzip" was being answered with 2.1 MB of plain text.
//
// Enabling it for HTTPS is what re-opens the BREACH question. It applies to a
// response that mixes a secret with attacker-chosen input; the token here is
// issued once by /api/auth and travels in a request header afterwards, and
// that one path is excluded from compression below. Everything else we
// compress is either a static asset or telemetry the caller already has.
//
// Optimal, not Fastest. Brotli's Fastest is quality 1 and left app.js at
// 737 KB; Optimal is quality 4 and gets it to 538 KB for about 30 ms of CPU
// per request here, so call it ~120 ms on the SBC. That cost is only paid on a
// cold load: afterwards the browser revalidates with the ETag and gets a 304
// with no body. Measured over the whole 33-asset page: 5.18 MB -> 1.24 MB.
builder.Services.AddResponseCompression(o => {
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    // The default list omits application/javascript and image/svg+xml, which
    // between them are most of the payload. Images, video and FITS are
    // deliberately absent: re-compressing them burns CPU to make them slightly
    // bigger.
    //
    // application/wasm IS here, and an earlier version of this comment claimed
    // the opposite. The wasm bundle is not pre-compressed: dotnet.native.wasm
    // is 8.31 MB raw and 2.74 MB Brotli'd, so leaving it out costs a user on a
    // slow link far more than the ~130 ms of CPU it takes to compress the whole
    // 14 MB bundle. That cost is paid once per cold load, because afterwards the
    // browser revalidates by ETag and gets a 304 with no body.
    o.MimeTypes = new[] {
        "text/html", "text/css", "text/plain", "text/javascript",
        "application/javascript", "application/json", "application/manifest+json",
        "application/xml", "text/xml", "image/svg+xml", "application/wasm"
    };
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Optimal);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Optimal);
// The once-per-second /ws/status frame. Each contributor owns its own blocks
// and declares which ones; the builder puts the envelope around them and
// refuses to start if two of them claim the same key. Adding a status field is
// a change to one of these files and nothing else.
builder.Services.AddSingleton<NINA.Polaris.WebSocket.Status.IStatusContributor,
    NINA.Polaris.WebSocket.Status.EquipmentStatusContributor>();
builder.Services.AddSingleton<NINA.Polaris.WebSocket.Status.IStatusContributor,
    NINA.Polaris.WebSocket.Status.GuidingStatusContributor>();
builder.Services.AddSingleton<NINA.Polaris.WebSocket.Status.IStatusContributor,
    NINA.Polaris.WebSocket.Status.LiveStackStatusContributor>();
builder.Services.AddSingleton<NINA.Polaris.WebSocket.Status.IStatusContributor,
    NINA.Polaris.WebSocket.Status.CaptureStatusContributor>();
builder.Services.AddSingleton<NINA.Polaris.WebSocket.Status.IStatusContributor,
    NINA.Polaris.WebSocket.Status.SequencingStatusContributor>();
builder.Services.AddSingleton<NINA.Polaris.WebSocket.Status.IStatusContributor,
    NINA.Polaris.WebSocket.Status.ProcessingStatusContributor>();
builder.Services.AddSingleton<NINA.Polaris.WebSocket.Status.IStatusContributor,
    NINA.Polaris.WebSocket.Status.HostStatusContributor>();
builder.Services.AddSingleton<NINA.Polaris.WebSocket.StatusPayloadBuilder>();
builder.Services.AddSingleton<MeridianFlipService>();
// Auto meridian flip during LIVE stacking (polls HA, flips when due).
builder.Services.AddHostedService<MeridianFlipAutoLiveService>();
// Fail-safe mount watchdog: anti cable-wrap (past-meridian limit) + guiding
// circuit breaker. Singleton so the meridian-flip endpoint can read its trip
// state; also hosted so its poll loop runs.
builder.Services.AddSingleton<MountSafetyGuardService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MountSafetyGuardService>());

// Wind guard: watches the guide stream for an error that has run away and is
// not coming back, and restarts guiding when it finds one. Hosted because it
// has to be listening before anything asks it a question.
builder.Services.AddSingleton<GuideRunawayGuard>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GuideRunawayGuard>());
builder.Services.AddSingleton<FlatWizardService>();
// PA-1: TPPA orchestrator. Singleton because it holds CurrentJob
// (consumed by StatusStreamHandler) + the in-flight CancellationTokenSource.
builder.Services.AddSingleton<PolarAlignmentService>();
// PA-6: TPPA target suggester. Pure read against the catalog + altitude
// helpers, no state, fine as a singleton.
builder.Services.AddSingleton<PolarTppaTargetService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Alpaca.AlpacaDiscovery>();
builder.Services.AddSingleton<NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache>();
builder.Services.AddSingleton<StellariumClient>();
builder.Services.AddSingleton<AltitudeService>();
builder.Services.AddSingleton<GeocodingService>();
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddSingleton<CelestialImageService>();
builder.Services.AddSingleton<CometEphemerisService>();
builder.Services.AddSingleton<CometElementsUpdater>();
// Keeps the comet elements current when the host has internet; silent and
// non-blocking when it does not (the usual case in the field).
builder.Services.AddHostedService<CometElementsRefreshWorker>();
builder.Services.AddSingleton<TonightsBestService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.FrameLibraryService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.FrameProcessingService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.MasterFrameService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.CalibrationService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.BatchStackingService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.FrameGradingService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.SpccDatabase>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.SpccService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.ChannelCombineService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.StarColorRepairService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.ColorCalibrationService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Sky.ApassCatalog>();
builder.Services.AddSingleton<NINA.Polaris.Services.Sky.ApassDownloadService>();
// CAT-2: bundled expanded DSO catalog (NGC/IC/M/C/Arp/Sh2/HCG/AGC,
// ~14.5k objects in wwwroot/catalogs/dso/dso.db). SkyCatalogService
// delegates to it when IsAvailable, falls back to the hardcoded
// 150-object legacy list when missing.
builder.Services.AddSingleton<NINA.Polaris.Services.Sky.DsoCatalog>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.FrameOperationsService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Editor.ImageEditService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Editor.ImageBlendService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Editor.EditSidecarStore>();
builder.Services.AddSingleton<NINA.Polaris.Services.Onnx.OnnxModelRegistry>();
builder.Services.AddSingleton<NINA.Polaris.Services.Onnx.ModelDownloadService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.SolverDatabaseService>();
// THUMBPACK-4: on-demand downloader for the converted ncnn GPU-Vulkan models
// (excluded from the package). Extracts into the writable models root's ncnn/
// subtree, where the existing NcnnInferenceService resolver already looks.
builder.Services.AddSingleton<NINA.Polaris.Services.External.NcnnModelPackService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Onnx.OnnxFileService>();
// RKNN: host-side NPU acceleration for GraXpert AI on Rockchip RK3588.
// Injected (optionally) into GraXpertService; no-op when no NPU is present.
builder.Services.AddSingleton<NINA.Polaris.Services.Rknn.RknnInferenceService>();

// QNN: host-side NPU acceleration for GraXpert AI on the Qualcomm Hexagon
// (QCS6490 / Radxa Dragon Q6A). Counterpart to the Rockchip path; no-op when
// the QAIRT runtime / cDSP isn't present. Mutually exclusive by hardware.
builder.Services.AddSingleton<NINA.Polaris.Services.Qnn.QnnInferenceService>();

// NCNN: open, vendor-neutral Vulkan-GPU acceleration for GraXpert AI (BGE +
// denoise v2). Runs on the Adreno 643 (Q6A / Turnip), Mali, etc. Injected
// (optionally) into GraXpertService; no-op when libncnn/Vulkan aren't present.
builder.Services.AddSingleton<NINA.Polaris.Services.Ncnn.NcnnInferenceService>();

// OCL: SBC GPU acceleration for classic image kernels via OpenCL. Resolve the
// OpenCL backend when the board exposes an OpenCL ICD loader (Adreno on the
// Radxa Dragon Q6A, Mali on RK3588, ...), else the always-available CPU backend.
// Every kernel falls back to the CPU per-call too, so this is a pure no-op on
// boards without OpenCL (e.g. Raspberry Pi).
builder.Services.AddSingleton<NINA.Image.Gpu.IGpuCompute>(sp => {
    if (NINA.Polaris.Services.OpenCl.OpenClRuntime.IsAvailable) {
        var log = sp.GetRequiredService<ILogger<NINA.Polaris.Services.OpenCl.OpenClGpuCompute>>();
        var impl = new NINA.Polaris.Services.OpenCl.OpenClGpuCompute(log);
        // Honour the persisted Settings toggle at startup.
        impl.Enabled = sp.GetService<ProfileService>()?.Active?.UseGpuOpenCl ?? true;
        return impl;
    }
    return new NINA.Image.Gpu.CpuGpuCompute();
});
builder.Services.AddSingleton<FileBrowserService>();
builder.Services.AddSingleton<NINA.Polaris.Services.External.SirilService>();
builder.Services.AddSingleton<NINA.Polaris.Services.External.GraXpertService>();
// Self-update (SBC .deb installs): checks GitHub releases + installs the new
// .deb on request. AddHttpClient gives it an IHttpClientFactory.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<NINA.Polaris.Services.External.UpdateService>();
builder.Services.AddSingleton<NINA.Polaris.Services.CropService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PostProcess.ScnrService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PostProcess.StretchService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PostProcess.CosmeticService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PostProcess.StarReductionService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PostProcess.WaveletService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PostProcess.DustRemovalService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PostProcess.TonalService>();
builder.Services.AddSingleton<NINA.Polaris.Services.DeconvolutionService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.FrameAnalysisService>();
// TLS-2: DuckDNS HTTP client for setting TXT records during ACME
// DNS-01 challenge. Used by LetsEncryptService (TLS-4); also exposed
// directly via /api/tls/letsencrypt/test-dns (TLS-5) so the user
// can sanity-check token+domain without burning a Let's Encrypt
// rate-limit budget.
builder.Services.AddSingleton<NINA.Polaris.Services.Tls.DuckDnsClient>();
// Host CPU + memory sampler. AddResourceMonitoring wires the
// platform-specific provider (Job Objects on Windows, cgroups on
// Linux). HostMetricsService loops in the background, exposes the
// latest snapshot via the Latest property which StatusStreamHandler
// folds into the per-second WS broadcast.
builder.Services.AddResourceMonitoring();
builder.Services.AddSingleton<HostMetricsService>();
// BENCH: on-demand hardware benchmark (Settings -> Hardware Benchmark).
// Not a hosted service; runs only when the user clicks Run. The results
// store persists run history under {ProfileService.DataDir}/benchmarks/.
builder.Services.AddSingleton<BenchmarkResultsStore>();
builder.Services.AddSingleton<BenchmarkService>();
// On-demand DSS Color HiPS downloader (Settings -> Sky imagery). Pulls the
// higher HEALPix orders (4 ~110 MB, 5 ~400 MB) into the bundled skydata dir
// so the SKY map reaches ASIAIR-grade detail without shipping them in git.
builder.Services.AddSingleton<NINA.Polaris.Services.External.DssDownloadService>();
// THUMBPACK: on-demand downloader for the full DSO thumbnail set, excluded from
// the package to keep it slim. Serves from the download dir + bundled core subset.
builder.Services.AddSingleton<NINA.Polaris.Services.External.DsoThumbPackService>();
// Camera sensor analysis (e/ADU, read noise, full well, dynamic range
// vs gain via the photon-transfer-curve method). On-demand, like the
// benchmark; launched from the Equipment camera card.
builder.Services.AddSingleton<SensorAnalysisStore>();
builder.Services.AddSingleton<SensorAnalysisService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostMetricsService>());
builder.Services.AddSingleton<MdnsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MdnsService>());
// Server-pushed toast channel + boot-time auto-connect for INDI /
// Alpaca / active-rig equipment. The auto-connect service is gated
// on profile.AutoConnectOnStartup; if the toggle is off, RunAsync
// is never scheduled and there's zero runtime cost.
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHostedService<HardwareAutoConnectService>();
builder.Services.AddSingleton<RelayClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayClient>());
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var host = config.GetValue("Indi:Host", "localhost")!;
    var port = config.GetValue("Indi:Port", 7624);
    var client = new IndiClient(host, port);
    // Wire DBGLOG-2 bridge so every INDI write (newNumberVector /
    // newSwitchVector / newTextVector) shows up in the LOG panel,
    // and "property name doesn't exist on this device" warnings
    // surface when the driver doesn't advertise the property we
    // tried to write.
    client.DiagLogger = sp.GetRequiredService<ILoggerFactory>()
        .CreateLogger("NINA.INDI.IndiClient");
    return client;
});

var app = builder.Build();

// The cert had to be built before this point, so it ran against a NullLogger
// and everything it decided about the user's certificate was thrown away.
// Replay it now, into the real pipeline: journald gets it, and so does the
// DEBUG panel. Regenerating a self-signed cert voids every stored browser and
// app exception, and until now it did so without leaving a single line behind.
certService.ReplayLogInto(app.Services.GetRequiredService<ILogger<NINA.Polaris.Services.SelfSignedCertService>>());

// Seed the built-in "Standard" Auto Workflow on first run (one-time, marker-
// guarded so a user-deleted default stays deleted). Best-effort.
try { app.Services.GetRequiredService<NINA.Polaris.Services.Workflow.WorkflowStore>().SeedDefaults(); }
catch { /* non-fatal */ }

// HTTP -> HTTPS redirect. Runs first so any plaintext request (LAN client that
// typed http://) is bounced to the HTTPS endpoint, preserving host + path +
// query. WebSocket upgrades only happen after the page loads over HTTPS, so
// they're unaffected.
if (httpRedirect) {
    app.Use(async (ctx, next) => {
        // NEVER redirect loopback. The plaintext HTTP port is the documented
        // INTERNAL channel: the relay tunnel replays browser requests against
        // http://127.0.0.1:{Server:Http:Port}, and host-local scripts
        // (polarispy) call the same API there. Bouncing those to HTTPS handed
        // them a 307 instead of the real response - it silently broke the relay
        // and produced the "POST /api/script/.../dialog -> HTTP 307" script
        // failure. Loopback is already auth-exempt (AuthMiddleware), so this
        // keeps the two consistent.
        var remote = ctx.Connection.RemoteIpAddress;
        var isLoopback = remote == null || System.Net.IPAddress.IsLoopback(remote);
        if (!ctx.Request.IsHttps && !isLoopback) {
            var host = ctx.Request.Host.Host;
            var portSuffix = httpsPort == 443 ? "" : $":{httpsPort}";
            var location = $"https://{host}{portSuffix}{ctx.Request.Path}{ctx.Request.QueryString}";
            ctx.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            ctx.Response.Headers.Location = location;
            return;
        }
        await next();
    });
}

// WIRE-1: compress before anything that writes a body, so the static handlers,
// the reverse proxies and the API all inherit it. /api/auth is carved out: it
// is the one response that carries a freshly minted token, and keeping it
// uncompressed costs nothing (a few hundred bytes, once per login) while
// leaving no BREACH argument to have. The middleware skips a response that
// already declares a Content-Encoding, so the proxied /sky and /phd2-gui
// bodies are not compressed twice.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase),
    b => b.UseResponseCompression());

// Eagerly resolve PHD2ProfileSyncService so its constructor wires the
// ProfileService.EquipmentProfileActivated event subscription. Without
// this, the singleton would only be constructed on first /api/guider/*
// request and rig activations before that would skip auto-sync.
app.Services.GetRequiredService<PHD2ProfileSyncService>();
// Same eager-resolve rationale: LiveStackTriggersService subscribes to
// LiveStackingService frame events in its constructor. Without this
// the singleton would only be constructed when /api/livestack/triggers
// is hit, and any frames stacked before then would skip auto-refocus
// / auto-recenter evaluation.
app.Services.GetRequiredService<LiveStackTriggersService>();
// REFSUG-1: same eager-resolve rationale. RefocusSuggestionService
// hooks LiveStackingService.FrameIntegrated in its constructor and
// must be alive before the first live-stack frame arrives.
app.Services.GetRequiredService<RefocusSuggestionService>();


// INDIROB-3: sync the active rig's PreConnectDelayMsByDevice dict
// into IndiClient.PreConnectDelaysMs so ConnectDeviceAsync honours
// per-device sleep windows before sending CONNECTION. Runs on
// startup and on every rig switch — operators with multiple
// rigs (mini-PC + Pi, different mounts) can have different
// settling needs per setup.
{
    var indi = app.Services.GetRequiredService<IndiClient>();
    var profiles = app.Services.GetRequiredService<ProfileService>();
    var indiLogger = app.Services.GetRequiredService<ILogger<IndiClient>>();

    void ApplyPreConnectDelays(string trigger) {
        var src = profiles.ActiveEquipmentProfile?.PreConnectDelayMsByDevice
                  ?? new Dictionary<string, int>();
        indi.PreConnectDelaysMs.Clear();
        foreach (var (k, v) in src) {
            if (v > 0) indi.PreConnectDelaysMs[k] = v;
        }
        if (indi.PreConnectDelaysMs.Count > 0) {
            indiLogger.LogInformation(
                "INDI per-device pre-connect delays applied ({Trigger}): {Pairs}",
                trigger,
                string.Join(", ", indi.PreConnectDelaysMs.Select(kv => $"{kv.Key}={kv.Value}ms")));
        }
    }
    ApplyPreConnectDelays("startup");
    profiles.EquipmentProfileActivated += _ => ApplyPreConnectDelays("rig-switch");
}

// SWE-3-bugfix: strip CSP for /sky/* responses. The ASP.NET dev-time
// browser refresh middleware injects a strict Content-Security-Policy
// header (no 'unsafe-eval', no 'wasm-unsafe-eval') into HTML responses.
// stellarium-web-engine's Emscripten runtime calls addFunction() during
// init, which internally uses `new Function(...)` to build callback
// trampolines, CSP blocks that and the engine never reaches onReady,
// so addDataSource never fires and the sky stays empty with no Network
// requests to skydata at all (matches the symptom we hit).
//
// Easiest correct fix: remove the CSP header entirely for the /sky/
// sub-app via Response.OnStarting (which runs AFTER all upstream
// middlewares have set their headers and BEFORE the body streams).
// The iframe is sandboxed by the parent's sandbox attribute already,
// so dropping CSP on /sky/ doesn't widen the attack surface, the
// surface is bounded by the iframe sandbox.
app.Use(async (ctx, next) => {
    if (ctx.Request.Path.StartsWithSegments("/sky")) {
        ctx.Response.OnStarting(() => {
            ctx.Response.Headers.Remove("Content-Security-Policy");
            ctx.Response.Headers.Remove("Content-Security-Policy-Report-Only");
            return Task.CompletedTask;
        });
    }
    // PH2X-9: xpra's HTML5 client emits its own framing headers
    // (X-Frame-Options: SAMEORIGIN / DENY, a CSP with frame-ancestors, and
    // Cross-Origin-Resource-Policy: same-origin). HttpTransformer.Default
    // forwards them verbatim, so the embedded /phd2-gui iframe dies with
    // net::ERR_BLOCKED_BY_RESPONSE and the Relaunch/Restart buttons look
    // like no-ops (they work, but the reloaded iframe can never render).
    // Strip all three so the same-origin iframe can embed. The proxy already
    // sits behind Polaris auth and xpra binds to 127.0.0.1 only.
    else if (ctx.Request.Path.StartsWithSegments("/phd2-gui")) {
        ctx.Response.OnStarting(() => {
            ctx.Response.Headers.Remove("X-Frame-Options");
            ctx.Response.Headers.Remove("Content-Security-Policy");
            ctx.Response.Headers.Remove("Content-Security-Policy-Report-Only");
            ctx.Response.Headers.Remove("Cross-Origin-Resource-Policy");
            return Task.CompletedTask;
        });
    }
    await next();
});

app.UseDefaultFiles();
// CLST-2: the WASM AppBundle includes extensions ASP.NET Core's
// default FileExtensionContentTypeProvider doesn't know about
// (.dat for ICU data, .blat / .dll for Brotli-compressed managed
// assemblies). Without these mappings the static-file middleware
// returns 404, the browser's SRI check sees an empty body, and
// the dotnet runtime fails to boot with cascading "integrity
// checks failed" errors. ServeUnknownFileTypes scoped via a
// custom content-type map is cleaner than blanket allowing
// everything, keeps obscure extensions outside /js/wasm/ still 404.
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".dat"] = "application/octet-stream";
contentTypes.Mappings[".blat"] = "application/octet-stream";
contentTypes.Mappings[".dll"] = "application/octet-stream";
contentTypes.Mappings[".pdb"] = "application/octet-stream";
contentTypes.Mappings[".webcil"] = "application/octet-stream";
contentTypes.Mappings[".wasm"] = "application/wasm";
contentTypes.Mappings[".br"] = "application/octet-stream";
contentTypes.Mappings[".gz"] = "application/octet-stream";
// SWE-3-bugfix: stellarium-web-engine HiPS tile pyramids ship
// as .eph (binary ephemeris) and the per-survey `properties`
// metadata files have NO extension at all. The default static
// middleware refuses both, silently 404s and the engine then
// renders an empty sky with no console error. Map .eph here and
// add a scoped ServeUnknownFileTypes pass below for the no-ext
// `properties` files inside /sky/data/skydata/.
contentTypes.Mappings[".eph"] = "application/octet-stream";

// Downloaded DSS tiles (orders 4-5) live in a WRITABLE data dir, not in the
// read-only install wwwroot. Serve them FIRST at the exact request path the
// sky engine fetches, so a downloaded high-order tile wins; anything not
// present there falls through (next()) to the bundled baseline (orders 0-3)
// in the wwwroot passes below.
//
// Served by a hand-rolled middleware doing a plain stream copy — NOT
// UseStaticFiles. On the Pi the static-file handler reset the connection
// ("Empty reply from server", no logged exception) when serving this
// PhysicalFileProvider rooted under /home, most likely a sendfile / OnStarting
// edge case. A manual File.OpenRead + CopyToAsync sidesteps sendfile and the
// static middleware entirely, with explicit error handling. Runs before the
// auth gate, same as the wwwroot static handlers, so tiles load without a token.
{
    var dssDownload = app.Services.GetRequiredService<NINA.Polaris.Services.External.DssDownloadService>();
    var dssRoot = Path.GetFullPath(dssDownload.DownloadDir);
    try { Directory.CreateDirectory(dssRoot); } catch { /* non-fatal */ }
    const string dssPrefix = "/sky/data/skydata/surveys/dss/";
    app.Use(async (ctx, next) => {
        var path = ctx.Request.Path.Value;
        if (path == null
            || !path.StartsWith(dssPrefix, StringComparison.Ordinal)
            || !HttpMethods.IsGet(ctx.Request.Method)) {
            await next();
            return;
        }
        var rel = Uri.UnescapeDataString(path.Substring(dssPrefix.Length));
        // Path-traversal guard: resolve and confirm the result stays under root.
        var full = Path.GetFullPath(Path.Combine(dssRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(dssRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(full)) {
            await next();   // not a downloaded tile -> let the bundled baseline try
            return;
        }
        try {
            var ext = Path.GetExtension(full).ToLowerInvariant();
            ctx.Response.ContentType = ext is ".jpg" or ".jpeg" ? "image/jpeg"
                : ext == ".png" ? "image/png"
                : ext == ".fits" ? "application/fits"
                : "application/octet-stream";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=604800";   // 7 days
            var fi = new FileInfo(full);
            ctx.Response.ContentLength = fi.Length;
            await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, useAsync: true);
            await fs.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        } catch (OperationCanceledException) {
            /* client went away mid-tile — normal during fast panning */
        } catch (Exception ex) {
            app.Logger.LogWarning(ex, "Failed to serve downloaded DSS tile {Path}", full);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 500;
        }
    });
}

// THUMBPACK: serve /sky/data/skydata/dso-thumbs/{slug}.jpg from the DOWNLOADED
// pack (data dir) first, then the bundled CORE subset (wwwroot/dso-thumbs-core).
// The full dso-thumbs/ dir is excluded from publish, so on a packaged install
// this middleware is the only thing that answers these URLs — a hit on a
// downloaded or curated thumb, a fall-through (404) otherwise. In a source-tree
// dev run the full dir still exists in wwwroot, so a miss here falls through to
// UseStaticFiles and serves it — dev sees every thumb without downloading.
{
    var pack = app.Services.GetRequiredService<NINA.Polaris.Services.External.DsoThumbPackService>();
    var packRoot = Path.GetFullPath(pack.PackDir);
    var coreRoot = Path.GetFullPath(pack.CoreDir);
    const string thumbPrefix = "/sky/data/skydata/dso-thumbs/";
    app.Use(async (ctx, next) => {
        var path = ctx.Request.Path.Value;
        if (path == null
            || !path.StartsWith(thumbPrefix, StringComparison.Ordinal)
            || !HttpMethods.IsGet(ctx.Request.Method)) {
            await next();
            return;
        }
        // Slug only (no subdirs); sanitise against traversal, then resolve
        // pack -> core.
        var slug = Path.GetFileNameWithoutExtension(
            Uri.UnescapeDataString(path.Substring(thumbPrefix.Length)));
        var full = pack.Resolve(slug ?? "");
        if (full == null) { await next(); return; }   // dev: static serves the full dir; prod: 404
        // Defensive: Resolve builds paths under packRoot/coreRoot, but confirm.
        var resolved = Path.GetFullPath(full);
        if (!resolved.StartsWith(packRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !resolved.StartsWith(coreRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            await next();
            return;
        }
        try {
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=604800";   // 7 days
            var fi = new FileInfo(resolved);
            ctx.Response.ContentLength = fi.Length;
            await using var fs = new FileStream(resolved, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, useAsync: true);
            await fs.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        } catch (OperationCanceledException) {
            /* client navigated away — normal */
        } catch (Exception ex) {
            app.Logger.LogWarning(ex, "Failed to serve DSO thumb {Path}", resolved);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 500;
        }
    });
}

app.UseStaticFiles(new StaticFileOptions {
    ContentTypeProvider = contentTypes,
    // Force the browser to revalidate every cached asset on every
    // page load via If-None-Match (ETag) instead of relying on
    // its heuristic freshness lifetime. Without this, ASP.NET's
    // default static-file middleware sets no Cache-Control header
    // at all, and the browser's heuristic cache can hold onto a
    // stale index.html / app.js for hours after a deploy -- the
    // operator updates the .deb, refreshes, and still sees the
    // old UI because nothing told the browser to check.
    //
    // "no-cache" is a misnomer: it does NOT prevent caching. It
    // means "cache freely, but ALWAYS revalidate with the server
    // (sending If-None-Match) before serving the cached copy".
    // Server responds 304 Not Modified if the ETag still matches
    // (cheap, ~20-byte response) so the bandwidth cost is
    // negligible and the user always sees the freshest deployed
    // code.
    //
    // The /sky/data/ skydata HiPS tiles and the vendored libs
    // under /js/lib/ are big AND essentially immutable -- we
    // exempt those paths with a longer cache so we don't hit the
    // server for hundreds of revalidations per page load. They
    // get a 7-day max-age which is plenty short for the rare
    // upstream update + a hard refresh to recover from.
    //
    // /js/wasm/ used to be on that list and must NOT be. Those files are not
    // independent assets: dotnet.boot.js carries a SHA-256 for every other file
    // in the bundle and the runtime refuses any file whose hash disagrees. A
    // 7-day cache with no revalidation lets the parts drift apart, because the
    // browser evicts cache entries individually and by size: after an update,
    // the 8.3 MB dotnet.native.wasm gets evicted and re-fetched from the new
    // build while the 2 KB dotnet.boot.js is still served from the old one. The
    // integrity check then fails on exactly the files that changed in the
    // release, the runtime never boots, and the page never finishes loading.
    // That is a field report, not a hypothetical, and it survived hard
    // refreshes for days. Revalidation costs a conditional request per file and
    // no body when nothing changed; a bundle that must be internally consistent
    // does not get to skip it.
    // The rule itself lives in StaticAssetCachePolicy, where it is covered by
    // tests: getting it wrong does not slow the app down, it stops the app
    // loading, and that is worth more than a lambda.
    OnPrepareResponse = ctx => {
        ctx.Context.Response.Headers["Cache-Control"] =
            NINA.Polaris.Services.StaticAssetCachePolicy.For(
                ctx.Context.Request.Path.Value ?? "");
    }
});

// SWE-3-bugfix continued: second pass scoped to the Stellarium
// skydata directory only, with ServeUnknownFileTypes=true so the
// extensionless `properties` files (one per survey/landscape/
// skyculture) get a Content-Type and don't 404. Scoped to the
// skydata path so this never accidentally serves obscure
// extensionless files from elsewhere in wwwroot.
app.UseStaticFiles(new StaticFileOptions {
    RequestPath = "/sky/data",
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(builder.Environment.WebRootPath, "sky", "data")),
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

// CANOPUS: serve the open chat client for the "On this device" tier same-origin
// from the app's copy of canopus/client. agent.js + provider-local.js run IN THE
// BROWSER against the user's own local LLM server (Ollama / LM Studio / llama.cpp).
// Public open-source assets, no secrets; tool execution is still gated at the
// Polaris host bridge. Exists-guarded so a build without the copy doesn't crash.
var canopusClientDir = Path.Combine(AppContext.BaseDirectory, "canopus", "client");
if (Directory.Exists(canopusClientDir)) {
    app.UseStaticFiles(new StaticFileOptions {
        RequestPath = "/canopus-client",
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(canopusClientDir),
        ServeUnknownFileTypes = true,
        DefaultContentType = "application/octet-stream"
    });
}

// AUTH-2: gate /api/*, /ws/*, /phd2-gui/*, /indi-web/*, /sky/*
// behind the bearer token issued by AuthService. /api/auth/* and
// /api/system/version are exempt. Loopback (127.0.0.1/::1) and the
// AuthEnabled=false toggle bypass too. The login page itself + every
// static asset (CSS, JS, images, fonts) live outside the gated
// prefixes so they load without a token, the JS then drives the
// status -> wizard/login/app boot flow.
//
// Order: AFTER UseStaticFiles (so wwwroot assets terminate first)
// and BEFORE UseWebSockets / the /sky+/phd2-gui reverse proxies
// / endpoint mapping (so gated requests bounce here with 401
// instead of hitting handlers).
//
// NOTE: the /sky CSP-strip middleware above also runs before this
// for path matching, but it only adds a Response.OnStarting hook
// and calls next() unconditionally, so we still catch /sky/ here.
// DBGLOG-3: HTTP request logger BEFORE the auth gate so 401s still
// produce an entry. Static assets + /api/logs* are skipped inside the
// middleware (no value, and avoids feedback loops).
app.UseMiddleware<NINA.Polaris.Middleware.RequestLoggingMiddleware>();

app.UseAuthMiddleware();

app.UseWebSockets();

// ----- PH2X-7: /phd2-gui/* reverse-proxy → xpra HTML5 client -----
// Same-origin proxy so the iframe's sessionStorage works and Polaris's
// outer auth layer (Relay tokens / LAN) covers PHD2 GUI access. xpra
// itself binds to 127.0.0.1 only, never exposed to the network directly.
//
// MapForwarder handles both static HTML5 client assets (HTML/JS/CSS
// stripped from the iframe URL) AND the WebSocket upgrade that streams
// PHD2's pixel updates.
var phd2GuiForwarder = app.Services.GetRequiredService<IHttpForwarder>();
var phd2GuiHttpClient = new HttpMessageInvoker(new SocketsHttpHandler {
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    ActivityHeadersPropagator = new Yarp.ReverseProxy.Forwarder.ReverseProxyPropagator(
        System.Diagnostics.DistributedContextPropagator.Current),
    ConnectTimeout = TimeSpan.FromSeconds(5),
});
var phd2GuiTransform = HttpTransformer.Default;
app.Map("/phd2-gui/{**rest}", async (HttpContext ctx, Phd2GuiSessionService gui) => {
    if (!gui.IsSupportedOs || !gui.XpraInstalled) {
        ctx.Response.StatusCode = 501;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "Embedded PHD2 GUI requires Linux + xpra installed on the Polaris host."
        });
        return;
    }
    if (!gui.SessionRunning) {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "xpra session not running. POST /api/guider/gui-session/start to launch it."
        });
        return;
    }
    // Strip the /phd2-gui prefix in-place so xpra's HTML5 client
    // sees its own root paths (the HTML serves asset URLs like
    // /js/Client.js that need to resolve to upstream root, not
    // /phd2-gui/js/Client.js). Same approach as the indi-web proxy
    // below. Without this the iframe loads the xpra HTML shell but
    // every JS/CSS/WebSocket request 404s and the screen stays black.
    var rest = ctx.Request.Path.Value ?? "/";
    if (rest.StartsWith("/phd2-gui", StringComparison.OrdinalIgnoreCase)) {
        rest = rest["/phd2-gui".Length..];
        if (string.IsNullOrEmpty(rest)) rest = "/";
    }
    // Strip the optional /t/<token> auth segment the cross-origin Capacitor
    // wrapper embeds (see AuthEndpoints.ExtractPathToken). xpra never sees
    // it; AuthMiddleware already validated the token from the original path.
    if (rest.StartsWith("/t/", StringComparison.Ordinal)) {
        var after = rest[3..];
        var slash = after.IndexOf('/');
        rest = slash >= 0 ? after[slash..] : "/";
    }
    if (string.IsNullOrEmpty(rest)) rest = "/";
    ctx.Request.Path = rest;
    var target = $"http://127.0.0.1:{gui.BindPort}";
    var err = await phd2GuiForwarder.SendAsync(ctx, target, phd2GuiHttpClient,
        ForwarderRequestConfig.Empty, phd2GuiTransform);
    // Only write a 502 if nothing was sent yet: a mid-stream forwarder error
    // (client aborted, upstream dropped) means the response already started, and
    // setting StatusCode then throws "response has already started".
    if (err != ForwarderError.None && !ctx.Response.HasStarted) {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync($"xpra proxy error: {err}");
    }
});

// ----- PH2VNC-2: /phd2-vnc-ws WebSocket → TightVNC TCP bridge -----
// noVNC speaks WebSocket; TightVNC speaks raw RFB over TCP. Standalone
// noVNC setups use the "websockify" Python proxy for this; we do it
// inline in C# (~60 lines) so there's no extra process to manage.
// AuthMiddleware gates this path so only authenticated Polaris users
// can reach the VNC server, even when TightVNC itself is bound to
// all interfaces (the docs walk the user through restricting that
// too, but the auth layer is the actual security boundary).
app.Map("/phd2-vnc-ws", async (HttpContext ctx, Phd2VncSessionService vnc,
                                ILoggerFactory loggers) => {
    var log = loggers.CreateLogger("Phd2VncBridge");
    if (!vnc.IsSupportedOs || !vnc.TightVncInstalled) {
        ctx.Response.StatusCode = 501;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "Embedded PHD2 GUI via VNC requires Windows + TightVNC installed on the Polaris host."
        });
        return;
    }
    if (!vnc.ServiceRunning || !vnc.Listening) {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "TightVNC service is not running or not listening on the loopback port. " +
                    "Open Settings → PHD2 Embedded GUI and start the service."
        });
        return;
    }
    if (!ctx.WebSockets.IsWebSocketRequest) {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Expected WebSocket upgrade request");
        return;
    }

    // Negotiate subprotocol. Modern noVNC (1.0+) doesn't request any
    // subprotocol — empty WebSocketRequestedProtocols, SubProtocol
    // stays null, and the connection is binary by default which is
    // what RFB needs. Older noVNC + websockify-compat clients ask
    // for "binary" explicitly; honour that so the wire stays
    // compatible across versions.
    string? chosenProto = null;
    if (ctx.WebSockets.WebSocketRequestedProtocols.Contains("binary"))
        chosenProto = "binary";
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
        SubProtocol = chosenProto
    });
    log.LogInformation("PHD2 VNC bridge: WS accepted (subProto={Proto}), connecting to 127.0.0.1:{Port}",
        chosenProto ?? "(none)", vnc.Port);

    using var tcp = new System.Net.Sockets.TcpClient();
    try {
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, vnc.Port, ctx.RequestAborted);
        // Disable Nagle on the TCP side. RFB is latency-sensitive
        // (mouse moves + tiny pointer-events traffic) and Nagle
        // batches small writes for ~40ms, which on top of WS framing
        // overhead made the cursor lag visibly + occasionally caused
        // the RFB handshake to stall when initial bytes got held
        // in the kernel buffer waiting for batching.
        tcp.NoDelay = true;
    } catch (Exception ex) {
        log.LogWarning(ex, "PHD2 VNC bridge: TCP connect to TightVNC failed");
        // Service was running at probe time but we can't connect now,
        // race condition (user stopped TightVNC between probe and
        // WS upgrade). Close the WS with a code the client can read.
        await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable,
            "TightVNC connection failed: " + ex.Message, ctx.RequestAborted);
        return;
    }
    var stream = tcp.GetStream();

    // Bidirectional pump. Each direction is its own task; first one
    // to complete wins and we tear the other down via the linked
    // CancellationTokenSource so neither leaks.
    using var pumpCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    var ct = pumpCts.Token;
    long bytesWs2Tcp = 0, bytesTcp2Ws = 0;

    async Task PumpWsToTcp() {
        var buf = new byte[16 * 1024];
        try {
            while (!ct.IsCancellationRequested) {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) {
                    log.LogDebug("PHD2 VNC bridge: client sent Close frame, tearing down");
                    break;
                }
                if (r.Count == 0) continue;
                await stream.WriteAsync(buf.AsMemory(0, r.Count), ct);
                bytesWs2Tcp += r.Count;
            }
        } catch (OperationCanceledException) { /* normal teardown */ }
        catch (Exception ex) {
            // Was silent before — meant "TightVNC dropped us" looked
            // identical to "browser closed tab". Logged at Debug so
            // it doesn't spam Warning on every normal disconnect but
            // a tail -f when troubleshooting catches it.
            log.LogDebug(ex, "PHD2 VNC bridge: WS→TCP pump terminated by exception");
        }
    }
    async Task PumpTcpToWs() {
        var buf = new byte[16 * 1024];
        try {
            while (!ct.IsCancellationRequested) {
                var n = await stream.ReadAsync(buf, ct);
                if (n == 0) {
                    log.LogDebug("PHD2 VNC bridge: TightVNC closed TCP, tearing down");
                    break;
                }
                await ws.SendAsync(buf.AsMemory(0, n),
                    System.Net.WebSockets.WebSocketMessageType.Binary,
                    endOfMessage: true, ct);
                bytesTcp2Ws += n;
            }
        } catch (OperationCanceledException) { /* normal teardown */ }
        catch (Exception ex) {
            log.LogDebug(ex, "PHD2 VNC bridge: TCP→WS pump terminated by exception");
        }
    }

    var ws2tcp = PumpWsToTcp();
    var tcp2ws = PumpTcpToWs();
    var winner = await Task.WhenAny(ws2tcp, tcp2ws);
    pumpCts.Cancel();
    try { await Task.WhenAll(ws2tcp, tcp2ws); } catch { }
    log.LogInformation("PHD2 VNC bridge: session closed (ws→tcp={Tx}B, tcp→ws={Rx}B, first-done={Side})",
        bytesWs2Tcp, bytesTcp2Ws, winner == ws2tcp ? "ws" : "tcp");
});

// ----- INDI-WEB-2: /indi-web/* reverse-proxy → indi-web (Bottle webapp) -----
// Same shape as /phd2-gui/* above: same-origin proxy so the iframe
// gets indi-web's HTML / JS / XHR / WebSocket without CORS dance,
// and Polaris's outer auth layer (Relay tokens / LAN-only) covers
// driver management. indi-web binds to 127.0.0.1 only — never
// directly exposed to the network even when Polaris listens on
// 0.0.0.0.
var indiWebForwarder = app.Services.GetRequiredService<IHttpForwarder>();
var indiWebHttpClient = new HttpMessageInvoker(new SocketsHttpHandler {
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    ActivityHeadersPropagator = new Yarp.ReverseProxy.Forwarder.ReverseProxyPropagator(
        System.Diagnostics.DistributedContextPropagator.Current),
    ConnectTimeout = TimeSpan.FromSeconds(5),
});
// Default transformer leaves headers / body untouched. We strip
// the /indi-web prefix from the request path manually below
// (HttpContext.Request.Path) before calling SendAsync — indi-web
// returns asset URLs like /static/app.css that need to resolve
// to the upstream root, not /indi-web/static/app.css.
var indiWebTransform = HttpTransformer.Default;
app.Map("/indi-web/{**rest}", async (HttpContext ctx, IndiWebManagerService svc) => {
    if (!svc.IsSupportedOs) {
        ctx.Response.StatusCode = 501;
        await ctx.Response.WriteAsJsonAsync(new {
            error = svc.UnsupportedReason ?? "Not supported on this OS",
        });
        return;
    }
    if (!svc.Installed) {
        ctx.Response.StatusCode = 501;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "indi-web not installed. Run: pip install indiwebmanager",
        });
        return;
    }
    if (!svc.Running) {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "indi-web not running. POST /api/indi/web/start to launch it.",
        });
        return;
    }
    // Strip the /indi-web prefix in-place so indi-web sees its
    // own root paths. PathBase grows / Path shrinks; the forwarder
    // uses Path verbatim for the upstream request.
    var rest = ctx.Request.Path.Value ?? "/";
    if (rest.StartsWith("/indi-web", StringComparison.OrdinalIgnoreCase)) {
        rest = rest["/indi-web".Length..];
        if (string.IsNullOrEmpty(rest)) rest = "/";
    }
    ctx.Request.Path = rest;
    var target = $"http://{svc.BindAddress}:{svc.BindPort}";
    var err = await indiWebForwarder.SendAsync(ctx, target, indiWebHttpClient,
        ForwarderRequestConfig.Empty, indiWebTransform);
    if (err != ForwarderError.None && !ctx.Response.HasStarted) {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync($"indi-web proxy error: {err}");
    }
});

// ----- CANOPUS: /canopus/* reverse-proxy → local assistant agent -----
// Serves the open chat client, the local-tier manifest, and the agent
// WebSocket from the Canopus Python server on loopback. Same shape as the
// /indi-web proxy: strip the /canopus prefix so the agent sees its own root
// paths, and the forwarder carries the WebSocket upgrade for /canopus/api/agent.
var canopusForwarder = app.Services.GetRequiredService<IHttpForwarder>();
var canopusHttpClient = new HttpMessageInvoker(new SocketsHttpHandler {
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    ActivityHeadersPropagator = new Yarp.ReverseProxy.Forwarder.ReverseProxyPropagator(
        System.Diagnostics.DistributedContextPropagator.Current),
    ConnectTimeout = TimeSpan.FromSeconds(5),
});
var canopusTransform = HttpTransformer.Default;
app.Map("/canopus/{**rest}", async (HttpContext ctx,
        NINA.Polaris.Services.External.CanopusServerService svc) => {
    if (!svc.Running) {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsJsonAsync(new {
            error = svc.UnavailableReason
                ?? "Canopus local backend is not running. POST /api/canopus/start to launch it.",
        });
        return;
    }
    var rest = ctx.Request.Path.Value ?? "/";
    if (rest.StartsWith("/canopus", StringComparison.OrdinalIgnoreCase)) {
        rest = rest["/canopus".Length..];
        if (string.IsNullOrEmpty(rest)) rest = "/";
    }
    // Strip the optional /t/<token> auth segment the cross-origin wrapper embeds
    // (AuthMiddleware already validated it), same as the phd2-gui proxy.
    if (rest.StartsWith("/t/", StringComparison.Ordinal)) {
        var after = rest[3..];
        var slash = after.IndexOf('/');
        rest = slash >= 0 ? after[slash..] : "/";
    }
    if (string.IsNullOrEmpty(rest)) rest = "/";
    ctx.Request.Path = rest;
    var target = $"http://127.0.0.1:{svc.AgentPort}";
    var err = await canopusForwarder.SendAsync(ctx, target, canopusHttpClient,
        ForwarderRequestConfig.Empty, canopusTransform);
    if (err != ForwarderError.None && !ctx.Response.HasStarted) {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync($"canopus proxy error: {err}");
    }
});

// Equipment endpoints
app.MapEquipmentEndpoints();
app.MapCameraEndpoints();
app.MapVideoEndpoints();
app.MapTelescopeEndpoints();
app.MapFocuserEndpoints();
app.MapAuxEndpoints();
app.MapImagerEndpoints();
app.MapDitherEndpoints();
app.MapFilterWheelEndpoints();
// ASCOM Platform-specific (SetupDialog, platform-presence probe).
// Per-device select/connect/discover are already handled by the
// per-device endpoint groups above with ?driver=ascom-com.
app.MapAscomEndpoints();
app.MapRotatorEndpoints();
app.MapSwitchEndpoints();
app.MapFlatDeviceEndpoints();
app.MapDomeEndpoints();
app.MapWeatherEndpoints();
app.MapGuiderEndpoints();
app.MapSimulatorEndpoints();
app.MapIndiWebEndpoints();
// WIFI-3: hotspot ↔ station mode switch (Linux + NetworkManager only)
app.MapNetworkEndpoints();
app.MapStorageEndpoints();
app.MapUsbEndpoints();
app.MapScriptEndpoints();
app.MapAutoFocusEndpoints();
// Sticky UI field persistence (panel exposure/gain/binning, target name,
// AF params, ...): client PUTs a JSON blob, restores it on load.
app.MapUiStateEndpoints();
// MFOC-3: Bahtinov mask analyser endpoint, lives under the same
// /api/focus group as future manual-assist sub-features (donut
// metric, gaussian FWHM fit, ...).
app.MapFocusEndpoints();
app.MapMeridianFlipEndpoints();
// FIELD4-4: PREVIEW-tab one-shot plate solve.
app.MapPlateSolveEndpoints();
// AUTH-1: /api/auth/{status,setup,login,logout,change-password,
// disable,enable}. Mapped here; AuthMiddleware (AUTH-2) exempts the
// whole /api/auth/* prefix so these are reachable without a token.
app.MapAuthEndpoints();
// TLS-1: /api/tls/{status,letsencrypt/config}. Read + persist HTTPS
// cert config (self-signed + Let's Encrypt via DuckDNS DNS-01).
// Issuance + renew endpoints land in TLS-5.
app.MapTlsEndpoints();
// DBGLOG-4: /api/logs/* (gated by AuthMiddleware along with the
// rest of /api/*). The middleware skip-list for /api/logs* only
// blocks the http-request-logging entry, NOT the auth gate.
NINA.Polaris.Endpoints.LogsEndpoints.MapLogsEndpoints(app);
app.MapPolarAlignmentEndpoints();
app.MapFlatWizardEndpoints();
app.MapAlpacaEndpoints();
app.MapStellariumEndpoints();
app.MapSequenceEndpoints();
app.MapPlanEndpoints();
app.MapAdvancedSequenceEndpoints();
app.MapMosaicEndpoints();
app.MapPluginEndpoints();
app.MapSkyEndpoints();
app.MapSystemEndpoints();
app.MapImageEndpoints();
app.MapStudioEndpoints();
app.MapEditorEndpoints();
app.MapBlendEndpoints();
app.MapOnnxEndpoints();
app.MapFilesEndpoints();
app.MapCacheEndpoints();
app.MapBenchmarkEndpoints();
app.MapSensorAnalysisEndpoints();
app.MapSirilEndpoints();
app.MapDssEndpoints();
app.MapCanopusEndpoints();
app.MapDsoThumbPackEndpoints();
app.MapNcnnModelPackEndpoints();
app.MapGraXpertEndpoints();
app.MapUpdateEndpoints();
// STORAGE-1/2: capture-disk survey, format and mount. A SEPARATE map from
// MapStorageEndpoints (the network-push settings, already mapped above) --
// calling that one twice made every /api/storage/config request throw
// AmbiguousMatchException and 500 the storage settings page.
app.MapDataDiskEndpoints();
app.MapCropEndpoints();
app.MapPostProcessEndpoints();
app.MapDeconEndpoints();
app.MapAnalysisEndpoints();
app.MapWorkflowEndpoints();

// GX-1: kick off an initial walk of the configured Onnx:ModelsPath
// so /api/onnx/manifest is populated before the first browser request.
// RescanAsync only stat-walks; the SHA-256 of each model comes from the
// persisted cache when the file is unchanged.
//
// ONNXHASH: warm whatever the cache does not cover, here, in the background.
// The hashes used to be computed inside the first /api/onnx/manifest request
// after every restart -- 24 models and 3.86 GB off the system card on the Q6A,
// measured at 48.07 s, with every other read queued behind it. The browser
// fetches that manifest at startup, so the cost landed on the operator as "the
// app takes forever to load". Doing it here means a request never pays for it.
_ = Task.Run(async () => {
    var reg = app.Services.GetRequiredService<NINA.Polaris.Services.Onnx.OnnxModelRegistry>();
    try { await reg.RescanAsync(); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "OnnxModelRegistry initial scan failed"); }
    try { await reg.WarmHashesAsync(); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "OnnxModelRegistry hash warm-up failed"); }
});

// Live stacking + INDI
app.MapLiveStackEndpoints();
app.MapIndiEndpoints();
// INDIPROP-1: in-process INDI property browser (native replacement
// for the standalone indi_control_panel Qt binary that's no longer
// packaged on recent Raspberry Pi OS releases).
app.MapIndiPropertiesEndpoints();
// INDI profile assistant: USB scan -> proposed drivers -> (on confirm)
// indi-web profile, so a new rig doesn't start with picking drivers by hand
// out of ~420 installed entries.
app.MapIndiDetectEndpoints();

// WebSocket streams
app.Map("/ws/image-stream", ImageStreamHandler.Handle);
app.Map("/ws/status", StatusStreamHandler.Handle);
// Remote terminal, gated by Terminal:Enabled in appsettings. The
// handler itself returns 403 when disabled so a curious client can
// still see why the endpoint exists.
app.Map("/ws/terminal", TerminalSocketHandler.Handle);

// GX-10: surface where to actually reach the server. Logs the HTTP
// + HTTPS endpoints at startup so the user (and the docs/screenshots)
// can copy the right URL into a remote browser without guessing.
// The cert fingerprint goes to the log too so a security-paranoid
// user can verify what Chrome shows matches what Polaris generated.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Polaris.Startup");
if (httpsEnabled) {
    startupLogger.LogInformation("HTTPS listening on https://*:{Port}  (cert fingerprint {Fp})",
        httpsPort, certService.Fingerprint);
    startupLogger.LogInformation("HTTPS is the LAN entry point, use one of: {Names}",
        string.Join(", ", certService.SanEntries().Take(8)));
}
if (httpEnabled) {
    var bind = httpBindAny ? "*" : "127.0.0.1 (loopback only)";
    startupLogger.LogInformation("HTTP  listening on http://{Bind}:{Port} {Note}",
        bind, httpPort,
        httpBindAny
            ? "(LAN-exposed, Server:Http:Bind=any)"
            : "(loopback only, used by Relay tunnel + host-local scripts)");
}
if (!httpsEnabled && !httpEnabled) {
    startupLogger.LogWarning("Both HTTP and HTTPS are disabled, Polaris will not accept any requests.");
}

app.Run();

// Reached when the host shuts down cleanly (Ctrl-C, SIGTERM,
// IHostApplicationLifetime.StopApplication). Returning 0 here is
// what gives top-level Main its `int` return type — required
// because the --ascom-setup helper path above returns 1/2 on
// driver-side failures, and the C# compiler insists every code
// path of an `int`-returning Main produces an int.
return 0;