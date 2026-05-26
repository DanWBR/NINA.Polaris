namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// Common interface for every plate-solving backend the app supports.
/// Implementations are stateless wrappers around an external executable
/// (ASTAP, PlateSolve3, solve-field) or an HTTP API (nova.astrometry.net).
/// </summary>
public interface IPlateSolver {
    /// <summary>Short identifier used in profile config (e.g. "astap").</summary>
    string Id { get; }

    /// <summary>Human-readable display name (e.g. "ASTAP").</summary>
    string DisplayName { get; }

    /// <summary>True when the backend is installed/configured and ready to run.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// True if this backend can solve "blind" (without RA/Dec hints + FOV).
    /// Used by the dispatcher to pick a sensible fallback.
    /// </summary>
    bool SupportsBlindSolve { get; }

    /// <summary>
    /// Solve the given FITS file. Returns a successful or failed result,
    /// implementations should not throw for solver-level failures, only for
    /// programming errors. <paramref name="ct"/> may abort the underlying
    /// process and should always be respected.
    /// </summary>
    Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options, CancellationToken ct = default);
}
