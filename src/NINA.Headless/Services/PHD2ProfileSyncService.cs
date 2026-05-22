namespace NINA.Headless.Services;

/// <summary>
/// Keeps each Polaris rig in 1:1 sync with a PHD2 profile of the same name.
/// Listens to ProfileService.EquipmentProfileActivated and, when the rig
/// has AutoSyncOnRigSwitch=true, switches PHD2 to the matching profile +
/// applies the rig's algorithm preset + per-rig custom param overrides.
///
/// We CAN'T create PHD2 profiles via JSON-RPC (the API doesn't have a
/// new_profile method). When the name doesn't match, we surface that to
/// the UI so the user can open the embedded PHD2 GUI (xpra panel) and
/// run the wizard. After that the next sync attempt picks it up.
/// </summary>
public class PHD2ProfileSyncService : IDisposable {
    private readonly PHD2Client _phd2;
    private readonly ProfileService _profiles;
    private readonly ILogger<PHD2ProfileSyncService> _logger;
    private readonly object _statusLock = new();

    public SyncStatus CurrentStatus { get; private set; } = new(
        RigId: "", RigName: "", Phase: "idle", ProfileId: null,
        Error: null, ProfileMissing: false, At: DateTime.UtcNow);

    public event Action<SyncStatus>? StatusChanged;

    public PHD2ProfileSyncService(PHD2Client phd2,
                                  ProfileService profiles,
                                  ILogger<PHD2ProfileSyncService> logger) {
        _phd2 = phd2;
        _profiles = profiles;
        _logger = logger;
        _profiles.EquipmentProfileActivated += OnRigActivated;
    }

    private void OnRigActivated(EquipmentProfile rig) {
        if (!rig.PHD2AutoSyncOnRigSwitch) {
            _logger.LogDebug("Rig {Name} activated; AutoSync disabled, skipping PHD2 sync", rig.Name);
            return;
        }
        // Fire-and-forget so we don't block the rig save path. Use Task.Run
        // since the activation event runs on the caller's thread.
        _ = Task.Run(async () => {
            try { await SyncRigToProfileAsync(rig, CancellationToken.None); }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Background rig sync failed for {Name}", rig.Name);
            }
        });
    }

    /// <summary>
    /// Pushes the given rig to PHD2: matches the profile by name,
    /// switches if needed, then applies the algo preset + per-rig
    /// overrides. Safe to call when PHD2 is disconnected (returns
    /// warning, no-op).
    /// </summary>
    public async Task<SyncResult> SyncRigToProfileAsync(EquipmentProfile rig, CancellationToken ct) {
        var warnings = new List<string>();

        if (!_phd2.IsConnected) {
            UpdateStatus(rig, "phd2-disconnected", null, null,
                "PHD2 not connected — skipping rig sync", false);
            return new SyncResult(Ok: false, Error: "PHD2 not connected",
                ProfileId: null, ProfileMissing: false, Warnings: warnings);
        }

        UpdateStatus(rig, "looking-up", null, null, null, false);

        // 1. Match profile by name (case-insensitive). PHD2 profile names
        //    are typically already case-sensitive, but we're lenient on
        //    lookup to match user expectations.
        List<PHD2Profile> profiles;
        try {
            profiles = await _phd2.GetProfilesAsync(ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "GetProfilesAsync failed");
            UpdateStatus(rig, "error", null, null,
                "Could not enumerate PHD2 profiles: " + ex.Message, false);
            return new SyncResult(false, ex.Message, null, false, warnings);
        }

        var match = profiles.FirstOrDefault(p =>
            string.Equals(p.Name, rig.Name, StringComparison.OrdinalIgnoreCase));

        if (match == null) {
            UpdateStatus(rig, "missing-profile", null, null,
                $"No PHD2 profile named '{rig.Name}' — create one in the PHD2 GUI tab",
                ProfileMissing: true);
            return new SyncResult(
                Ok: false,
                Error: $"PHD2 profile '{rig.Name}' not found",
                ProfileId: null,
                ProfileMissing: true,
                Warnings: warnings);
        }

        // Cache the matched id back to the rig so subsequent activations
        // and the UI can show "linked to profile #N".
        if (rig.PHD2ProfileId != match.Id) {
            _profiles.UpdateEquipmentProfile(rig.Id, r => r.PHD2ProfileId = match.Id);
            rig.PHD2ProfileId = match.Id;
        }

        // 2. Switch if not already active. SetProfileAsync handles
        //    equipment disconnect internally; we don't need to do that.
        int? currentId = null;
        try {
            var current = await _phd2.GetCurrentProfileAsync(ct);
            currentId = current?.Id;
        } catch (Exception ex) { _logger.LogDebug(ex, "get_profile failed (continuing)"); }

        if (currentId != match.Id) {
            UpdateStatus(rig, "switching", match.Id, null, null, false);
            try {
                await _phd2.SetProfileAsync(match.Id, ct);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "set_profile failed");
                UpdateStatus(rig, "error", match.Id, null,
                    "set_profile failed: " + ex.Message, false);
                return new SyncResult(false, ex.Message, match.Id, false, warnings);
            }
        }

        // 3. Apply the rig's algorithm preset (skip silently for params
        //    the current PHD2 algorithm doesn't expose).
        UpdateStatus(rig, "applying-preset", match.Id, null, null, false);
        await ApplyAlgoPresetAsync(rig, warnings, ct);

        UpdateStatus(rig, "ok", match.Id, null, null, false);
        return new SyncResult(true, null, match.Id, false, warnings);
    }

    private async Task ApplyAlgoPresetAsync(EquipmentProfile rig, List<string> warnings, CancellationToken ct) {
        // Built-in preset OR custom bag — exactly one path.
        if (string.Equals(rig.PHD2AlgoPreset, PHD2AlgoPresets.CustomPresetName, StringComparison.OrdinalIgnoreCase)
            && rig.PHD2CustomAlgoParams.Count > 0) {
            foreach (var kv in rig.PHD2CustomAlgoParams) {
                // Keys are "axis:param" — split once.
                var sep = kv.Key.IndexOf(':');
                if (sep <= 0) { warnings.Add($"Skipping malformed custom key '{kv.Key}'"); continue; }
                var axis = kv.Key.Substring(0, sep);
                var name = kv.Key.Substring(sep + 1);
                var ok = await _phd2.SetAlgoParamAsync(axis, name, kv.Value, ct);
                if (!ok) warnings.Add($"Skipped {axis}/{name} (algo doesn't expose it)");
            }
            return;
        }

        var preset = PHD2AlgoPresets.GetBuiltin(rig.PHD2AlgoPreset);
        if (preset == null) {
            warnings.Add($"Unknown preset '{rig.PHD2AlgoPreset}' — leaving PHD2 algorithm params untouched");
            return;
        }
        foreach (var p in preset.Params) {
            var ok = await _phd2.SetAlgoParamAsync(p.Axis, p.Name, p.Value, ct);
            if (!ok) warnings.Add($"Skipped {p.Axis}/{p.Name} (algo doesn't expose it)");
        }
    }

    private void UpdateStatus(EquipmentProfile rig, string phase, int? profileId,
                              string? _unused, string? error, bool ProfileMissing) {
        SyncStatus snapshot;
        lock (_statusLock) {
            snapshot = new SyncStatus(
                RigId: rig.Id, RigName: rig.Name, Phase: phase,
                ProfileId: profileId, Error: error,
                ProfileMissing: ProfileMissing, At: DateTime.UtcNow);
            CurrentStatus = snapshot;
        }
        try { StatusChanged?.Invoke(snapshot); }
        catch (Exception ex) { _logger.LogDebug(ex, "StatusChanged handler threw"); }
    }

    public void Dispose() {
        _profiles.EquipmentProfileActivated -= OnRigActivated;
    }
}

/// <summary>Result of an explicit SyncRigToProfileAsync call.</summary>
public record SyncResult(
    bool Ok,
    string? Error,
    int? ProfileId,
    bool ProfileMissing,
    List<string> Warnings);

/// <summary>Snapshot streamed over /ws/status as guider.profileSync.</summary>
public record SyncStatus(
    string RigId,
    string RigName,
    string Phase,
    int? ProfileId,
    string? Error,
    bool ProfileMissing,
    DateTime At);
