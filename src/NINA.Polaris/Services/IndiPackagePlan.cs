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

using System.Text.RegularExpressions;

namespace NINA.Polaris.Services;

/// <summary>What apt says it would do, and whether we are willing to let it.</summary>
public sealed record IndiPackagePlan(
    IReadOnlyList<string> Install,
    IReadOnlyList<string> Remove,
    bool Ok,
    string? Refusal);

/// <summary>
/// Reads `apt-get install -s` and decides whether an install is safe.
///
/// The rule is one line long and it is the whole point of this file: an
/// install that REMOVES anything is refused. INDI on these hosts comes either
/// from the mutlaqja PPA (libindi1) or from the distribution archive
/// (libindidriver1), the two cannot coexist, and apt resolves the conflict by
/// quietly tearing out the stack you are using.
///
/// This is not theoretical. Installing the archive's indi-asi on a PPA host
/// removed indi-bin, libindi1, libindi-dev AND phd2 in one command, in the
/// middle of the night, on a working rig. apt printed the plan first and
/// nobody read it. Now the machine reads it.
/// </summary>
public static class IndiPackagePlanner {
    /// <summary>Packages whose removal is never acceptable, named so the
    /// refusal can say what would have been lost.</summary>
    public static readonly string[] LoadBearing = {
        "indi-bin", "libindi1", "libindi-dev", "libindidriver1", "phd2",
        "indi-3rdparty-drivers", "indi-3rdparty-libs", "polaris"
    };

    public static IndiPackagePlan Parse(string? simulationOutput) {
        var install = new List<string>();
        var remove = new List<string>();
        if (string.IsNullOrWhiteSpace(simulationOutput))
            return new IndiPackagePlan(install, remove, false,
                "apt did not say what it would do, so nothing was installed.");

        foreach (var raw in simulationOutput.Split('\n')) {
            var line = raw.Trim();
            // "Inst indi-asi (2.2+2022 Ubuntu:26.04/resolute [arm64])"
            // "Remv phd2 [2.6.14]"
            var m = Regex.Match(line, @"^(Inst|Remv)\s+(\S+)");
            if (!m.Success) continue;
            if (m.Groups[1].Value == "Inst") install.Add(m.Groups[2].Value);
            else remove.Add(m.Groups[2].Value);
        }

        if (remove.Count > 0) {
            var lost = remove.Where(r => LoadBearing.Contains(r, StringComparer.Ordinal)).ToList();
            var named = lost.Count > 0 ? lost : remove;
            return new IndiPackagePlan(install, remove, false,
                "Refused: this would remove " + string.Join(", ", named)
                + ". That package comes from a different INDI build than the one installed here, "
                + "and apt would take the working one out to make room.");
        }

        if (install.Count == 0)
            return new IndiPackagePlan(install, remove, false,
                "apt had nothing to install: it may already be present.");

        return new IndiPackagePlan(install, remove, true, null);
    }

    /// <summary>Only ever hand apt a name that looks like a driver package.
    /// The name reaches a root process, so it is validated twice: here, and
    /// again in the script.</summary>
    public static bool IsValidPackageName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && Regex.IsMatch(name, @"^(indi|libindi)[a-z0-9.+-]*$");
}
