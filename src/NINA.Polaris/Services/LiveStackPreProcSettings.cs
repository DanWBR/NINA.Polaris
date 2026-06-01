namespace NINA.Polaris.Services;

/// <summary>
/// Per-rig settings that control per-frame pre-processing inside the
/// live stacker (LSPP). Lives on the EquipmentProfile so the choices
/// survive across sessions.
///
/// All flags default to OFF so existing rigs continue to behave
/// exactly as they did before LSPP shipped -- opting in requires an
/// explicit toggle from the LIVE tab.
/// </summary>
public class LiveStackPreProcSettings {
    /// <summary>When true, every incoming frame is calibrated
    /// (subtract dark / divide by flat) BEFORE star detection +
    /// accumulation. Auto-matches masters from FrameLibrary by
    /// (gain, exposure, filter) unless one of the override IDs
    /// below is set. Falls back to the raw frame on failure (no
    /// abort -- live stack keeps growing).</summary>
    public bool CalibrationEnabled { get; set; } = false;

    /// <summary>Optional pin -- master dark to use regardless of
    /// auto-match. Null = use whatever the auto-match picker finds
    /// based on the frame's gain + exposure metadata. Refers to a
    /// FrameLibrary row id; resolved to a path at frame time.</summary>
    public int? MasterDarkOverrideId { get; set; }

    /// <summary>Optional pin -- master flat to use regardless of
    /// auto-match. Null = auto-match by (gain, filter).</summary>
    public int? MasterFlatOverrideId { get; set; }

    /// <summary>Optional pin -- master bias to use when no dark is
    /// available. Null = auto-match by gain.</summary>
    public int? MasterBiasOverrideId { get; set; }

    /// <summary>When true, run GraXpert BGE on every frame inside
    /// the browser BEFORE adding it to the stack. Only takes effect
    /// in MetricsOnly (client-side stack) mode -- server-side stacks
    /// can't reach the WASM BGE pipeline and silently no-op when
    /// this is on. UI shows a banner explaining the constraint.</summary>
    public bool BgeEnabled { get; set; } = false;

    /// <summary>BGE smoothing parameter [0.0, 1.0]. Default 1.0
    /// matches the FILES tab + AutoGraXpert default.</summary>
    public double BgeSmoothing { get; set; } = 1.0;

    /// <summary>BGE correction mode: "Subtraction" or "Division".
    /// Subtraction is the standard astrophoto default; Division is
    /// rare (used when the gradient is multiplicative, e.g. dust
    /// shadows missed by the flat).</summary>
    public string BgeCorrection { get; set; } = "Subtraction";
}
