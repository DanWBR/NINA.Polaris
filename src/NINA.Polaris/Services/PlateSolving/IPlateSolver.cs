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

    /// <summary>Resolved executable / endpoint path, when applicable (shown in
    /// the Settings plate-solve card). Null for solvers without a local binary
    /// (e.g. the online astrometry.net API).</summary>
    string? SolverPath => null;

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