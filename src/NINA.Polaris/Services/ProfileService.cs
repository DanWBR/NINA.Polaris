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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// Central runtime configuration service. Owns the active
/// <see cref="UserProfile"/> and the per-rig <c>EquipmentProfile</c>
/// list, persists to JSON under <c>{LocalAppData}/NINA.Polaris/profiles/</c>,
/// and raises <see cref="EquipmentProfileActivated"/> so dependent
/// services (PHD2ProfileSyncService, LiveStackTriggersService, the
/// meridian-flip orchestrator) can reconfigure themselves when the
/// user switches rigs.
///
/// All mutations go through a save-lock <see cref="SemaphoreSlim"/>
/// so concurrent endpoint writes don't tear the JSON file. Reads
/// return the current snapshot directly, callers should not mutate
/// the returned record; the profile is replaced wholesale on save,
/// not edited in place.
/// </summary>
public class ProfileService {
    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _profileDir;
    private readonly string _activeProfilePath;
    private readonly ILogger<ProfileService> _logger;

    private UserProfile _activeProfile = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public UserProfile Active => _activeProfile;

    /// <summary>Where small auxiliary state files (sessions snapshot,
    /// etc.) can live next to the profile data. Exposed so other
    /// services don't have to re-derive the path from IConfiguration.</summary>
    public string DataDir => _profileDir;

    public ProfileService(IConfiguration config, ILogger<ProfileService> logger) {
        _logger = logger;

        var baseDir = config.GetValue("Profiles:Directory",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "profiles"))!;

        _profileDir = baseDir;
        _activeProfilePath = Path.Combine(baseDir, "active.json");

        Directory.CreateDirectory(_profileDir);
        Load();
    }

    public List<ProfileSummary> ListProfiles() {
        var files = Directory.GetFiles(_profileDir, "*.json")
            .Where(f => !f.EndsWith("active.json"))
            .ToList();

        var profiles = new List<ProfileSummary>();
        foreach (var file in files) {
            try {
                var json = File.ReadAllText(file);
                var p = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts);
                if (p != null) {
                    profiles.Add(new ProfileSummary {
                        Id = Path.GetFileNameWithoutExtension(file),
                        Name = p.Name,
                        LastModified = File.GetLastWriteTimeUtc(file)
                    });
                }
            } catch { }
        }

        return profiles;
    }

    public void Load() {
        // Crash-safe load. active.json is written atomically (tmp + replace)
        // with a .bak of the previous good version, so a torn/truncated file
        // from a power cut mid-write (common on field SBCs) can be recovered.
        // The cardinal rule: NEVER silently reset to an empty profile and then
        // let the next Save() overwrite a recoverable file — that's how rigs
        // vanish. So on a parse failure we preserve the bad file, try the
        // backup, and only fall back to a fresh Default when nothing is usable.
        var bakPath = _activeProfilePath + ".bak";

        if (TryLoadProfileFrom(_activeProfilePath)) {
            // loaded fine
        } else if (File.Exists(_activeProfilePath)) {
            // Main exists but didn't parse → corrupt/torn. Keep a copy so the
            // operator (or we) can recover it, then try the backup.
            PreserveCorruptProfile(_activeProfilePath);
            if (TryLoadProfileFrom(bakPath)) {
                _logger.LogWarning("active.json was corrupt; recovered from backup .bak");
                Save();   // rewrite a clean main from the recovered backup
            } else {
                _logger.LogError("active.json corrupt and no usable backup; " +
                    "starting a fresh Default profile (corrupt file preserved alongside)");
                _activeProfile = new UserProfile { Name = "Default" };
                Save();
            }
        } else if (TryLoadProfileFrom(bakPath)) {
            // Main missing but a backup survived (e.g. crash between delete and
            // rename) → recover it.
            _logger.LogWarning("active.json missing; recovered from backup .bak");
            Save();
        } else {
            _activeProfile = new UserProfile { Name = "Default" };
            Save();
            _logger.LogInformation("Created default profile at {Path}", _activeProfilePath);
        }

        // FIELD4-3: hoist legacy per-rig camera quirks (Bayer
        // override + vertical flip) into the per-camera-id map on
        // the user profile. Runs once per load; skipped for any
        // camera id that's already present in CameraQuirks so a
        // later edit to the per-camera entry isn't overwritten by
        // a stale legacy field on an old rig.
        MigrateLegacyCameraQuirks();

        // Logging defaults: builds prior to the default-ON change persisted
        // LogToDisk=false into active.json, so upgraded installs never wrote
        // the app log or the per-session guide log ("logs aren't being
        // written" field report). Re-seed both flags ON exactly once; a user
        // who turns them off AFTER this migration stays off.
        MigrateLoggingDefaults();

        // AFPORT: rigs saved before the AutoFocus settings block existed get
        // one seeded from the legacy manual-focus fields, so an upgraded
        // install's first profile-driven AF run uses the step size + backlash
        // the operator had already dialed in.
        MigrateAutoFocusSettings();

        // Sub-500ms guide exposures are junk from the stale-rig-PUT echo bug
        // (and below the new 0.5 s product minimum); re-seed them to 1 s.
        MigrateGuideExposureFloor();

        // Rigs saved while the auto-focus default was "all stars" carry a 0
        // that shadows the new single-star default forever. Re-seed once.
        MigrateAutoFocusSingleStar();

        // Deployment-time override for the capture root. Useful for
        // distribution images (Pi systemd unit, Docker, etc.) that
        // want a sensible default like /home/polaris/files without
        // forcing the user to click through the FILES tab on first
        // boot. Honoured only when the profile has no explicit value
        // saved; user-set values via the UI always win.
        if (string.IsNullOrWhiteSpace(_activeProfile.ImageOutputDir)) {
            var envDir = Environment.GetEnvironmentVariable("POLARIS_IMAGE_OUTPUT_DIR");
            if (!string.IsNullOrWhiteSpace(envDir)) {
                _activeProfile.ImageOutputDir = envDir.Trim();
                _logger.LogInformation(
                    "ImageOutputDir seeded from POLARIS_IMAGE_OUTPUT_DIR env: {Dir}",
                    _activeProfile.ImageOutputDir);
            } else {
                // No explicit value and no deployment override: default to a
                // "files" folder under the user's home so captures / saved live
                // frames land somewhere sensible out of the box instead of
                // being silently dropped. The directory is created lazily by
                // ImageWriterService on the first save; user-set values always
                // win (this only fires while the field is blank).
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home)) {
                    _activeProfile.ImageOutputDir = Path.Combine(home, "files");
                    _logger.LogInformation(
                        "ImageOutputDir defaulted to {Dir} (home/files)",
                        _activeProfile.ImageOutputDir);
                }
            }
        }
    }

    /// <summary>
    /// Factory reset: delete every profile JSON in the profile
    /// directory (active + named) plus the auth-sessions file, then
    /// recreate a fresh "Default" profile. Used to ship a clean
    /// distribution image with none of the operator's rigs, location,
    /// password, camera quirks, or test settings. Captured images
    /// (in the user's output folder) are NOT touched -- that's data,
    /// not config. Returns the number of files removed.
    /// </summary>
    public int FactoryReset() {
        _saveLock.Wait();
        int removed = 0;
        try {
            if (Directory.Exists(_profileDir)) {
                foreach (var f in Directory.GetFiles(_profileDir, "*.json")) {
                    try { File.Delete(f); removed++; }
                    catch (Exception ex) { _logger.LogWarning(ex, "FactoryReset: could not delete {File}", f); }
                }
                // Auth sessions (not a .json profile) live here too.
                var sessions = Path.Combine(_profileDir, "auth-sessions.json");
                try { if (File.Exists(sessions)) { File.Delete(sessions); removed++; } } catch { }
            }
        } finally {
            _saveLock.Release();
        }
        // Rebuild a pristine Default profile in memory + on disk.
        _activeProfile = new UserProfile { Name = "Default" };
        Save();
        _logger.LogWarning("Factory reset complete: removed {Count} config file(s), reset to defaults", removed);
        return removed;
    }

    public void Save() {
        _saveLock.Wait();
        try {
            var json = JsonSerializer.Serialize(_activeProfile, JsonOpts);
            WriteFileAtomic(_activeProfilePath, json, backup: true);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save profile");
        } finally {
            _saveLock.Release();
        }
    }

    public void SaveAs(string name) {
        var id = SanitizeFileName(name);
        var path = Path.Combine(_profileDir, id + ".json");
        _activeProfile.Name = name;

        try {
            var json = JsonSerializer.Serialize(_activeProfile, JsonOpts);
            WriteFileAtomic(path, json, backup: false);
            Save();  // writes active.json atomically (with .bak) under the lock
            _logger.LogInformation("Profile saved as: {Name} ({Path})", name, path);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save profile as {Name}", name);
        }
    }

    /// <summary>
    /// Crash-safe file write: serialise to a sibling <c>.tmp</c>, then move it
    /// over the target. The move is atomic on the same volume, so a reader (or
    /// a power cut) never sees a half-written file. When <paramref name="backup"/>
    /// is set, the previous good file is first copied to <c>.bak</c> so
    /// <see cref="Load"/> can recover from a torn write. Falls back to a plain
    /// overwrite on filesystems that reject atomic replace.
    /// </summary>
    private void WriteFileAtomic(string path, string contents, bool backup) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);

        if (backup && File.Exists(path)) {
            try { File.Copy(path, path + ".bak", overwrite: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not refresh backup for {Path}", path); }
        }

        try {
            File.Move(tmp, path, overwrite: true);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Atomic replace failed for {Path}; falling back to direct write", path);
            File.WriteAllText(path, contents);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Try to deserialise a profile from <paramref name="path"/> into
    /// <see cref="_activeProfile"/>. Returns false (without mutating state) when
    /// the file is missing, empty, or unparseable, so the caller can fall back
    /// to a backup instead of clobbering recoverable data.</summary>
    private bool TryLoadProfileFrom(string path) {
        try {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;
            var p = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts);
            if (p == null) return false;
            _activeProfile = p;
            _logger.LogInformation("Loaded profile: {Name} (from {File})",
                _activeProfile.Name, Path.GetFileName(path));
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to parse profile {File}", Path.GetFileName(path));
            return false;
        }
    }

    /// <summary>Copy a corrupt profile aside (never delete it) so the operator's
    /// data is recoverable even in the worst case where the backup is also
    /// unusable.</summary>
    private void PreserveCorruptProfile(string path) {
        try {
            var dest = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(path, dest, overwrite: true);
            _logger.LogWarning("Preserved corrupt profile as {Dest}", Path.GetFileName(dest));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not preserve corrupt profile {Path}", path);
        }
    }

    public bool LoadProfile(string id) {
        var path = Path.Combine(_profileDir, id + ".json");
        if (!File.Exists(path)) return false;

        try {
            var json = File.ReadAllText(path);
            _activeProfile = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts)
                ?? new UserProfile { Name = "Default" };
            Save();
            _logger.LogInformation("Switched to profile: {Name}", _activeProfile.Name);
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to load profile {Id}", id);
            return false;
        }
    }

    public void UpdateSettings(Action<UserProfile> update) {
        update(_activeProfile);
        Save();
    }

    /// <summary>Serialise the active profile exactly as it is persisted, for a
    /// user-downloadable backup. Uses the same options as <see cref="Save"/> so
    /// a round-trip re-imports byte-for-byte.</summary>
    public string ExportActiveJson() => JsonSerializer.Serialize(_activeProfile, JsonOpts);

    /// <summary>Replace the entire active profile from a restored backup and
    /// persist it. This is the "restore all settings" counterpart to
    /// <see cref="ExportActiveJson"/>. The name is preserved from the file.
    /// Throws on unparseable input so the endpoint can return 400 instead of
    /// silently wiping the profile.</summary>
    public void ImportProfile(string json) {
        var p = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts)
            ?? throw new InvalidDataException("The settings file is empty or not a Polaris profile.");
        _activeProfile = p;
        Save();
        _logger.LogWarning("Imported full settings backup: {Name} ({Rigs} rig(s))",
            _activeProfile.Name, _activeProfile.EquipmentProfiles.Count);
    }

    /// <summary>Restore a set of rigs from a backup file, merging by Id: an
    /// incoming rig whose Id already exists overwrites that rig, otherwise it is
    /// added. Non-destructive to rigs not present in the file, so restoring a
    /// single lost rig never wipes the others. Sets the active rig when the
    /// backup names one that survives the merge. Returns how many were applied.</summary>
    public int ImportEquipmentProfiles(IEnumerable<EquipmentProfile> incoming, string? activeId) {
        EnsureMigratedToEquipmentProfiles();
        int applied = 0;
        foreach (var rig in incoming) {
            if (rig == null) continue;
            if (string.IsNullOrWhiteSpace(rig.Id)) rig.Id = Guid.NewGuid().ToString("N");
            var idx = _activeProfile.EquipmentProfiles.FindIndex(e => e.Id == rig.Id);
            if (idx >= 0) _activeProfile.EquipmentProfiles[idx] = rig;
            else _activeProfile.EquipmentProfiles.Add(rig);
            applied++;
        }
        if (!string.IsNullOrWhiteSpace(activeId) &&
            _activeProfile.EquipmentProfiles.Any(e => e.Id == activeId))
            _activeProfile.ActiveEquipmentProfileId = activeId;
        Save();
        _logger.LogInformation("Imported {Count} rig(s) from backup", applied);
        return applied;
    }

    // ----- Equipment profile (rig) management -----

    /// <summary>The currently-active equipment rig, creating a "Default" rig
    /// from the legacy LastXxx fields if the user has never used this feature
    /// before.</summary>
    public EquipmentProfile ActiveEquipmentProfile {
        get {
            EnsureMigratedToEquipmentProfiles();
            var id = _activeProfile.ActiveEquipmentProfileId;
            return _activeProfile.EquipmentProfiles.FirstOrDefault(e => e.Id == id)
                ?? _activeProfile.EquipmentProfiles[0];
        }
    }

    public List<EquipmentProfile> ListEquipmentProfiles() {
        EnsureMigratedToEquipmentProfiles();
        return _activeProfile.EquipmentProfiles.ToList();
    }

    public EquipmentProfile CreateEquipmentProfile(string name) {
        EnsureMigratedToEquipmentProfiles();
        var existing = _activeProfile.EquipmentProfiles
            .FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        // RIGPUT-1: the per-rig value-type fields are nullable (so a partial PUT
        // can't reset them). A genuinely NEW rig gets the sensible defaults set
        // HERE — otherwise null would resolve to each read site's fallback, which
        // for offset is 0 (camera default) rather than the intended 50, and a
        // fresh rig would run at black-level-clipping offset 0.
        var rig = new EquipmentProfile {
            Name = name,
            DefaultGain = 100,
            DefaultOffset = 50,
            DefaultBinning = 1,
            CoolerTargetTemperature = -10,
            FocuserStepSize = 50,
            FocuserBacklashSteps = 0,
            VerticalFlipImage = false
        };
        _activeProfile.EquipmentProfiles.Add(rig);
        Save();
        _logger.LogInformation("Created equipment profile: {Name}", name);
        return rig;
    }

    /// <summary>Save the active rig's current values under a new name without
    /// switching to it.</summary>
    public EquipmentProfile CloneActiveRigAs(string newName) {
        var src = ActiveEquipmentProfile;
        var copy = new EquipmentProfile {
            Name = newName,
            Camera = src.Camera, CameraDriver = src.CameraDriver,
            Telescope = src.Telescope, TelescopeDriver = src.TelescopeDriver,
            Focuser = src.Focuser, FocuserDriver = src.FocuserDriver,
            FilterWheel = src.FilterWheel, FilterWheelDriver = src.FilterWheelDriver,
            Rotator = src.Rotator,
            FlatDevice = src.FlatDevice, Dome = src.Dome, Weather = src.Weather,
            CoolerTargetTemperature = src.CoolerTargetTemperature,
            CoolerRampDegPerMinute = src.CoolerRampDegPerMinute,
            DefaultGain = src.DefaultGain, DefaultOffset = src.DefaultOffset,
            DefaultBinning = src.DefaultBinning,
            BayerPatternOverride = src.BayerPatternOverride,
            VerticalFlipImage = src.VerticalFlipImage,
            FocuserStepSize = src.FocuserStepSize,
            FocuserBacklashSteps = src.FocuserBacklashSteps,
            // AFPORT: per-rig autofocus tuning travels with the clone.
            AutoFocus = src.AutoFocus == null ? null : new AutoFocusSettings {
                StepSize = src.AutoFocus.StepSize,
                OffsetSteps = src.AutoFocus.OffsetSteps,
                ExposureSeconds = src.AutoFocus.ExposureSeconds,
                FramesPerPoint = src.AutoFocus.FramesPerPoint,
                Method = src.AutoFocus.Method,
                RSquaredThreshold = src.AutoFocus.RSquaredThreshold,
                Attempts = src.AutoFocus.Attempts,
                MaxHfrRatio = src.AutoFocus.MaxHfrRatio,
                InnerCropRatio = src.AutoFocus.InnerCropRatio,
                UseBrightestStars = src.AutoFocus.UseBrightestStars,
                BacklashIn = src.AutoFocus.BacklashIn,
                BacklashOut = src.AutoFocus.BacklashOut,
                BacklashModel = src.AutoFocus.BacklashModel,
                MinStars = src.AutoFocus.MinStars,
                FilterMemoryEnabled = src.AutoFocus.FilterMemoryEnabled,
                FilterMemoryAutoApply = src.AutoFocus.FilterMemoryAutoApply,
                FilterMemoryAutoRunWhenStale = src.AutoFocus.FilterMemoryAutoRunWhenStale,
                FilterMemoryTempToleranceC = src.AutoFocus.FilterMemoryTempToleranceC,
                FilterMemoryMaxAgeHours = src.AutoFocus.FilterMemoryMaxAgeHours,
                FilterOffsetReference = src.AutoFocus.FilterOffsetReference
            },
            FocalLengthMm = src.FocalLengthMm,
            ApertureMm = src.ApertureMm,
            TelescopeBrand = src.TelescopeBrand,
            TelescopeModel = src.TelescopeModel,
            AccessoryType = src.AccessoryType,
            AccessoryModel = src.AccessoryModel,
            AccessoryFactor = src.AccessoryFactor,
            RequiredBackspacingMm = src.RequiredBackspacingMm,
            CameraPixelSizeUm = src.CameraPixelSizeUm,
            CameraMaxX = src.CameraMaxX,
            CameraMaxY = src.CameraMaxY,
            CameraBitDepth = src.CameraBitDepth,
            GuiderFocalLengthMm = src.GuiderFocalLengthMm,
            // Auxiliary (second) camera + its optics/focuser.
            AuxCamera = src.AuxCamera, AuxCameraDriver = src.AuxCameraDriver,
            AuxFocalLengthMm = src.AuxFocalLengthMm,
            AuxCameraPixelSizeUm = src.AuxCameraPixelSizeUm,
            AuxCameraMaxX = src.AuxCameraMaxX,
            AuxCameraMaxY = src.AuxCameraMaxY,
            AuxCameraBitDepth = src.AuxCameraBitDepth,
            AuxApertureMm = src.AuxApertureMm,
            AuxTelescopeBrand = src.AuxTelescopeBrand,
            AuxTelescopeModel = src.AuxTelescopeModel,
            AuxExposureMs = src.AuxExposureMs,
            AuxGain = src.AuxGain,
            AuxBinning = src.AuxBinning,
            AuxEnabled = src.AuxEnabled,
            AuxFocuser = src.AuxFocuser, AuxFocuserDriver = src.AuxFocuserDriver,
            GuideFocuser = src.GuideFocuser, GuideFocuserDriver = src.GuideFocuserDriver,
            // Native guider backend selection + tunables.
            GuiderDriver = src.GuiderDriver,
            GuideCamera = src.GuideCamera, GuideCameraDriver = src.GuideCameraDriver,
            NativeGuideExposureMs = src.NativeGuideExposureMs,
            NativeCalibrationStepMs = src.NativeCalibrationStepMs,
            NativeMinMoveRaPx = src.NativeMinMoveRaPx,
            NativeMinMoveDecPx = src.NativeMinMoveDecPx,
            NativeRaAggression = src.NativeRaAggression,
            NativeDecAggression = src.NativeDecAggression,
            NativeRaHysteresis = src.NativeRaHysteresis,
            NativeMaxRaDurationMs = src.NativeMaxRaDurationMs,
            NativeMaxDecDurationMs = src.NativeMaxDecDurationMs,
            NativeRaAlgorithm = src.NativeRaAlgorithm,
            NativeDecAlgorithm = src.NativeDecAlgorithm,
            NativeDecGuideMode = src.NativeDecGuideMode,
            NativePredictiveWormPeriodSec = src.NativePredictiveWormPeriodSec,
            NativePredictiveWindowSamples = src.NativePredictiveWindowSamples,
            NativePredictiveBlend = src.NativePredictiveBlend,
            NativeBacklashComp = src.NativeBacklashComp,
            NativeBacklashMaxMs = src.NativeBacklashMaxMs,
            NativeMultiStar = src.NativeMultiStar,
            NativeMaxGuideStars = src.NativeMaxGuideStars,
            NativePierSideHandling = src.NativePierSideHandling,
            NativeReverseDecAfterFlip = src.NativeReverseDecAfterFlip,
            // A cloned rig starts un-calibrated (geometry differs per setup).
            NativeCalibration = null,
            NativeCalibrations = new(),
            NativeGuideGain = src.NativeGuideGain,
            NativeGuideBin = src.NativeGuideBin,
            PHD2Host = src.PHD2Host, PHD2Port = src.PHD2Port,
            // PHD2 deep-integration fields (cloned rig starts un-matched,
            // it will run its own first-time profile lookup the first time
            // it activates).
            PHD2ProfileId = null,
            PHD2AlgoPreset = src.PHD2AlgoPreset,
            PHD2CalibrationStepMsOverride = src.PHD2CalibrationStepMsOverride,
            PHD2AutoSyncOnRigSwitch = src.PHD2AutoSyncOnRigSwitch,
            PHD2CustomAlgoParams = new Dictionary<string, double>(src.PHD2CustomAlgoParams),
            FilterOffsets = new Dictionary<string, int>(src.FilterOffsets),
            // Learned per-filter focus memory travels with the clone (deep copy
            // of each entry) so a Save-As keeps the setup's focus history.
            FilterFocusMemory = src.FilterFocusMemory.ToDictionary(
                kv => kv.Key,
                kv => new FilterFocusMemory {
                    Position = kv.Value.Position,
                    TemperatureC = kv.Value.TemperatureC,
                    Utc = kv.Value.Utc,
                    FocuserName = kv.Value.FocuserName,
                    Hfr = kv.Value.Hfr
                }),
            // Control panels travel with the clone (deep copy of each panel and
            // its widgets) so a Save-As keeps the operator's SCADA layout.
            ControlPanels = src.ControlPanels.Select(p => new ControlPanelDef {
                Id = p.Id, Title = p.Title, Visible = p.Visible,
                Left = p.Left, Top = p.Top, Width = p.Width, Height = p.Height, Z = p.Z,
                Dock = p.Dock, DockOrder = p.DockOrder,
                Widgets = p.Widgets.Select(w => new ControlWidgetDef {
                    Id = w.Id, Label = w.Label, Kind = w.Kind, Source = w.Source,
                    Path = w.Path, ChannelKey = w.ChannelKey, ControlId = w.ControlId, Which = w.Which,
                    Device = w.Device, Property = w.Property, Element = w.Element, Action = w.Action,
                    Unit = w.Unit, Min = w.Min, Max = w.Max, Step = w.Step, Decimals = w.Decimals
                }).ToList()
            }).ToList(),
            // FILTERNAME: a cloned rig keeps the operator's filter labels too,
            // not just the offsets — otherwise the copy comes up with the
            // driver's raw names and the offsets (keyed by the saved names) miss.
            FilterNames = (string[])(src.FilterNames ?? System.Array.Empty<string>()).Clone(),
            // Live-stack triggers, clone the whole shape so the new rig
            // gets the same refocus/recenter policy as the source. Reset
            // counters live on the orchestrator, not the settings.
            LiveStackTriggers = new LiveStackTriggers {
                RefocusEnabled = src.LiveStackTriggers.RefocusEnabled,
                RefocusEveryNFrames = src.LiveStackTriggers.RefocusEveryNFrames,
                RefocusEveryMinutes = src.LiveStackTriggers.RefocusEveryMinutes,
                RefocusTempDeltaC = src.LiveStackTriggers.RefocusTempDeltaC,
                RefocusHfrIncreasePercent = src.LiveStackTriggers.RefocusHfrIncreasePercent,
                RefocusRequest = src.LiveStackTriggers.RefocusRequest,
                RecenterEnabled = src.LiveStackTriggers.RecenterEnabled,
                RecenterEveryNFrames = src.LiveStackTriggers.RecenterEveryNFrames,
                RecenterEveryMinutes = src.LiveStackTriggers.RecenterEveryMinutes,
                RecenterDriftArcsec = src.LiveStackTriggers.RecenterDriftArcsec,
                RecenterToleranceArcsec = src.LiveStackTriggers.RecenterToleranceArcsec
            },
            // FW-1: Flat Wizard per-rig defaults (TargetADU, tolerance,
            // frame count, exposure bounds, binning, max iterations,
            // panel brightness). Same rationale as LiveStackTriggers:
            // different scope/aperture pairs converge to different
            // exposures + flat-field workflows.
            FlatWizard = new FlatWizardSettings {
                TargetAdu = src.FlatWizard.TargetAdu,
                Tolerance = src.FlatWizard.Tolerance,
                FramesPerFilter = src.FlatWizard.FramesPerFilter,
                MinExposureSec = src.FlatWizard.MinExposureSec,
                MaxExposureSec = src.FlatWizard.MaxExposureSec,
                Binning = src.FlatWizard.Binning,
                MaxSearchIterations = src.FlatWizard.MaxSearchIterations,
                PanelBrightness = src.FlatWizard.PanelBrightness
            },
            NativeGuideCalibrationMode = src.NativeGuideCalibrationMode,
            NativeGuideDarkFrames = src.NativeGuideDarkFrames,
            LiveStackSaveFramesToDisk = src.LiveStackSaveFramesToDisk,
            LiveStackColor = src.LiveStackColor,
            LiveStackSigmaRejection = src.LiveStackSigmaRejection,
            LiveStackSigmaKappa = src.LiveStackSigmaKappa,
            LiveStackMaxDurationSeconds = src.LiveStackMaxDurationSeconds,
            TargetSnr = src.TargetSnr,
            // LSPP-3: per-frame pre-processing (calibration + BGE).
            // Clone the shape so the new rig inherits the source's
            // enable flags + master overrides + BGE knobs.
            LiveStackPreProcessing = new LiveStackPreProcSettings {
                CalibrationEnabled    = src.LiveStackPreProcessing.CalibrationEnabled,
                MasterDarkOverrideId  = src.LiveStackPreProcessing.MasterDarkOverrideId,
                MasterFlatOverrideId  = src.LiveStackPreProcessing.MasterFlatOverrideId,
                MasterBiasOverrideId  = src.LiveStackPreProcessing.MasterBiasOverrideId,
                BgeEnabled            = src.LiveStackPreProcessing.BgeEnabled,
                BgeSmoothing          = src.LiveStackPreProcessing.BgeSmoothing,
                BgeCorrection         = src.LiveStackPreProcessing.BgeCorrection
            },
            // INDIROB-3: pre-connect delays follow the rig — different
            // setups (mini-PC vs Pi, USB hub topology, ESP32 vs FTDI
            // bridges) have different settling needs.
            PreConnectDelayMsByDevice = new Dictionary<string, int>(src.PreConnectDelayMsByDevice),
            GuideRunawayRestart = src.GuideRunawayRestart,
            GuideRunawayRmsArcsec = src.GuideRunawayRmsArcsec,
            // Per-rig anti-crash slew guards follow the clone.
            SlewConfirmDeg = src.SlewConfirmDeg,
            SlewFloorDeg = src.SlewFloorDeg,
            FlipFloorDeg = src.FlipFloorDeg,
            AutoSyncMountBeforeSlew = src.AutoSyncMountBeforeSlew
        };
        _activeProfile.EquipmentProfiles.Add(copy);
        Save();
        return copy;
    }

    /// <summary>Fired after an edit to the rig that is CURRENTLY ACTIVE.
    /// Distinct from <see cref="EquipmentProfileActivated"/>, which only
    /// fires when the operator switches rigs: some runtime state is derived
    /// from per-rig FIELDS and has to be recomputed when one of them changes
    /// under the same rig. The live-stack compute mode is the case that
    /// prompted this: changing it in the UI left the running mode untouched
    /// until the next client connect or rig switch.
    /// Handlers run on the calling thread, keep them fast.</summary>
    public event Action<EquipmentProfile>? ActiveEquipmentProfileEdited;

    public bool UpdateEquipmentProfile(string id, Action<EquipmentProfile> update) {
        EnsureMigratedToEquipmentProfiles();
        var rig = _activeProfile.EquipmentProfiles.FirstOrDefault(e => e.Id == id);
        if (rig == null) return false;
        update(rig);
        Save();
        if (id == _activeProfile.ActiveEquipmentProfileId) {
            try { ActiveEquipmentProfileEdited?.Invoke(rig); }
            catch (Exception ex) { _logger.LogWarning(ex, "ActiveEquipmentProfileEdited handler threw"); }
        }
        return true;
    }

    public bool RenameEquipmentProfile(string id, string newName) {
        return UpdateEquipmentProfile(id, r => r.Name = newName);
    }

    public bool DeleteEquipmentProfile(string id) {
        EnsureMigratedToEquipmentProfiles();
        if (_activeProfile.EquipmentProfiles.Count <= 1) return false; // never delete the last one
        var rig = _activeProfile.EquipmentProfiles.FirstOrDefault(e => e.Id == id);
        if (rig == null) return false;
        _activeProfile.EquipmentProfiles.Remove(rig);
        if (_activeProfile.ActiveEquipmentProfileId == id)
            _activeProfile.ActiveEquipmentProfileId = _activeProfile.EquipmentProfiles[0].Id;
        Save();
        return true;
    }

    /// <summary>
    /// Fired after a rig is successfully activated and persisted.
    /// PHD2ProfileSyncService subscribes here to push the matching PHD2
    /// profile + apply algo presets when AutoSyncOnRigSwitch is true.
    /// Event handlers run on the calling thread, keep them fast (do
    /// long work via Task.Run / fire-and-forget).
    /// </summary>
    public event Action<EquipmentProfile>? EquipmentProfileActivated;

    public bool ActivateEquipmentProfile(string id) {
        EnsureMigratedToEquipmentProfiles();
        var rig = _activeProfile.EquipmentProfiles.FirstOrDefault(e => e.Id == id);
        if (rig == null) return false;
        _activeProfile.ActiveEquipmentProfileId = id;
        Save();
        _logger.LogInformation("Activated equipment profile {Id}", id);
        try { EquipmentProfileActivated?.Invoke(rig); }
        catch (Exception ex) { _logger.LogWarning(ex, "EquipmentProfileActivated handler threw"); }
        return true;
    }

    /// <summary>FIELD4-3: get the quirks (Bayer override + vertical
    /// flip) for the currently-active rig's camera id. Returns an
    /// empty <see cref="CameraQuirks"/> (both fields off) when no
    /// camera is selected or no entry exists for that id yet. The
    /// active camera's quirks are looked up by string match against
    /// <see cref="EquipmentProfile.Camera"/>, so the same physical
    /// camera shared across rigs gets the same workaround
    /// automatically.</summary>
    public CameraQuirks GetActiveCameraQuirks() {
        var id = ActiveEquipmentProfile?.Camera;
        if (string.IsNullOrWhiteSpace(id)) return new CameraQuirks();
        return _activeProfile.CameraQuirks.TryGetValue(id, out var q)
            ? q
            : new CameraQuirks();
    }

    /// <summary>FIELD4-3: get-or-create the quirks entry for a given
    /// camera id. Used by the camera-quirks PUT endpoint. Does not
    /// persist; caller is responsible for calling Save() once the
    /// edit is applied (the endpoint does).</summary>
    public CameraQuirks GetOrCreateCameraQuirks(string cameraId) {
        if (!_activeProfile.CameraQuirks.TryGetValue(cameraId, out var q)) {
            q = new CameraQuirks();
            _activeProfile.CameraQuirks[cameraId] = q;
        }
        return q;
    }

    /// <summary>FIELD4-3: snapshot of the full per-camera quirks
    /// map. Returns a copy of the keys (so a caller can iterate
    /// without worrying about concurrent edits) plus the live
    /// CameraQuirks references (callers shouldn't mutate). Consumed
    /// by the camera-quirks GET endpoint and by the RIGS tab UI to
    /// populate the table.</summary>
    public IReadOnlyDictionary<string, CameraQuirks> ListCameraQuirks() {
        return new Dictionary<string, CameraQuirks>(_activeProfile.CameraQuirks);
    }

    /// <summary>AFPORT: seed a missing per-rig <see cref="AutoFocusSettings"/>
    /// block from the legacy FocuserStepSize / FocuserBacklashSteps fields.
    /// Runs on every load but only touches rigs whose block is null, so a
    /// user's later edits are never overwritten.</summary>
    private void MigrateAutoFocusSettings() {
        if (_activeProfile.EquipmentProfiles == null) return;
        var seeded = 0;
        foreach (var rig in _activeProfile.EquipmentProfiles) {
            if (rig.AutoFocus != null) continue;
            rig.AutoFocus = new AutoFocusSettings {
                StepSize = (rig.FocuserStepSize ?? 0) > 0 ? rig.FocuserStepSize!.Value : 50,
                BacklashIn = Math.Max(0, rig.FocuserBacklashSteps ?? 0)
            };
            seeded++;
        }
        if (seeded > 0) {
            Save();
            _logger.LogInformation(
                "AF settings migration: seeded AutoFocus block on {N} rig(s) from legacy focuser fields", seeded);
        }
    }

    /// <summary>Guide exposures shorter than the 0.5 s product minimum are
    /// junk left behind by the stale-rig-PUT echo bug (a full-rig save from a
    /// client with an outdated rigs list kept reverting the exposure to an
    /// ancient 100 ms). Re-seed them to the 1 s default. Runs on every load;
    /// idempotent because valid values are never touched.</summary>
    private void MigrateGuideExposureFloor() {
        if (_activeProfile.EquipmentProfiles == null) return;
        var fixedUp = 0;
        foreach (var rig in _activeProfile.EquipmentProfiles) {
            if (rig.NativeGuideExposureMs is > 0 and < 500) {
                rig.NativeGuideExposureMs = 1000;
                fixedUp++;
            }
        }
        if (fixedUp > 0) {
            Save();
            _logger.LogInformation(
                "Guide exposure migration: re-seeded {N} rig(s) with sub-500ms guide exposure to 1s", fixedUp);
        }
    }

    /// <summary>One-time re-seed of the auto-focus star count. Every rig saved
    /// before the single-star change holds a 0, "measure every detected star",
    /// which is the behaviour the field report showed to be wrong: the mean
    /// followed whichever stars the detector found at each position rather than
    /// the defocus. Seeds 1 exactly once, so someone who deliberately sets 0 (or
    /// any other count) afterwards keeps it.</summary>
    private void MigrateAutoFocusSingleStar() {
        const int SingleStarVersion = 2;
        if (_activeProfile.SettingsMigration >= SingleStarVersion) return;
        var seeded = 0;
        foreach (var rig in _activeProfile.EquipmentProfiles ?? new()) {
            if (rig.AutoFocus != null && rig.AutoFocus.UseBrightestStars <= 0) {
                rig.AutoFocus.UseBrightestStars = 1;
                seeded++;
            }
        }
        _activeProfile.SettingsMigration = SingleStarVersion;
        Save();
        _logger.LogInformation(
            "Settings migration v{V}: auto-focus set to track 1 star on {N} rig(s) (one-time)",
            SingleStarVersion, seeded);
    }

    /// <summary>One-time re-seed of the logging defaults on profiles saved by
    /// builds where LogToDisk/SaveGuideLogs defaulted OFF (those builds wrote
    /// `false` into active.json, which then shadowed the new default-ON
    /// initializers forever). Guarded by <see cref="UserProfile.SettingsMigration"/>
    /// so it runs once — a deliberate opt-out made after the migration is
    /// respected on every later load.</summary>
    private void MigrateLoggingDefaults() {
        const int LoggingDefaultsOnVersion = 1;
        if (_activeProfile.SettingsMigration >= LoggingDefaultsOnVersion) return;
        _activeProfile.LogToDisk = true;
        _activeProfile.SaveGuideLogs = true;
        _activeProfile.SettingsMigration = LoggingDefaultsOnVersion;
        Save();
        _logger.LogInformation(
            "Settings migration v{V}: LogToDisk + SaveGuideLogs re-seeded ON (one-time)",
            LoggingDefaultsOnVersion);
    }

    /// <summary>FIELD4-3: hoist legacy per-rig BayerPatternOverride
    /// and VerticalFlipImage values into the user-profile-level
    /// CameraQuirks map keyed by the rig's camera id. Skips any
    /// camera id already present in the map so a later edit to the
    /// new field isn't overwritten by a stale legacy field on an
    /// old rig. Runs once on Load(). Safe to keep running on every
    /// boot, it's idempotent (the if-present guard).</summary>
    private void MigrateLegacyCameraQuirks() {
        if (_activeProfile.EquipmentProfiles == null) return;
        var migrated = 0;
        foreach (var rig in _activeProfile.EquipmentProfiles) {
            if (string.IsNullOrWhiteSpace(rig.Camera)) continue;
            if (_activeProfile.CameraQuirks.ContainsKey(rig.Camera)) continue;

            var hasBayer = !string.IsNullOrWhiteSpace(rig.BayerPatternOverride);
            var hasFlip = rig.VerticalFlipImage ?? false;
            if (!hasBayer && !hasFlip) continue;

            _activeProfile.CameraQuirks[rig.Camera] = new CameraQuirks {
                BayerPatternOverride = hasBayer ? rig.BayerPatternOverride : null,
                VerticalFlipImage = hasFlip
            };
            migrated++;
        }
        if (migrated > 0) {
            _logger.LogInformation(
                "Migrated {Count} legacy per-rig Bayer/flip override(s) into per-camera quirks",
                migrated);
            Save();
        }
    }

    /// <summary>INDIPROP: snapshot copy of the operator's INDI property
    /// help notes (keyed by INDI property name). Returned to the INDI
    /// control panel so it can show the operator's own text in the
    /// help tooltip / editor. Copy so callers can't mutate the live
    /// map.</summary>
    public IReadOnlyDictionary<string, string> GetIndiPropertyNotes() {
        return new Dictionary<string, string>(_activeProfile.IndiPropertyNotes);
    }

    /// <summary>INDIPROP: set or clear the operator's help note for one
    /// INDI property (keyed by property name, e.g. "CCD_TEMPERATURE").
    /// A null/whitespace text removes the note so the built-in English
    /// dictionary entry (if any) takes over again. Persists immediately
    /// via Save().</summary>
    public void SetIndiPropertyNote(string property, string? text) {
        if (string.IsNullOrWhiteSpace(property)) return;
        var key = property.Trim();
        if (string.IsNullOrWhiteSpace(text)) {
            _activeProfile.IndiPropertyNotes.Remove(key);
        } else {
            _activeProfile.IndiPropertyNotes[key] = text.Trim();
        }
        Save();
    }

    /// <summary>On first run (or upgrade from a pre-rig profile), create a
    /// "Default" rig populated from the legacy LastXxx fields.</summary>
    private void EnsureMigratedToEquipmentProfiles() {
        if (_activeProfile.EquipmentProfiles.Count > 0) return;
        var rig = new EquipmentProfile {
            Name = "Default",
            Camera = _activeProfile.LastCamera,
            Telescope = _activeProfile.LastTelescope,
            Focuser = _activeProfile.LastFocuser,
            FilterWheel = _activeProfile.LastFilterWheel,
            FocalLengthMm = _activeProfile.FocalLengthMm,
            // Carry the legacy top-level defaults; fill the rest with the same
            // sensible values a new rig gets (nullable fields, RIGPUT-1).
            DefaultGain = _activeProfile.DefaultGain,
            DefaultBinning = _activeProfile.DefaultBinning,
            DefaultOffset = 50,
            CoolerTargetTemperature = -10,
            FocuserStepSize = 50,
            FocuserBacklashSteps = 0,
            VerticalFlipImage = false
        };
        _activeProfile.EquipmentProfiles.Add(rig);
        _activeProfile.ActiveEquipmentProfileId = rig.Id;
        Save();
        _logger.LogInformation("Migrated legacy equipment selection into Default rig");
    }

    private static string SanitizeFileName(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray())
            .Replace(' ', '_').ToLowerInvariant();
    }
}
