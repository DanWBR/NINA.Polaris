namespace NINA.Polaris.Services;

/// <summary>
/// Per-rig persisted defaults for the Flat Wizard (FW-1). These mirror
/// the fields on <see cref="FlatWizardRequest"/>, so the UI can hydrate
/// from the active rig on tab-enter and (debounced) PUT them back to
/// <see cref="EquipmentProfile.FlatWizard"/> as the user tweaks values.
///
/// Hosted on <see cref="EquipmentProfile.FlatWizard"/>; the request that
/// kicks <see cref="FlatWizardService.Start"/> still carries every field
/// explicitly (the JS client sends the hydrated form), so this record is
/// just the persistence layer, not a runtime input.
///
/// Per-rig is the right scope: a wide-aperture refractor at f/5 + an
/// SCT at f/10 + a guidescope-as-imager all converge to different
/// exposures + benefit from independent target ADU / frame counts.
/// Cloning a rig copies these too (clone path in ProfileService).
/// </summary>
public class FlatWizardSettings {
    /// <summary>Target median ADU for the binary search. 30000 is a
    /// classic "well-lit but not clipped" for 16-bit; lower for 14-bit
    /// sensors that clip earlier. Stored as int because that's what
    /// the request shape uses.</summary>
    public int TargetAdu { get; set; } = 30000;

    /// <summary>±tolerance band as a fraction. 0.05 = ±5%. The search
    /// converges when median ∈ [TargetAdu * (1−tol), TargetAdu * (1+tol)].</summary>
    public double Tolerance { get; set; } = 0.05;

    /// <summary>Number of flat frames to capture per filter once the
    /// search converges. 20 is enough for a clean median master at
    /// most pixel scales; bump for very low-light sky flats.</summary>
    public int FramesPerFilter { get; set; } = 20;

    /// <summary>Lower bound for the binary search (seconds). Should be
    /// the shortest exposure the camera can produce reliably (sub-100ms
    /// often has dead-time bias).</summary>
    public double MinExposureSec { get; set; } = 0.1;

    /// <summary>Upper bound for the binary search (seconds). Tune up for
    /// dim sky flats, down for bright panels so the search doesn't waste
    /// iterations exploring nonsense values.</summary>
    public double MaxExposureSec { get; set; } = 30.0;

    /// <summary>Camera binning to apply before each search/capture.
    /// Trained exposures are cached per (filter, binning) tuple, so
    /// switching binning here trains a separate slot.</summary>
    public int Binning { get; set; } = 1;

    /// <summary>Hard cap on binary-search iterations per filter. 10
    /// covers ~3 decades of dynamic range; bump only if the band is
    /// very tight (Tolerance &lt; 0.02) and convergence stalls.</summary>
    public int MaxSearchIterations { get; set; } = 10;

    /// <summary>Flat-panel brightness 0-100 to apply (via flat-panel
    /// driver) before the wizard starts. 0 means "don't touch the
    /// panel" — useful for sky / T-shirt flats where there's no panel
    /// connected. The wizard itself doesn't drive the panel; the
    /// frontend POSTs to <c>/api/flatdevice/brightness</c> before
    /// kicking <c>/api/flatwizard/start</c>.</summary>
    public int PanelBrightness { get; set; } = 0;
}
