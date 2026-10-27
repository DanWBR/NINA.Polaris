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

using System.Diagnostics;

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// Asterion (https://github.com/asterion-solver/asterion), a blind solver that
/// finishes in tens of milliseconds, driven through its ASTAP compatibility
/// mode: <c>asterion astap</c> takes ASTAP's command line and writes ASTAP's
/// <c>.ini</c> / <c>.wcs</c> with ASTAP's exit codes, so everything the ASTAP
/// solver does around the process (hints, the blind and downsample retries,
/// the single-channel proxy for colour masters, WCS read-back, cancellation)
/// applies unchanged. ASTAP-only options such as <c>-d</c> are accepted and
/// ignored; the catalogs come from <see cref="AsterionInstallService"/>.
/// </summary>
public sealed class AsterionSolver : AstapSolver {
    private readonly AsterionInstallService _install;

    public AsterionSolver(IConfiguration config, ILogger<AstapSolver> logger,
                          AsterionInstallService install, ProfileService? profiles = null)
        : base(config, logger, profiles) {
        _install = install;
    }

    public override string Id => "asterion";
    public override string DisplayName => "Asterion";
    public override string SolverPath => _install.ExecutablePath;

    /// <summary>Ready once the executable and at least one catalog are in place:
    /// without a catalog every solve ends in exit 32.</summary>
    public override bool IsAvailable => _install.SolverInstalled && _install.InstalledCatalogs().Count > 0;

    protected override string CommandPrefix => "astap ";

    protected override void ConfigureProcess(ProcessStartInfo psi)
        => psi.Environment["ASTERION_CATALOGS"] = _install.CatalogDir;
}
