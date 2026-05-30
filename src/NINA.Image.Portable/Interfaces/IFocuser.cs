namespace NINA.Image.Interfaces;

/// <summary>
/// Common contract every focuser backend honours so EquipmentManager,
/// AutoFocusService, the meridian-flip resume hook, and the live-stack
/// triggers orchestrator stay backend-agnostic. Implementations:
/// <c>IndiFocuser</c> (any INDI-supported focuser, the common path),
/// <c>AscomComFocuser</c> (direct COM on Windows, MoonLite / Pegasus /
/// ZWO EAF natively without ASCOM Remote in the way).
///
/// <para>Properties that don't apply to a given device should return a
/// neutral value (zero / NaN). Temperature, in particular, is null for
/// focusers without an onboard probe.</para>
/// </summary>
public interface IFocuser {
    string DeviceName { get; }
    bool IsConnected { get; }
    /// <summary>Current absolute step position. Backends without
    /// absolute positioning report a synthetic position starting at
    /// some midpoint and updated by relative moves.</summary>
    int Position { get; }
    /// <summary>Maximum addressable step value. Used by the UI to
    /// clamp slider ranges and by auto-focus to refuse a sweep that
    /// would overshoot.</summary>
    int MaxPosition { get; }
    /// <summary>Onboard temperature probe reading in degrees Celsius,
    /// NaN when the focuser doesn't have a sensor.</summary>
    double Temperature { get; }
    bool IsMoving { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task MoveAbsoluteAsync(int position, CancellationToken ct = default);
    Task MoveRelativeAsync(int steps, CancellationToken ct = default);
    Task AbortAsync(CancellationToken ct = default);
}
