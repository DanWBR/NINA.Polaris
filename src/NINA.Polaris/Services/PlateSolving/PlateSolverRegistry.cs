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
/// Solvers contributed by plugins. The plugin loader fills it at startup and
/// <see cref="PlateSolveService"/> reads it on every lookup, so it does not
/// matter which of the two the container builds first.
/// </summary>
public class PlateSolverRegistry {
    private readonly object _gate = new();
    private readonly Dictionary<string, (IPlateSolver Solver, string Source)> _solvers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ids the host's own solvers use; a plugin cannot take one.</summary>
    public static readonly string[] ReservedIds = {
        "astap", "platesolve3", "astrometry-net-online", "astrometry-net-local",
        "asterion", CustomCommandSolver.SolverId
    };

    /// <summary>Add a solver. Returns the reason it was refused, or null.</summary>
    public string? Add(IPlateSolver solver, string source) {
        ArgumentNullException.ThrowIfNull(solver);
        var id = solver.Id?.Trim() ?? "";
        if (id.Length == 0) return "the solver has no Id";
        if (ReservedIds.Contains(id, StringComparer.OrdinalIgnoreCase)) return $"'{id}' is a built-in solver id";
        lock (_gate) {
            if (_solvers.TryGetValue(id, out var existing))
                return $"'{id}' is already registered by {existing.Source}";
            _solvers[id] = (solver, source);
        }
        return null;
    }

    public IReadOnlyList<IPlateSolver> Solvers {
        get { lock (_gate) return _solvers.Values.Select(v => v.Solver).ToList(); }
    }

    public IPlateSolver? Find(string id) {
        lock (_gate) return _solvers.TryGetValue(id, out var v) ? v.Solver : null;
    }
}
