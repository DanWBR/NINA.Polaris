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

using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Services;

/// <summary>
/// Dispatcher that picks a plate-solving backend based on configuration and
/// falls back to a blind-capable solver if the primary fails.
///
/// Selection priority:
///   1. Primary solver: <c>PlateSolve:PrimarySolver</c> (default "astap")
///   2. If primary fails AND <c>PlateSolve:UseBlindFallback</c> is true,
///      try <c>PlateSolve:BlindSolver</c> (default "astrometry-net-online")
///
/// All implementations live under <see cref="PlateSolving"/>; this class is
/// just routing + result aggregation, so the rest of the app can keep
/// calling <c>SolveAsync</c> without knowing which backend is in use.
/// </summary>
public class PlateSolveService {
    private readonly IConfiguration _config;
    private readonly ILogger<PlateSolveService> _logger;
    private readonly ProfileService? _profiles;
    private readonly IReadOnlyDictionary<string, IPlateSolver> _solvers;

    public PlateSolveService(IConfiguration config, ILogger<PlateSolveService> logger,
        AstapSolver astap, PlateSolve3Solver ps3,
        AstrometryNetOnlineSolver netOnline, AstrometryNetLocalSolver netLocal,
        ProfileService? profiles = null) {
        _config = config;
        _logger = logger;
        _profiles = profiles;
        _solvers = new Dictionary<string, IPlateSolver>(StringComparer.OrdinalIgnoreCase) {
            [astap.Id] = astap,
            [ps3.Id] = ps3,
            [netOnline.Id] = netOnline,
            [netLocal.Id] = netLocal
        };
    }

    /// <summary>Backwards-compat constructor for tests that only need ASTAP.</summary>
    public PlateSolveService(IConfiguration config, ILogger<PlateSolveService> logger)
        : this(config, logger,
              new AstapSolver(config, new Microsoft.Extensions.Logging.Abstractions.NullLogger<AstapSolver>()),
              new PlateSolve3Solver(config, new Microsoft.Extensions.Logging.Abstractions.NullLogger<PlateSolve3Solver>()),
              new AstrometryNetOnlineSolver(config, new Microsoft.Extensions.Logging.Abstractions.NullLogger<AstrometryNetOnlineSolver>()),
              new AstrometryNetLocalSolver(config, new Microsoft.Extensions.Logging.Abstractions.NullLogger<AstrometryNetLocalSolver>())) { }

    public IEnumerable<IPlateSolver> AllSolvers => _solvers.Values;

    /// <summary>The most recent successful solve (coords + when), shared so the
    /// capture pipeline can name the saved-frame folder from the actual sky
    /// position instead of "Unknown". Null until the first successful solve.</summary>
    public LastSolveInfo? LastSuccessfulSolve { get; private set; }

    private void RecordSolve(PlateSolveResult r) {
        if (r.Success) {
            LastSuccessfulSolve = new LastSolveInfo(r.RaHours, r.DecDeg, DateTime.UtcNow);
        }
    }

    public IPlateSolver PrimarySolver {
        get {
            // UI (profile) choice wins, then appsettings config.
            var id = !string.IsNullOrWhiteSpace(_profiles?.Active.PlateSolvePrimary)
                ? _profiles!.Active.PlateSolvePrimary
                : _config.GetValue("PlateSolve:PrimarySolver", "astap")!;
            return _solvers.TryGetValue(id, out var s) ? s : _solvers["astap"];
        }
    }

    public IPlateSolver? BlindSolver {
        get {
            var useBlind = _profiles != null
                ? _profiles.Active.PlateSolveUseBlindFallback
                : _config.GetValue("PlateSolve:UseBlindFallback", true);
            if (!useBlind) return null;
            var id = _config.GetValue("PlateSolve:BlindSolver", "astrometry-net-online")!;
            return _solvers.TryGetValue(id, out var s) && s.SupportsBlindSolve ? s : null;
        }
    }

    /// <summary>True if at least one configured backend is ready.</summary>
    public bool IsAvailable => PrimarySolver.IsAvailable || (BlindSolver?.IsAvailable ?? false);

    /// <summary>Path of the primary solver (back-compat for existing tests).</summary>
    public string SolverPath => PrimarySolver is AstapSolver a ? a.SolverPath : "";

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options,
            CancellationToken ct = default, Action<string>? onLog = null) {
        var primary = PrimarySolver;
        string? primaryError = null;
        if (primary.IsAvailable) {
            try { onLog?.Invoke($"== {primary.DisplayName} =="); } catch { }
            var result = await primary.SolveAsync(fitsPath, options, ct, onLog);
            if (result.Success) { RecordSolve(result); return result; }
            primaryError = result.Error;
            _logger.LogWarning("Primary solver {Name} failed: {Err}", primary.DisplayName, result.Error);
        } else {
            primaryError = $"{primary.DisplayName} not available at {(primary as AstapSolver)?.SolverPath ?? "(unknown path)"}";
            _logger.LogInformation("Primary solver {Name} not available", primary.DisplayName);
        }

        var blind = BlindSolver;
        if (blind != null && blind.IsAvailable && blind.Id != primary.Id) {
            _logger.LogInformation("Falling back to blind solver {Name}", blind.DisplayName);
            try { onLog?.Invoke($"== blind fallback: {blind.DisplayName} =="); } catch { }
            var blindResult = await blind.SolveAsync(fitsPath, options, ct, onLog);
            if (blindResult.Success) { RecordSolve(blindResult); return blindResult; }
            return PlateSolveResult.Failed(
                $"Primary ({primary.DisplayName}) failed: {primaryError}. " +
                $"Blind fallback ({blind.DisplayName}) failed: {blindResult.Error}");
        }

        return PlateSolveResult.Failed(
            primary.IsAvailable
                ? $"{primary.DisplayName} failed (no blind fallback configured): {primaryError}"
                : $"Primary solver {primary.DisplayName} is not available: {primaryError}");
    }
}

/// <summary>Last successful plate-solve result, used to name capture folders
/// from the real sky position. RA in hours, Dec in degrees, captured UTC.</summary>
public sealed record LastSolveInfo(double RaHours, double DecDeg, DateTime WhenUtc);

public class PlateSolveOptions {
    public double? HintRa { get; set; }
    public double? HintDec { get; set; }
    public double SearchRadiusDeg { get; set; } = 30;
    public double FovDeg { get; set; }
    public int Downsample { get; set; } = 2;
    /// <summary>True when <see cref="Downsample"/> is a deliberate decision for
    /// this one call and must beat the profile/config default. Only the retry
    /// ladder sets it: the escalation exists precisely to use a factor the
    /// operator's setting does not, and without this the profile value silently
    /// won and the "coarser" retry re-ran the command that had just failed.</summary>
    public bool DownsampleIsExplicit { get; set; }
    /// <summary>Approximate pixel scale in arcsec/pixel, required by PlateSolve3, optional hint for others.</summary>
    public double ScaleArcsecPerPixel { get; set; }
}

public class PlateSolveResult {
    public bool Success { get; set; }
    public string? Error { get; set; }
    public double RaHours { get; set; }
    public double RaDeg { get; set; }
    public double DecDeg { get; set; }
    public double ScaleArcsecPerPixel { get; set; }
    public double RotationDeg { get; set; }
    /// <summary>Id of the solver that produced this result (or attempted to).</summary>
    public string? SolverUsed { get; set; }

    /// <summary>Full WCS CD matrix (deg/pixel) when the solver exposes it
    /// (ASTAP does, via its .wcs output). Unlike the scalar
    /// <see cref="RotationDeg"/>, the CD matrix encodes rotation AND parity
    /// (mirror/flip), so annotation projection that uses it lands on the
    /// right objects for mirrored optical trains. Null for solvers that
    /// only report scale + rotation.</summary>
    public double? CD11 { get; set; }
    public double? CD12 { get; set; }
    public double? CD21 { get; set; }
    public double? CD22 { get; set; }
    /// <summary>Reference pixel (1-based, FITS convention) for the CD matrix.
    /// Defaults to the image centre when the solver omits it.</summary>
    public double CrPix1 { get; set; }
    public double CrPix2 { get; set; }

    /// <summary>True when the CD matrix is populated and usable.</summary>
    public bool HasCdMatrix => CD11.HasValue && CD12.HasValue
        && CD21.HasValue && CD22.HasValue
        && (CD11.Value * CD22.Value - CD12.Value * CD21.Value) != 0;

    /// <summary>Raw solver process output (stdout + stderr), surfaced to the UI
    /// so the operator can see what the backend did. Null when not captured.</summary>
    public string? Output { get; set; }

    public static PlateSolveResult Failed(string error) =>
        new() { Success = false, Error = error };
}