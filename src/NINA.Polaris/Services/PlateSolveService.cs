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

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options, CancellationToken ct = default) {
        var primary = PrimarySolver;
        string? primaryError = null;
        if (primary.IsAvailable) {
            var result = await primary.SolveAsync(fitsPath, options, ct);
            if (result.Success) return result;
            primaryError = result.Error;
            _logger.LogWarning("Primary solver {Name} failed: {Err}", primary.DisplayName, result.Error);
        } else {
            primaryError = $"{primary.DisplayName} not available at {(primary as AstapSolver)?.SolverPath ?? "(unknown path)"}";
            _logger.LogInformation("Primary solver {Name} not available", primary.DisplayName);
        }

        var blind = BlindSolver;
        if (blind != null && blind.IsAvailable && blind.Id != primary.Id) {
            _logger.LogInformation("Falling back to blind solver {Name}", blind.DisplayName);
            var blindResult = await blind.SolveAsync(fitsPath, options, ct);
            if (blindResult.Success) return blindResult;
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

public class PlateSolveOptions {
    public double? HintRa { get; set; }
    public double? HintDec { get; set; }
    public double SearchRadiusDeg { get; set; } = 30;
    public double FovDeg { get; set; }
    public int Downsample { get; set; } = 2;
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

    /// <summary>Raw solver process output (stdout + stderr), surfaced to the UI
    /// so the operator can see what the backend did. Null when not captured.</summary>
    public string? Output { get; set; }

    public static PlateSolveResult Failed(string error) =>
        new() { Success = false, Error = error };
}