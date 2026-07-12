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

// Profile + equipment data models extracted from ProfileService.cs for
// readability. These are plain serialisable records owned by
// ProfileService; no behaviour lives here.

namespace NINA.Polaris.Services;

public class UserProfile {
    public string Name { get; set; } = "Default";

    // Observatory location
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }

    // Camera optics (fallback only, live sensor dims come from the camera)
    public double SensorWidthMm { get; set; } = 23.5;
    public double SensorHeightMm { get; set; } = 15.7;
    public double FocalLengthMm { get; set; } = 478;
    public int SensorPixelsX { get; set; } = 6248;
    public int SensorPixelsY { get; set; } = 4176;

    // Default imaging settings
    public double DefaultExposure { get; set; } = 30;
    public int DefaultGain { get; set; } = 100;
    public int DefaultBinning { get; set; } = 1;

    // INDI connection
    public string IndiHost { get; set; } = "localhost";
    public int IndiPort { get; set; } = 7624;

    /// <summary>Master toggle for HardwareAutoConnectService, when on,
    /// app startup tries INDI, runs Alpaca discovery, and then
    /// re-connects every device saved on the active rig. Default off
    /// so a fresh install never silently dials hardware that isn't
    /// powered up yet.</summary>
    public bool AutoConnectOnStartup { get; set; } = false;

    // Legacy single-rig equipment selection (still serialised for
    // backward-compat; new code uses EquipmentProfiles below).
    public string? LastCamera { get; set; }
    public string? LastTelescope { get; set; }
    public string? LastFocuser { get; set; }
    public string? LastFilterWheel { get; set; }

    // Multi-rig support: a list of named equipment sets the user can switch
    // between. Loaded on first run by migrating the legacy LastXxx fields
    // into a "Default" rig.
    public List<EquipmentProfile> EquipmentProfiles { get; set; } = new();
    public string? ActiveEquipmentProfileId { get; set; }

    // PLAN mode: a global library of saved multi-target imaging plans
    // (see Services/Plan/PlanModels.cs). Global rather than per-rig so a plan
    // can be run with whatever rig is active.
    public List<NINA.Polaris.Services.Plan.ImagingPlan> Plans { get; set; } = new();

    // Plate solver
    public string? AstapPath { get; set; }
    public double SolveToleranceArcsec { get; set; } = 30;
    /// <summary>Star-database directory override for ASTAP (the dir holding the
    /// *.290 / *.1476 files). Empty/null = auto-detect.</summary>
    public string? AstapDataDir { get; set; }
    /// <summary>Solver id used first (matches IPlateSolver.Id, e.g. "astap",
    /// "platesolve3", "astrometry-net-local", "astrometry-net-online").</summary>
    public string PlateSolvePrimary { get; set; } = "astap";
    /// <summary>ASTAP downsample factor: 0 = auto (scales with image size),
    /// 1 = none, 2/3/4 = fixed binning before star detection. Higher is faster
    /// on weak hardware (Pi-class boards) at some accuracy cost.</summary>
    public int PlateSolveDownsample { get; set; } = 2;
    /// <summary>Search radius in degrees around the RA/Dec hint.</summary>
    public double PlateSolveSearchRadiusDeg { get; set; } = 30;
    /// <summary>Fall back to a blind-capable solver if the primary fails.</summary>
    public bool PlateSolveUseBlindFallback { get; set; } = true;
    /// <summary>API key for the nova.astrometry.net online solver (free, from
    /// the site's Profile → API Key). Empty = the online solver stays disabled
    /// ("not installed" in the card).</summary>
    public string? AstrometryApiKey { get; set; }
    /// <summary>Path to the local Astrometry.net <c>solve-field</c> binary.
    /// Empty/null = auto (/usr/bin/solve-field on Linux; on Windows requires
    /// WSL or a Cygwin/ANSVR install). When <see cref="PlateSolveUseWsl"/> is
    /// on this is the path/command INSIDE the WSL distro (default
    /// "solve-field").</summary>
    public string? SolveFieldPath { get; set; }
    /// <summary>Windows-only: run <c>solve-field</c> through WSL (wsl.exe).
    /// Polaris translates the Windows FITS/.wcs paths to /mnt/&lt;drive&gt;/...
    /// automatically. Lets a Windows host use the Linux Astrometry.net build.</summary>
    public bool PlateSolveUseWsl { get; set; }
    /// <summary>Optional WSL distro name (wsl.exe -d &lt;distro&gt;). Empty =
    /// the default distro.</summary>
    public string? PlateSolveWslDistro { get; set; }

    // TLS-1: Let's Encrypt via DuckDNS DNS-01 challenge. Replaces the
    // self-signed cert with a real publicly-trusted cert when the user
    // owns (or has registered free of charge on duckdns.org) a domain
    // that DNS-resolves to the Pi's LAN IP. Self-signed remains the
    // fallback when LE isn't configured or its cert is missing /
    // expired. Auto-renewal runs daily via LetsEncryptRenewalService
    // and refreshes when the cert has less than 30 days remaining.
    // Token + email are persisted in plain JSON because the profile
    // file is already gated by OS file permissions; if someone gains
    // read access to the profile they already control Polaris itself.
    public bool LetsEncryptEnabled { get; set; } = false;
    public string LetsEncryptDomain { get; set; } = "";       // e.g. nina-polaris.duckdns.org
    public string DuckDnsToken { get; set; } = "";            // DuckDNS account UUID
    public string LetsEncryptEmail { get; set; } = "";        // ACME registration + expiry warnings
    public bool LetsEncryptUseStaging { get; set; } = false;  // true = Let's Encrypt staging CA (untrusted, no rate limits)
    public DateTime? LetsEncryptLastRenewalUtc { get; set; }  // last successful issuance / renewal
    public DateTime? LetsEncryptNotAfterUtc { get; set; }     // cert expiry from last successful issuance
    public string? LetsEncryptStatus { get; set; }            // last operation outcome (Ok / Error / "in progress")
    public string? LetsEncryptLastError { get; set; }         // last failure message (null on success)

    // Auto-push of saved images to network storage (NAS / share / SSH box).
    // Global (one target for the host), persisted in plain JSON like the
    // DuckDns/PHD2 creds above — the profile file is already gated by OS
    // file permissions. Password is never returned by GET /api/storage/config
    // nor exposed over the WebSocket status. Pushed files mirror the local
    // capture tree onto the target; the local copy is kept.
    public bool   StoragePushEnabled { get; set; } = false;
    public string StorageKind { get; set; } = "smb";   // smb | sftp | local
    public string StorageHost { get; set; } = "";        // host or IP (smb/sftp)
    public int    StoragePort { get; set; } = 0;          // 0 => provider default (445 smb / 22 sftp)
    public string StorageShare { get; set; } = "";        // SMB share name (no slashes)
    public string StorageBasePath { get; set; } = "";     // SFTP base dir OR local/mounted path
    public string StorageDomain { get; set; } = "";       // SMB workgroup/domain (optional)
    public string StorageUsername { get; set; } = "";
    public string StoragePassword { get; set; } = "";
    public string? StorageLastTestResult { get; set; }    // last "Test connection" outcome

    // External post-processing tools (Siril + GraXpert). Empty/null
    // means "auto-detect" via BinaryLocator; set explicitly to
    // override the default path search.
    public string? SirilPath { get; set; }
    public string? SirilScriptsDir { get; set; }
    public string? GraXpertPath { get; set; }
    public double GraXpertBgeSmoothing { get; set; } = 1.0;
    public string GraXpertBgeCorrection { get; set; } = "Subtraction";
    public double GraXpertDeconStrength { get; set; } = 0.5;
    public double GraXpertDeconPsfSize { get; set; } = 4.0;
    public double GraXpertDenoiseStrength { get; set; } = 0.5;

    // GX-1: ONNX in-browser inference for GraXpert AI ops. The server
    // hosts the .onnx model files (Onnx:ModelsPath points at any dir
    // containing them; GraXpert's models/ layout, {family}-ai-models/
    // {version}/model.onnx, is auto-detected) and serves bytes via
    // /api/onnx/model/... The browser fetches once, caches in IndexedDB
    // by SHA-256 hash, runs inference locally via onnxruntime-web.
    // LicenseAcknowledged tracks the CC BY-NC-SA 4.0 consent the user
    // gave (models are non-commercial; consent is per-install).
    public string OnnxModelsPath { get; set; } = "";
    // Base URL of a public bucket (e.g. Supabase Storage) hosting the ONNX
    // models + a models-index.json, so a device/image without the bundled
    // models can download them on demand. Empty = downloader disabled.
    public string OnnxModelsBucketUrl { get; set; } = "";
    public bool OnnxLicenseAcknowledged { get; set; } = false;
    public string OnnxDefaultDenoiseVersion { get; set; } = "2.0.0";
    public bool OnnxPreferCli { get; set; } = false;

    // OCL: use the SBC GPU (OpenCL) for classic image kernels when the board
    // exposes a usable OpenCL device. Default on; honoured only when an OpenCL
    // driver is present (no effect on Pi/x86 without OpenCL). Set false to force
    // the CPU path (A/B testing, or if a board's GPU misbehaves).
    public bool UseGpuOpenCl { get; set; } = true;

    // AUTH-1: basic auth for the local HTTP API + WebSockets. Default
    // enabled, but with no hash configured. The frontend's first-run
    // wizard forces the user to set a password before any other tab
    // becomes accessible. Loopback (127.0.0.1) bypasses regardless.
    // AuthEnabled toggle covers the "closed LAN, no friction" case
    // (Settings -> Authentication -> uncheck, requires current pwd).
    // Hash + salt are base64; HashAlgo lets us migrate to Argon2 later
    // without breaking existing installs. Session timeout drives the
    // sliding-expiration TTL inside AuthService's in-memory store.
    public bool AuthEnabled { get; set; } = true;
    public string AuthPasswordHash { get; set; } = "";
    public string AuthPasswordSalt { get; set; } = "";
    public string AuthHashAlgo { get; set; } = "pbkdf2-sha256-100000";
    public int AuthSessionTimeoutHours { get; set; } = 24;

    // Human-friendly name shown when this device is discovered on the
    // network (e.g. "Telescope on the balcony"). Empty = fall back to the
    // auto-generated mDNS instance name (polaris-app-XXXX). Useful when a
    // single SD-card image is cloned onto several Pis: each Pi already
    // self-names uniquely from its hardware id, and the owner can give
    // each a readable label here.
    public string DeviceFriendlyName { get; set; } = "";

    // Image output
    public string ImageOutputDir { get; set; } = "";
    public string ImageNamePattern { get; set; } = "{target}_{filter}_{exposure}s_g{gain}_{temp}C_{datetime}_{seq}";
    public string ImageFormat { get; set; } = "fits";

    // PHD2 lifecycle preferences (app-global, not per-rig). When true the
    // PHD2AutoStartService launches PHD2 (and connects the JSON-RPC client)
    // as soon as the Headless app starts.
    public bool PHD2AutoStart { get; set; } = false;

    // SIM-2: built-in equipment simulator (indi_simulator_* on Linux,
    // Alpaca Omni Simulator on Windows). When SimulatorAutoStart is
    // true, SimulatorAutoStartService launches the configured stack
    // ~3s after Polaris boots so the user doesn't need to babysit a
    // separate terminal. Toggleable from the Settings tab. Defaults
    // are conservative: off, sensible 4-device list, INDI default port.
    public bool SimulatorAutoStart { get; set; } = false;
    public List<string> SimulatorDevices { get; set; }
        = new() { "ccd", "telescope", "focus", "wheel" };
    // INDI default; AscomSimulatorBackend overrides to 32323 (Alpaca
    // Omni Sim default) when the active backend is "ascom". UI saves
    // whatever the user picked, the backend uses its own default
    // when the saved value doesn't make sense (0 / null).
    public int SimulatorPort { get; set; } = 7624;

    /// <summary>
    /// Which sequencer to surface as the default in the UI. The Simple
    /// Sequencer (legacy, A4-era) is a flat list of items; the Advanced
    /// Sequencer (Phase C) is a tree with containers, conditions, and
    /// triggers. Both run side-by-side; this flag only picks which tab
    /// the UI lands on first.
    /// </summary>
    public bool PreferAdvancedSequencer { get; set; } = false;

    /// <summary>
    /// FIELD4-3: per-camera-id quirks (Bayer override + vertical
    /// flip). Keyed on the camera identifier the operator picked
    /// in the rig editor (INDI device name, Alpaca host:port:dev,
    /// SDK serial), these follow the physical camera across rigs
    /// that share it. Migrated on first load from the legacy
    /// per-rig <c>BayerPatternOverride</c> / <c>VerticalFlipImage</c>
    /// fields, which stay on <see cref="EquipmentProfile"/> for one
    /// release so older profile JSON keeps deserialising.
    /// </summary>
    public Dictionary<string, CameraQuirks> CameraQuirks { get; set; } = new();

    /// <summary>INDIPROP: operator-written help notes for INDI control
    /// panel properties. Keyed by INDI property name (e.g.
    /// "CCD_TEMPERATURE"), NOT per device, so a note written once shows
    /// for that property on every device that exposes it and survives
    /// reconnects. Augments / overrides the built-in English dictionary
    /// shipped in wwwroot/data/indi-property-help.json. Empty map by
    /// default.</summary>
    public Dictionary<string, string> IndiPropertyNotes { get; set; } = new();

    /// <summary>
    /// DBGLOG-9: opt-in disk persistence for the debug log. When
    /// false (default), the LogService ring buffer is the only home
    /// for entries — a server restart discards everything. When
    /// true, LogRotatorService subscribes to the Appended event and
    /// flushes batched entries to disk.
    ///
    /// Default ON (ASIAIR-style): one log file per session is written
    /// automatically to {LocalAppData}/NINA.Polaris/logs/polaris_&lt;date&gt;_&lt;time&gt;.jsonl
    /// for later inspection, with files older than 7 days swept hourly so the
    /// SD card doesn't fill up.
    /// </summary>
    public bool LogToDisk { get; set; } = true;

    /// <summary>One-time settings migration marker (see
    /// ProfileService.MigrateLoggingDefaults). Bumped when a persisted default
    /// needs to be re-seeded on existing profiles; a user change made AFTER the
    /// migration ran is never overwritten again.</summary>
    public int SettingsMigration { get; set; }

    /// <summary>Save a dedicated per-session guiding log under logs/guide/
    /// (ASIAIR-style). Native guider → a PHD2-compatible Guide Log; external
    /// PHD2 → a copy of PHD2's own guide log. Default on.</summary>
    public bool SaveGuideLogs { get; set; } = true;

    /// <summary>Optional override for where the EXTERNAL PHD2 writes its guide
    /// logs, if not the default ~/Documents/PHD2 or ~/PHD2. Used when copying
    /// PHD2's guide log into the Polaris logs folder.</summary>
    public string? Phd2GuideLogDir { get; set; }

    /// <summary>
    /// Runtime opt-in for the in-browser SSH remote terminal. The
    /// <see cref="WebSocket.TerminalSocketHandler"/> is normally gated by
    /// the <c>Terminal:Enabled</c> appsettings key (default false); this
    /// persisted flag lets the operator turn the feature on from the
    /// Settings UI (behind a risk-acknowledgement modal) without editing a
    /// JSON file on a headless host. Either source being true enables the
    /// endpoint. Default OFF — full shell access to the host is a serious
    /// capability, so it stays opt-in.
    /// </summary>
    public bool TerminalEnabled { get; set; } = false;

    /// <summary>
    /// UI language for the web client (BCP-47-ish tag: "en", "pt-BR", "es",
    /// "fr", "de"). The browser keeps its own per-device choice in
    /// localStorage('nina-ui-lang') as the source of truth; this profile field
    /// is the seed a fresh browser / the Android wrapper inherits when it has
    /// no local choice yet. Empty/"en" = English (the source language, no
    /// catalog). Does not affect server logs (those stay en-US by design).
    /// </summary>
    public string UiLanguage { get; set; } = "en";
}

/// <summary>Persisted native-guider calibration (the fields needed to rebuild a
/// <c>GuideCalibration</c>) plus metadata for the restore prompt. Stored on the
/// rig so it survives app restarts.</summary>
public class NativeCalibrationData {
    /// <summary>Equipment signature this calibration was measured with
    /// (guide camera + driver + binning + guider focal length + mount + driver).
    /// Lets a rig keep several calibrations and restore the one matching the
    /// equipment currently fitted, so swapping gear and back reuses the right
    /// calibration. Empty/null for legacy single-slot data.</summary>
    public string? Key { get; set; }
    public double XAngle { get; set; }
    public double YAngle { get; set; }
    public double XRate { get; set; }   // px/ms
    public double YRate { get; set; }   // px/ms
    public double DeclinationRad { get; set; }
    public double BacklashMs { get; set; }
    public int PierSide { get; set; }   // NINA.Core.Enum.PierSide as int
    public int RaSteps { get; set; }
    public int DecSteps { get; set; }
    public double PixelScale { get; set; }
    public int Binning { get; set; } = 1;
    public string SavedAtUtc { get; set; } = "";
    // Measured per-step plot points (camera-frame offsets from the calibration
    // origin) so the "Review Calibration" RA/Dec scatter plot survives a restore.
    public double[][] RaPoints { get; set; } = Array.Empty<double[]>();
    public double[][] DecPoints { get; set; } = Array.Empty<double[]>();
}

/// <summary>
/// A named equipment set (a "rig"). The user can save the device-name +
/// per-rig preference pair for any combination of equipment, then switch
/// rigs in one click without re-selecting every device. Common use cases:
///   - "Backyard SCT" vs "Travel APO" vs "Remote site setup"
///   - Different cameras with different optimal cooler temps
///   - Different focuser positions / step sizes per OTA
/// </summary>
public class EquipmentProfile {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";

    // Device selections, INDI device names as returned by getProperties.
    // Camera is special: it accepts multiple backend kinds via
    // CameraDriver below. The Camera field carries the driver-specific
    // device id (INDI name, or vendor SDK serial number, etc.); for
    // legacy profiles that pre-date CameraDriver it's assumed to be an
    // INDI device name.
    public string? Camera { get; set; }
    /// <summary>Camera backend kind. One of: <c>indi</c>, <c>alpaca</c>,
    /// <c>canon-edsdk</c>, <c>nikon-sdk</c>, <c>sony-sdk</c>. Defaults
    /// to <c>indi</c> for backward compatibility with profiles created
    /// before this field existed.</summary>
    public string CameraDriver { get; set; } = "indi";
    public string? Telescope { get; set; }
    /// <summary>Mount backend kind. One of: <c>indi</c> (default, covers
    /// every mount the running indiserver exposes), <c>alpaca</c>,
    /// <c>synscan-wifi</c> (Sky-Watcher UDP, planned), <c>nexstar-wifi</c>
    /// (Celestron TCP, planned), <c>lx200-tcp</c> (Meade-compatible TCP,
    /// planned). Defaults to <c>indi</c> for backward compatibility.</summary>
    public string TelescopeDriver { get; set; } = "indi";
    public string? Focuser { get; set; }
    /// <summary>Focuser backend kind. One of: <c>indi</c> (default,
    /// every focuser the running indiserver exposes), <c>ascom-com</c>
    /// (Windows-only, direct ASCOM Platform COM-interop).
    /// Defaults to <c>indi</c> for backward compatibility with
    /// profiles created before this field existed.</summary>
    public string FocuserDriver { get; set; } = "indi";
    public string? FilterWheel { get; set; }
    /// <summary>Filter-wheel backend kind. Same enum as
    /// <see cref="FocuserDriver"/>.</summary>
    public string FilterWheelDriver { get; set; } = "indi";
    /// <summary>Short code for a fixed filter screwed into the optical
    /// train when there is NO filter wheel (e.g. a light-pollution or
    /// dual/narrowband filter). When set and no wheel is connected, it is
    /// stamped into the FITS FILTER keyword and the {filter} filename token
    /// so captures still record what was in front of the sensor. Empty
    /// string = "None" (default).</summary>
    public string AttachedFilter { get; set; } = "";
    public string? Rotator { get; set; }
    public string? FlatDevice { get; set; }
    public string? Dome { get; set; }
    public string? Weather { get; set; }

    // Per-rig defaults
    public double CoolerTargetTemperature { get; set; } = -10;
    public int DefaultGain { get; set; } = 100;
    public int DefaultOffset { get; set; } = 50;
    public int DefaultBinning { get; set; } = 1;
    public int FocuserStepSize { get; set; } = 50;
    public int FocuserBacklashSteps { get; set; }

    /// <summary>Per-rig autofocus configuration (AFPORT). The sequencer AF
    /// instruction, all AF triggers and the FOCUS tab resolve their run
    /// parameters from here; explicit request fields override per run.
    /// Nullable so <see cref="ProfileService"/> can detect a pre-AFPORT
    /// profile on load and seed it from the legacy FocuserStepSize /
    /// FocuserBacklashSteps fields exactly once.</summary>
    public AutoFocusSettings? AutoFocus { get; set; }

    /// <summary>FIELD-2: per-rig Bayer mosaic override. Null = honour
    /// whatever the camera / FITS header reports (current behaviour).
    /// One of "RGGB" / "BGGR" / "GBRG" / "GRBG" forces the corresponding
    /// <see cref="NINA.Core.Enum.BayerPatternEnum"/> regardless of what
    /// the driver said. Use this when the live stack comes out
    /// monochrome or with swapped colours -- some drivers (notably the
    /// SVBONY indi_svbony_ccd build at the time of writing) emit the
    /// wrong BAYERPAT keyword or omit it entirely, which collapses the
    /// stacked frame to greyscale.</summary>
    public string? BayerPatternOverride { get; set; }

    /// <summary>FIELD3-2: vertically flip the pixel array on receive.
    /// FITS stores pixel rows BOTTOM-UP per the spec, but some drivers
    /// (the SVBONY SV405CC indi_svbony_ccd notably) deliver buffers
    /// TOP-DOWN without the corresponding header axis flip. Our reader
    /// loads sequentially, so the buffer downstream is "wrong-handed"
    /// for one half of the camera population. The visible symptom is a
    /// red-green checkerboard after debayer -- the Bayer pattern is
    /// correct enum-wise but offset by 1 row. Setting this true flips
    /// the array Y-direction after decode so RGGB stays RGGB. Default
    /// false (most cameras need no flip).</summary>
    public bool VerticalFlipImage { get; set; }

    // Polar alignment (TPPA) tunables. Per-rig because exposure /
    // gain that work for a fast OSC don't necessarily work for a
    // long-FL mono guide cam. Defaults match the N.I.N.A. desktop
    // TPPA out-of-the-box values.
    public int PolarAlignSlewDegrees { get; set; } = 30;
    public double PolarAlignExposureSec { get; set; } = 3.0;
    public int PolarAlignSettleSeconds { get; set; } = 2;
    public int PolarAlignGain { get; set; } = 100;

    // Slew & Center plate-solve tunables. Used by SKY tab "Go to",
    // meridian-flip recenter, and live-stack auto-recenter trigger.
    // Per-rig because the same defaults bite long-FL setups (5s + low
    // gain saturates Sirius; high gain + 2s burns sky-glow on slow
    // optics). Defaults match the previous SlewCenterService hardcoded
    // values so existing rigs behave identically until the user tunes.
    public double SlewCenterExposureSec { get; set; } = 5.0;
    public int SlewCenterGain { get; set; } = 100;

    // Optics specific to this rig. FocalLengthMm is the *effective*
    // focal length used everywhere downstream (FOV calc, FITS
    // FOCALLEN header, mosaic planner, etc.), for OTAs with a
    // reducer / Barlow attached this is the native focal length
    // multiplied by AccessoryFactor. The picker in the Manage Rigs
    // modal computes it; the user can also override manually.
    public double FocalLengthMm { get; set; } = 478;
    /// <summary>OTA aperture in millimetres. Auto-filled from the
    /// telescopes.json catalogue when the user picks a model; can
    /// be set manually for off-catalogue scopes. Drives the FOV
    /// calculator's f-ratio readout.</summary>
    public double ApertureMm { get; set; }
    /// <summary>Telescope brand selected in the picker (e.g.
    /// "Celestron"). Empty string when the user filled the optics
    /// fields manually.</summary>
    public string TelescopeBrand { get; set; } = "";
    /// <summary>Telescope model selected in the picker (e.g.
    /// "EdgeHD 8"). Empty when manual.</summary>
    public string TelescopeModel { get; set; } = "";
    /// <summary>Optional accessory in the optical train. One of
    /// "reducer", "barlow", "flattener", or empty when none.</summary>
    public string AccessoryType { get; set; } = "";
    /// <summary>Accessory brand + model string (e.g. "Celestron
    /// 0.7x Reducer Lens (EdgeHD)"). Empty when none.</summary>
    public string AccessoryModel { get; set; } = "";
    /// <summary>Focal-length multiplier the accessory applies.
    /// 1.0 when no accessory; 0.7 for a typical reducer; 2.0 for
    /// a 2× Barlow; etc. Effective focal length =
    /// nativeFocalLength × AccessoryFactor.</summary>
    public double AccessoryFactor { get; set; } = 1.0;
    /// <summary>Back-focus (camera-side spacing) required by the
    /// current OTA + accessory combination, in millimetres.
    /// Surfaced as a reminder in the rig editor, wrong backspacing
    /// is the most common reason flatteners produce elongated
    /// stars in the corners. Null when the OTA / accessory doesn't
    /// publish a value.</summary>
    public double? RequiredBackspacingMm { get; set; }

    /// <summary>Main-camera pixel size in micrometres. Fallback for backends
    /// that don't report it via CCD_INFO — notably <c>indi_gphoto</c> (DSLRs),
    /// which leaves CCD_PIXEL_SIZE at 0 until/unless told. 0 = "let the camera
    /// report it" (the normal case for dedicated astro cameras). When set and
    /// the connected camera reports 0, Polaris pushes it into the driver's
    /// CCD_INFO on connect so FOV, the plate-solve scale hint, and the
    /// post-solve focal-length auto-update all get a correct pixel scale.</summary>
    public double CameraPixelSizeUm { get; set; }

    /// <summary>Main-camera sensor resolution + bit depth, for backends that
    /// don't report it until/unless told — indi_gphoto rejects every exposure
    /// ("Please update the CCD Information ...") until CCD_INFO has a non-zero
    /// Max X/Y. Filled by the DSLR picker (derived from the catalogue) and
    /// pushed to the driver on connect. 0 = let the camera report it.</summary>
    public int CameraMaxX { get; set; }
    public int CameraMaxY { get; set; }
    public int CameraBitDepth { get; set; }

    // Focal length of the guide scope. Used for record-keeping and as a
    // sanity-check reference against PHD2's reported pixel scale. PHD2 itself
    // computes its pixel scale from its own configuration; we just track what
    // the user *thinks* the guide scope is.
    public double GuiderFocalLengthMm { get; set; } = 200;

    /// <summary>
    /// Guide-scope aperture. Used for record-keeping and as the
    /// denominator of the guidescope f-ratio displayed in the
    /// Guidescope card on the RIGS tab. Default 50 mm matches the
    /// most common 50 mm × 200 mm finder-guider combo. Set to 0 to
    /// suppress the f-ratio display.
    /// </summary>
    public double GuiderApertureMm { get; set; } = 50;

    /// <summary>Brand of the guide telescope. Optional, free-form.</summary>
    public string? GuideTelescopeBrand { get; set; }

    /// <summary>Model of the guide telescope. Optional, free-form.</summary>
    public string? GuideTelescopeModel { get; set; }

    // ----- Auxiliary (second) imaging camera -----
    // A second camera riding the same mount with a different lens/telescope.
    // It captures on its own cadence (independent of the main camera) and only
    // saves frames to a separate aux/ subtree; it is NOT used for guiding,
    // plate solving, live stacking, or sequencing. Mirrors the guide-camera
    // slot's addressing scheme. It can also be viewed/focused in the FOCUS tab.

    /// <summary>Aux-camera device id (same addressing scheme as
    /// <see cref="Camera"/>). Null = no aux camera selected.</summary>
    public string? AuxCamera { get; set; }

    /// <summary>Aux-camera backend kind. Same enum as
    /// <see cref="CameraDriver"/>. Defaults to <c>indi</c>.</summary>
    public string AuxCameraDriver { get; set; } = "indi";

    /// <summary>Effective focal length of the aux optics (mm). Stamped into
    /// the saved aux frames' FITS FOCALLEN so FOV/plate-solve metadata is
    /// correct for that train.</summary>
    public double AuxFocalLengthMm { get; set; } = 200;

    /// <summary>Aux-camera pixel size in micrometres. Same role + fallback
    /// behaviour as <see cref="CameraPixelSizeUm"/> but for the aux camera —
    /// a DSLR on the aux port (indi_gphoto) reports 0, so Polaris pushes this
    /// into the aux driver's CCD_INFO on connect. 0 = let the camera report it.</summary>
    public double AuxCameraPixelSizeUm { get; set; }

    /// <summary>Aux-camera sensor resolution + bit depth. Same role as the main
    /// camera's CameraMaxX/Y/BitDepth — needed to bootstrap indi_gphoto's
    /// CCD_INFO so the aux DSLR's exposures aren't rejected. 0 = camera reports.</summary>
    public int AuxCameraMaxX { get; set; }
    public int AuxCameraMaxY { get; set; }
    public int AuxCameraBitDepth { get; set; }

    /// <summary>Aux optics aperture (mm). Optional, for f-ratio display.</summary>
    public double AuxApertureMm { get; set; }

    /// <summary>Brand of the aux telescope/lens. Optional, free-form.</summary>
    public string? AuxTelescopeBrand { get; set; }

    /// <summary>Model of the aux telescope/lens. Optional, free-form.</summary>
    public string? AuxTelescopeModel { get; set; }

    /// <summary>Aux-camera exposure per frame (ms). Independent of the main
    /// camera since the optics differ. Default 5 s.</summary>
    public int AuxExposureMs { get; set; } = 5000;

    /// <summary>Aux-camera gain. 0 = leave the driver default.</summary>
    public int AuxGain { get; set; }

    /// <summary>Aux-camera binning. Default 1.</summary>
    public int AuxBinning { get; set; } = 1;

    /// <summary>When true, the aux capture loop runs (and saves frames) while a
    /// main session (LIVE or AUTORUN) is active. Default off.</summary>
    public bool AuxEnabled { get; set; }

    /// <summary>Aux focuser device id (same addressing scheme as
    /// <see cref="Focuser"/>). Optional; enables manual focusing of the aux
    /// camera from the FOCUS tab. Null = no aux focuser.</summary>
    public string? AuxFocuser { get; set; }

    /// <summary>Aux focuser backend kind. Same enum as
    /// <see cref="FocuserDriver"/>. Defaults to <c>indi</c>.</summary>
    public string AuxFocuserDriver { get; set; } = "indi";

    /// <summary>Guide-scope focuser device id (same addressing scheme as
    /// <see cref="Focuser"/>). Optional; some setups motorise the guide scope.
    /// Null = no guide focuser.</summary>
    public string? GuideFocuser { get; set; }

    /// <summary>Guide focuser backend kind. Same enum as
    /// <see cref="FocuserDriver"/>. Defaults to <c>indi</c>.</summary>
    public string GuideFocuserDriver { get; set; } = "indi";

    /// <summary>
    /// Last-known filter-wheel slot names for this rig, in slot order.
    /// Filter names normally live in the driver (INDI FILTER_NAME), but
    /// some drivers reset them to "Filter N" on reconnect / driver reset,
    /// which loses the user's labels. We mirror them on the rig profile so
    /// the frontend can re-push them to the wheel after a reconnect when
    /// the driver reverts to defaults. Empty when never edited.
    /// </summary>
    public string[] FilterNames { get; set; } = System.Array.Empty<string>();

    // ----- Guider backend selection (native vs PHD2) -----

    /// <summary>Which autoguider drives this rig. <c>native</c> (default)
    /// uses the in-process <c>NativeGuider</c> (ported PHD2 math) with the
    /// rig's own guide camera + mount pulse guiding; <c>phd2</c> uses the
    /// external PHD2 process via <see cref="PHD2Client"/>. Selectable
    /// per-rig. New rigs default to the native guider; rigs already saved
    /// with <c>phd2</c> keep using external PHD2 until changed.</summary>
    public string GuiderDriver { get; set; } = "native";

    /// <summary>Guide-camera device id used by the native guider. Same
    /// addressing scheme as <see cref="Camera"/> (INDI device name, vendor
    /// SDK serial, host:port:devnum for Alpaca). Null = no guide camera
    /// selected (native guiding cannot start). Unused when
    /// <see cref="GuiderDriver"/> is <c>phd2</c> (PHD2 owns its own cam).</summary>
    public string? GuideCamera { get; set; }

    /// <summary>Guide-camera backend kind. Same enum as
    /// <see cref="CameraDriver"/>. Defaults to <c>indi</c>.</summary>
    public string GuideCameraDriver { get; set; } = "indi";

    /// <summary>Native guider exposure per frame (ms). Default 1 s.</summary>
    public int NativeGuideExposureMs { get; set; } = 1000;

    /// <summary>Native guider calibration step pulse length (ms). Default 1 s.</summary>
    public int NativeCalibrationStepMs { get; set; } = 1000;

    /// <summary>Native guider RA minimum-move deadband (pixels). Errors
    /// below this are not corrected. Default 0.15 px.</summary>
    public double NativeMinMoveRaPx { get; set; } = 0.15;

    /// <summary>Native guider Dec minimum-move deadband (pixels).
    /// Default 0.15 px.</summary>
    public double NativeMinMoveDecPx { get; set; } = 0.15;

    /// <summary>Native guider RA hysteresis-algorithm aggression
    /// (0..2, fraction of the error corrected each frame). Default 0.70.</summary>
    public double NativeRaAggression { get; set; } = 0.70;

    /// <summary>Native guider Dec algorithm aggression (0..2, fraction of the
    /// error corrected each frame). Default 0.70. Separate from RA so each
    /// axis can be tuned independently (ASIAIR-style 10..150%).</summary>
    public double NativeDecAggression { get; set; } = 0.70;

    /// <summary>Native guider RA hysteresis weight (0..0.99, fraction of
    /// the previous move blended into this one). Default 0.10.</summary>
    public double NativeRaHysteresis { get; set; } = 0.10;

    /// <summary>Max single guide-correction pulse on the RA axis (ms). Caps
    /// runaway corrections (e.g. on a lost star or wind gust). Default 2.5 s.</summary>
    public int NativeMaxRaDurationMs { get; set; } = 2500;

    /// <summary>Max single guide-correction pulse on the Dec axis (ms).
    /// Default 2.5 s.</summary>
    public int NativeMaxDecDurationMs { get; set; } = 2500;

    /// <summary>Native guider RA-axis algorithm: hysteresis (default),
    /// lowpass, lowpass2, predictive, or identity.</summary>
    public string NativeRaAlgorithm { get; set; } = "hysteresis";

    /// <summary>Native guider Dec-axis algorithm: resistswitch (default),
    /// lowpass, lowpass2, hysteresis, predictive, or identity.</summary>
    public string NativeDecAlgorithm { get; set; } = "resistswitch";

    /// <summary>Predictive algorithm: worm period in seconds for the periodic-error
    /// feed-forward. 0 = auto-estimate from the guiding history.</summary>
    public double NativePredictiveWormPeriodSec { get; set; } = 0.0;

    /// <summary>Predictive algorithm: number of recent samples kept for the
    /// PE + drift fit (≈ two worm periods). Clamped to [32, 4096].</summary>
    public int NativePredictiveWindowSamples { get; set; } = 256;

    /// <summary>Predictive algorithm: feed-forward weight (0..1) applied to the
    /// predicted per-frame change on top of the reactive baseline. Default 0.7.</summary>
    public double NativePredictiveBlend { get; set; } = 0.7;

    /// <summary>ZFilter algorithm: exposure factor. The equivalent post-filter
    /// exposure time ≈ this × the guide exposure; higher = smoother/slower (sets the
    /// low-pass corner). <c>0 = use the PHD2 default 2.0</c>; otherwise clamped to
    /// [1, 20]. 0-as-default lets partial rig saves skip it without clobbering.</summary>
    public double NativeZFilterExpFactor { get; set; } = 0.0;

    /// <summary>Apply Dec backlash compensation (the amount is auto-measured
    /// during calibration). Off by default — an over-large value oscillates
    /// worse than no compensation.</summary>
    public bool NativeBacklashComp { get; set; } = false;

    /// <summary>Hard ceiling (ms) on the applied Dec backlash pulse. 0 = use
    /// the measured value (capped internally at 2x).</summary>
    public int NativeBacklashMaxMs { get; set; } = 0;

    /// <summary>Multi-star guiding: track several stars and average their
    /// displacements into one robust offset (lower centroid noise, survives
    /// the loss of any single star). On by default, matching PHD2.</summary>
    public bool NativeMultiStar { get; set; } = true;

    /// <summary>Maximum number of guide stars to track when multi-star is on
    /// (primary + secondaries). Clamped to [1, 12].</summary>
    public int NativeMaxGuideStars { get; set; } = 8;

    /// <summary>Guide-camera gain for native guiding. 0 = leave the camera's
    /// current/default gain. Default 40 (a sane mid-gain for common guide cams).</summary>
    public int NativeGuideGain { get; set; } = 40;

    /// <summary>Guide-camera binning for native guiding (1 = 1x1, 2 = 2x2).
    /// Bin 2 lowers resolution but boosts SNR + frame rate, common for guiding.</summary>
    public int NativeGuideBin { get; set; } = 1;

    /// <summary>What the native guider applies to each guide frame from its
    /// dark library / bad-pixel map (PHD2-style): "off", "dark" (subtract a
    /// master dark matching the current exposure/gain/bin), "bpm" (interpolate
    /// over mapped hot/dead pixels), or "both". A single "Build calibration"
    /// capture produces both artifacts, so switching modes never recaptures.</summary>
    public string NativeGuideCalibrationMode { get; set; } = "off";

    /// <summary>Number of dark frames averaged when building the native guide
    /// dark library. More frames = cleaner master (less residual read noise)
    /// at the cost of a longer build.</summary>
    public int NativeGuideDarkFrames { get; set; } = 15;

    /// <summary>How the native guider reacts to a German-equatorial pier-side
    /// change (meridian flip) detected mid-session: "mirror" (auto-adjust the
    /// existing calibration, default), "recalibrate" (run a fresh calibration),
    /// or "off" (ignore; the user recalibrates manually).</summary>
    public string NativePierSideHandling { get; set; } = "mirror";

    /// <summary>When mirroring the calibration on a pier flip, also reverse the
    /// Dec axis. Needed for mounts that reverse Dec guide output after a flip;
    /// off for most mounts.</summary>
    public bool NativeReverseDecAfterFlip { get; set; } = false;

    /// <summary>Last completed native-guider calibration for this rig, persisted
    /// so it can be auto-restored on connect across app restarts (PHD2-style
    /// "restore calibration"). Null until the rig has been calibrated once.</summary>
    public NativeCalibrationData? NativeCalibration { get; set; }

    /// <summary>All saved native-guider calibrations for this rig, keyed by
    /// equipment signature (see <see cref="NativeCalibrationData.Key"/>). When
    /// you swap equipment and recalibrate, a new keyed entry is stored without
    /// clobbering the old one; swapping the original gear back restores its
    /// matching calibration. Capped to a handful of most-recent entries.</summary>
    public List<NativeCalibrationData> NativeCalibrations { get; set; } = new();

    // Per-rig PHD2 settings
    public string PHD2Host { get; set; } = "localhost";
    public int PHD2Port { get; set; } = 4400;

    // ----- PHD2 deep integration (xpra + RPC orchestration) -----

    /// <summary>
    /// Cached PHD2 profile id matched by name to this rig. Set the first
    /// time PHD2ProfileSyncService finds a PHD2 profile whose name equals
    /// this rig's Name. Null = not yet matched or PHD2 profile missing.
    /// Don't rely on the value across PHD2 reinstalls, call
    /// PHD2ProfileSyncService.SyncRigToProfileAsync to refresh.
    /// </summary>
    public int? PHD2ProfileId { get; set; }

    /// <summary>
    /// Guide-algorithm preset Polaris applies on rig activation. One of
    /// "Default" / "Reactive" / "Smooth" / "Custom", see PHD2AlgoPresets.
    /// "Custom" means use the per-rig PHD2CustomAlgoParams bag.
    /// </summary>
    public string PHD2AlgoPreset { get; set; } = "Default";

    /// <summary>
    /// Per-rig override for PHD2 calibration step (ms). Null = let the
    /// orchestrator auto-compute from pixel scale + guide rate.
    /// </summary>
    public int? PHD2CalibrationStepMsOverride { get; set; }

    /// <summary>
    /// When true (default), activating this rig automatically asks
    /// PHD2ProfileSyncService to switch PHD2 to the matching profile.
    /// Set false if the user wants manual control of PHD2 profile switching.
    /// </summary>
    public bool PHD2AutoSyncOnRigSwitch { get; set; } = true;

    /// <summary>
    /// Free-form algorithm-parameter overrides for the "Custom" preset.
    /// Keys are in the format "axis:paramName" (e.g. "ra:Hysteresis"),
    /// values are the raw doubles pushed via set_algo_param.
    /// </summary>
    public Dictionary<string, double> PHD2CustomAlgoParams { get; set; } = new();

    /// <summary>
    /// Per-filter focuser offset in steps, relative to the rig's reference
    /// filter (typically the L filter). Consumed by
    /// <c>MoveToFilterOffsetInstruction</c>: when an instruction names a filter
    /// here, it moves the focuser to <c>currentPos + offset</c>. Filters not
    /// in the table are treated as 0.
    /// </summary>
    public Dictionary<string, int> FilterOffsets { get; set; } = new();

    /// <summary>
    /// Auto re-focus + re-center policy applied during live stacking
    /// (LSTR-3). Persisted per-rig because thermal characteristics +
    /// guiding precision vary by setup. Default = all triggers disabled.
    /// </summary>
    public LiveStackTriggers LiveStackTriggers { get; set; } = new();

    /// <summary>
    /// FW-1: Flat Wizard defaults (TargetADU, tolerance, frame count,
    /// exposure bounds, binning, max iterations, panel brightness).
    /// Persisted per-rig because aperture + f-ratio + filter set drive
    /// very different flat-field setups (e.g. f/5 refractor vs f/10
    /// SCT both want their own TargetADU + max-exposure ceiling so
    /// the binary search doesn't waste iterations).
    /// </summary>
    public FlatWizardSettings FlatWizard { get; set; } = new();

    /// <summary>INDIROB-3: per-device pre-connect delay (ms). Some INDI
    /// drivers need extra time after USB enumeration before the
    /// CONNECTION switch is accepted -- ESP32-based mounts (Onstep,
    /// ZWO AM3 WiFi bridge), USB-serial focusers with slow firmware
    /// init, etc. Keyed by INDI device name (the same string the
    /// driver advertises in defXxxVector). Missing key or value 0 =
    /// no delay (default). Operator sets these from the RIGS card
    /// per device when a particular piece of hardware misbehaves on
    /// connect; survives restarts because it lives on the profile.</summary>
    public Dictionary<string, int> PreConnectDelayMsByDevice { get; set; } = new();

    /// <summary>CLST-7: where live-stacking math runs.
    /// <list type="bullet">
    /// <item><b>auto</b> (default), server flips to MetricsOnly
    /// when a WASM-capable client connects, back to Full otherwise.</item>
    /// <item><b>server</b>, force server-side accumulator regardless
    /// of clients. Use when you want a Pi to be the canonical source
    /// for multiple browsers, or when WASM is slow on the client.</item>
    /// <item><b>client</b>, force MetricsOnly. Useful for testing the
    /// WASM path, or to free Pi CPU even if no client is currently
    /// hooked up (the next one that connects will pick up the stack
    /// from frame 1 on its side).</item>
    /// </list>
    /// Stored per-rig because the trade-off depends on the host:
    /// Pi 2/3 → client; Pi 5 / mini-PC → either works.</summary>
    public string LiveStackComputeMode { get; set; } = "auto";

    /// <summary>Per-rig angular move size (deg) at or above which a SKY "Go To"
    /// is flagged for confirmation before the mount moves. A big swing can make
    /// the mount un-flip and take the long way toward the pier/tripod (the AM3
    /// near-crash). <c>null</c> ⇒ use <see cref="MountSlewSafety.LargeMoveDeg"/>
    /// (60°); 0 disables the large-slew check. Nullable so a partial rig PUT that
    /// omits it never clobbers a customised value.</summary>
    public double? SlewConfirmDeg { get; set; }

    /// <summary>Per-rig anti-crash altitude floor (deg). A GoTo target below this
    /// is flagged for confirmation, and the live safety guard aborts a slew whose
    /// pointing drops below it. <c>null</c> ⇒ use
    /// <see cref="MountSlewSafety.AltitudeFloorDeg"/> (5°); 0 disables. Nullable
    /// for the same partial-PUT reason as <see cref="SlewConfirmDeg"/>. The global
    /// <c>MeridianFlipSettings.SafetyStopEnabled</c> remains the master on/off.</summary>
    public double? SlewFloorDeg { get; set; }

    /// <summary>When true, each raw frame fed to
    /// <c>LiveStackingService.AddFrameAsync</c> is also persisted to
    /// disk as a regular LIGHT (lands in the same per-target /
    /// per-filter / per-session layout as a sequence capture).
    /// Default ON — most users want both the integrated preview
    /// AND an archive they can re-stack offline in Siril /
    /// PixInsight later. Per-rig so visual-only EAA rigs can opt
    /// out (just untick the checkbox in the LIVE tab).</summary>
    public bool LiveStackSaveFramesToDisk { get; set; } = true;

    /// <summary>Debayer each OSC frame to RGB and integrate in colour
    /// during live stacking, broadcasting a colour preview on the LIVE
    /// canvas. Default OFF (mono/CFA accumulation), since it costs a
    /// debayer + 3x the warp/accumulate work per frame. Per-rig so a
    /// colour EAA rig can opt in while a mono rig stays mono.</summary>
    public bool LiveStackColor { get; set; } = false;

    /// <summary>Per-pixel kappa-sigma outlier rejection on the live stack:
    /// drop cosmic rays / plane trails / dithered hot pixels instead of
    /// folding them into the running mean. Default OFF (extra CPU + a per-
    /// pixel M2 buffer). Pays off most WITH dithering. Per-rig.</summary>
    public bool LiveStackSigmaRejection { get; set; } = false;

    /// <summary>Rejection threshold in sigmas (default 3.0).</summary>
    public double LiveStackSigmaKappa { get; set; } = 3.0;

    /// <summary>Auto-pause the live stack after this many seconds
    /// of integration. 0 (default) = no cap, runs until the user
    /// resets or stops. Per-rig so different setups (planetary
    /// short-stacks vs. deep-sky long-stacks) keep their own
    /// preferred duration.</summary>
    public int LiveStackMaxDurationSeconds { get; set; }

    /// <summary>SNR-3: target signal-to-noise ratio used by the LIVE
    /// tab's "ETA to target SNR" widget to estimate remaining stack
    /// time. Per-rig so a planetary close-up (low target, fast) and a
    /// deep-sky long-stack (high target, slow) each keep their own
    /// number. null = no target configured, ETA widget shows "set
    /// target" prompt. The LIVE tab can override this for a single
    /// session without persisting (liveStack.targetSnrOverride on the
    /// frontend); when null the override falls back to this value.</summary>
    public double? TargetSnr { get; set; }

    /// <summary>LSPP-3: per-frame pre-processing toggles for live
    /// stacking. Calibration applies dark/flat/bias on the server
    /// (or wherever the stack runs); BGE applies GraXpert background
    /// extraction on the client (MetricsOnly mode only). Both default
    /// OFF so existing rigs behave identically to the pre-LSPP build
    /// until the operator opts in via the LIVE tab.</summary>
    public LiveStackPreProcSettings LiveStackPreProcessing { get; set; } = new();

    /// <summary>Last-used VIDEO tab ROI / FOV (subframe). Persisted so
    /// the next session restores the same crop without the user re-
    /// picking. Nullable on every dimension so partial-PUT bodies
    /// from the JS client (which only ship the ROI fields, not the
    /// entire rig) can leave them untouched when they're null. The
    /// X/Y are sensor pixels (top-left of the box), W/H are the box
    /// dimensions. Size + Aspect are UI-side state (which pill was
    /// active) so the highlight is restored too. W=0 or H=0 means
    /// "full sensor, no ROI saved".</summary>
    public int? LastVideoRoiW { get; set; }
    public int? LastVideoRoiH { get; set; }
    public int? LastVideoRoiX { get; set; }
    public int? LastVideoRoiY { get; set; }
    public int? LastVideoRoiSize { get; set; }
    public string? LastVideoRoiAspect { get; set; }
}

public class ProfileSummary {
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastModified { get; set; }
}

/// <summary>
/// FIELD4-3: per-camera-id workarounds for driver quirks. Keyed on
/// the camera identifier the operator picked in the rig editor
/// (INDI device name, Alpaca <c>host:port:dev</c>, SDK serial),
/// these toggles follow the physical camera across rigs that share
/// it. Surfaced in the RIGS tab top-level "Camera quirks" table so
/// every camera the operator ever connects shows up with both
/// knobs.
///
/// Both fields used to live per-rig on <see cref="EquipmentProfile"/>;
/// they were hoisted up here so a user with two rigs sharing the
/// same SVBONY camera only configures the workaround once.
/// </summary>
public class CameraQuirks {
    /// <summary>One of "RGGB" / "BGGR" / "GBRG" / "GRBG", forces
    /// the corresponding <see cref="NINA.Core.Enum.BayerPatternEnum"/>
    /// regardless of what the driver said. Null / empty / "Auto"
    /// honours the driver-reported pattern.</summary>
    public string? BayerPatternOverride { get; set; }

    /// <summary>Bayer grid X pixel offset (0 or 1). Some sensors
    /// start the CFA grid at column 1 instead of 0 (e.g. SV405CC /
    /// IMX533 via indi_svbony_ccd with overscan columns). When
    /// non-zero the WebGL shader shifts the 2x2 cell sampling so
    /// the debayer aligns with the actual sensor mosaic.</summary>
    public int BayerOffsetX { get; set; }

    /// <summary>Bayer grid Y pixel offset (0 or 1).</summary>
    public int BayerOffsetY { get; set; }

    /// <summary>True flips the pixel buffer Y-direction on receive.
    /// FITS rows are bottom-up per spec, but some drivers (notably
    /// the SVBONY indi_svbony_ccd build at the time of writing)
    /// deliver top-down without flipping NAXIS2. The visible
    /// symptom is a red-green checkerboard after debayer because
    /// the cell pairing in the GPU shader is off by one row. The
    /// row flip is composed with an automatic Bayer-enum row-shift
    /// inside <see cref="ImageRelayService"/> so the final pattern
    /// on the wire stays aligned with the new buffer orientation.</summary>
    public bool VerticalFlipImage { get; set; }
}
/// <summary>
/// Per-rig autofocus configuration (AFPORT: N.I.N.A. desktop algorithm port).
/// Persisted on the <see cref="EquipmentProfile"/> so sequenced/triggered AF
/// runs use the same tuning as interactive ones; every field can still be
/// overridden per run by the corresponding nullable
/// <see cref="AutoFocusRequest"/> field.
/// </summary>
public class AutoFocusSettings {
    /// <summary>Distance in focuser steps between consecutive sweep points.</summary>
    public int StepSize { get; set; } = 50;

    /// <summary>Points required on EACH trendline arm (desktop
    /// AutoFocusInitialOffsetSteps). The initial pass moves OUT by
    /// OffsetSteps*StepSize then sweeps IN through OffsetSteps+1 points; the
    /// planner then extends one point at a time until both arms reach this
    /// count.</summary>
    public int OffsetSteps { get; set; } = 4;

    public double ExposureSeconds { get; set; } = 2.0;

    /// <summary>Exposures averaged per sweep point (desktop
    /// AutoFocusNumberOfFramesPerPoint). 1 = fastest.</summary>
    public int FramesPerPoint { get; set; } = 1;

    /// <summary>Curve fitting method: TRENDLINES | PARABOLIC | TRENDPARABOLIC
    /// | HYPERBOLIC | TRENDHYPERBOLIC (default).</summary>
    public string Method { get; set; } = "TRENDHYPERBOLIC";

    /// <summary>Minimum R² the fits used by <see cref="Method"/> must reach
    /// (including BOTH trendline arms for TREND* methods). 0 disables.</summary>
    public double RSquaredThreshold { get; set; } = 0.7;

    /// <summary>Full-sweep attempts before giving up when a quality gate fails.</summary>
    public int Attempts { get; set; } = 2;

    /// <summary>Reject a run whose confirmation HFR is worse than the starting
    /// HFR by more than this factor. 0 disables.</summary>
    public double MaxHfrRatio { get; set; } = 1.15;

    /// <summary>Centered crop ratio used for AF exposures/detection
    /// (1 = full frame). When the camera supports subframing the crop is a
    /// REAL sensor ROI (faster readout + transfer); otherwise the detection
    /// runs on a software-cropped buffer.</summary>
    public double InnerCropRatio { get; set; } = 1.0;

    /// <summary>Track only the N brightest stars across the whole sweep
    /// (desktop AutoFocusUseBrightestStars). 0 = use all detected stars.</summary>
    public int UseBrightestStars { get; set; }

    /// <summary>Backlash compensation when moving INWARD (position
    /// decreasing), in focuser steps.</summary>
    public int BacklashIn { get; set; }

    /// <summary>Backlash compensation when moving OUTWARD (position
    /// increasing), in focuser steps.</summary>
    public int BacklashOut { get; set; }

    /// <summary>OVERSHOOT (default; overshoot past the target then approach)
    /// or ABSOLUTE (persistent offset applied on direction reversal).</summary>
    public string BacklashModel { get; set; } = "OVERSHOOT";

    /// <summary>A sample below this star count is soft-rejected (measure 0,
    /// huge error) instead of feeding a bogus HFR into the fit.</summary>
    public int MinStars { get; set; } = 5;
}
