namespace NINA.Polaris.Services;

/// <summary>
/// Routes generic guider operations to whichever backend the active rig
/// selected. PHD2 is the default; a rig opts into the native autoguider
/// by setting <c>EquipmentProfile.GuiderDriver == "native"</c>.
///
/// <para>GuiderEndpoints and the status WebSocket resolve <see cref="Active"/>
/// for every generic call so the switch is transparent to the frontend.
/// PHD2-only routes (profiles, GUI/VNC sessions, algo presets, smart
/// calibrate, process lifecycle) stay bound to <see cref="PHD2Client"/>
/// directly.</para>
/// </summary>
public sealed class ActiveGuiderProvider {
    private readonly ProfileService _profiles;
    private readonly PHD2Client _phd2;
    private readonly NativeGuider _native;

    public ActiveGuiderProvider(ProfileService profiles, PHD2Client phd2, NativeGuider native) {
        _profiles = profiles;
        _phd2 = phd2;
        _native = native;
    }

    /// <summary>The guider backend the active rig is configured to use.</summary>
    public IGuider Active =>
        string.Equals(_profiles.ActiveEquipmentProfile.GuiderDriver, "native",
            StringComparison.OrdinalIgnoreCase)
            ? _native
            : _phd2;
}
