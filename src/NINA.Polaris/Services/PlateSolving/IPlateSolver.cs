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
    ///
    /// <paramref name="onLog"/> (optional) receives the solver's console
    /// output line-by-line as it runs, so the UI can stream live progress.
    /// </summary>
    Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options,
        CancellationToken ct = default, Action<string>? onLog = null);
}

/// <summary>Shared helpers for the external-process solvers.</summary>
public static class PlateSolveProcessOutput {
    /// <summary>Format a solver run (command + stdout + stderr + exit code)
    /// into a single human-readable block for the UI's "process output" panel.
    /// Tail-trimmed so a chatty solver doesn't bloat the JSON payload.</summary>
    public static string Combine(string exe, string args, string? stdout, string? stderr, int? exitCode) {
        var sb = new System.Text.StringBuilder();
        sb.Append("$ ").Append(exe).Append(' ').Append(args).Append('\n');
        if (!string.IsNullOrWhiteSpace(stdout)) sb.Append(Tail(stdout!, 8000)).Append('\n');
        if (!string.IsNullOrWhiteSpace(stderr)) sb.Append("[stderr] ").Append(Tail(stderr!, 4000)).Append('\n');
        if (exitCode.HasValue) sb.Append("[exit ").Append(exitCode.Value).Append(']');
        return sb.ToString().TrimEnd();
    }

    private static string Tail(string s, int maxChars) {
        s = s.Replace("\r", "");
        return s.Length <= maxChars ? s : "…" + s[^maxChars..];
    }
}