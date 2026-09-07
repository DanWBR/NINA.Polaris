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

using System;
using System.Collections.Generic;
using NINA.Polaris.Services.PlateSolving;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// A board can carry more than one ASTAP CLI, and they are not equivalent.
///
/// Orange Pi 5 Pro, 2026-09-06: /usr/bin/astap_cli was CLI-2025.09.29, from the
/// astap-cli package our own .deb recommends, and /opt/astap/astap_cli was
/// CLI-2026.05.18. Same frame, same arguments, same "Only 0 stars found in
/// image. Abort" verdict, 114.6 s against 0.93 s. Resolving by path order gave
/// every solve to the slow one; an Orange Pi 4 Pro without that package was a
/// hundred times faster on slower silicon.
/// </summary>
[TestFixture]
public class AstapVersionChoiceTests {

    private static readonly DateOnly Old2025 = new(2025, 9, 29);
    private static readonly DateOnly New2026 = new(2026, 5, 18);

    private static Func<string, DateOnly?> Versions(Dictionary<string, DateOnly?> map)
        => p => map.TryGetValue(p, out var v) ? v : null;

    [Test]
    public void TheNewerCliWinsEvenWhenItIsLaterInThePathOrder() {
        var picked = AstapSolver.PickAstap(
            AstapSolver.LinuxCandidates(),
            p => p is "/usr/bin/astap_cli" or "/opt/astap/astap_cli" or "/usr/local/bin/astap",
            Versions(new() {
                ["/usr/bin/astap_cli"] = Old2025,
                ["/opt/astap/astap_cli"] = New2026,
            }));

        Assert.That(picked, Is.EqualTo("/opt/astap/astap_cli"),
            "o binario mais novo tem de vencer, e nao o primeiro caminho da lista");
    }

    [Test]
    public void PathOrderStillDecidesWhenTheVersionsMatch() {
        var picked = AstapSolver.PickAstap(
            AstapSolver.LinuxCandidates(),
            p => p is "/usr/bin/astap_cli" or "/opt/astap/astap_cli",
            Versions(new() {
                ["/usr/bin/astap_cli"] = New2026,
                ["/opt/astap/astap_cli"] = New2026,
            }));

        Assert.That(picked, Is.EqualTo("/usr/bin/astap_cli"));
    }

    [Test]
    public void ABinaryThatDoesNotAnswerNeverBeatsOneThatDoes() {
        var picked = AstapSolver.PickAstap(
            AstapSolver.LinuxCandidates(),
            p => p is "/usr/bin/astap_cli" or "/opt/astap/astap_cli",
            Versions(new() {
                ["/usr/bin/astap_cli"] = null,
                ["/opt/astap/astap_cli"] = Old2025,
            }));

        Assert.That(picked, Is.EqualTo("/opt/astap/astap_cli"));
    }

    [Test]
    public void WithNoVersionsAtAllTheFirstCandidateIsKept() {
        var picked = AstapSolver.PickAstap(
            AstapSolver.LinuxCandidates(),
            p => p is "/usr/bin/astap_cli" or "/opt/astap/astap_cli",
            _ => null);

        Assert.That(picked, Is.EqualTo("/usr/bin/astap_cli"));
    }

    /// <summary>The graphical binary must never be probed: asking it for a
    /// version means launching it, which opens a window on a desktop. It is
    /// also the last resort, so it is only reached when no CLI exists.</summary>
    [Test]
    public void TheGuiBinaryIsNeverRunToReadAVersion() {
        var probed = new List<string>();
        var picked = AstapSolver.PickAstap(
            AstapSolver.LinuxCandidates(),
            p => p is "/usr/local/bin/astap" or "/opt/astap/astap",
            p => { probed.Add(p); return New2026; });

        Assert.That(picked, Is.EqualTo("/usr/local/bin/astap"));
        Assert.That(probed, Is.Empty, "o binario grafico nao pode ser executado so para ler a versao");
    }

    [Test]
    public void AHeadlessBinaryStillBeatsAGuiOneWhateverTheVersions() {
        var picked = AstapSolver.PickAstap(
            AstapSolver.LinuxCandidates(),
            p => p is "/opt/astap/astap_cli" or "/usr/local/bin/astap",
            Versions(new() { ["/opt/astap/astap_cli"] = Old2025 }));

        Assert.That(picked, Is.EqualTo("/opt/astap/astap_cli"));
    }

    [Test]
    public void NothingInstalledResolvesToNull() {
        Assert.That(AstapSolver.PickAstap(AstapSolver.LinuxCandidates(), _ => false, _ => null),
            Is.Null);
    }

    [TestCase("ASTAP astrometric solver version CLI-2026.05.18", 2026, 5, 18)]
    [TestCase("ASTAP astrometric solver version CLI-2025.09.29", 2025, 9, 29)]
    [TestCase("ASTAP CLI solver version 2026.06.29", 2026, 6, 29)]
    public void TheBannerDateIsParsed(string banner, int y, int m, int d) {
        Assert.That(AstapSolver.ParseAstapVersion(banner), Is.EqualTo(new DateOnly(y, m, d)));
    }

    [TestCase("")]
    [TestCase("command not found")]
    [TestCase("version CLI-2026.13.40")]
    public void ABannerWithoutAUsableDateGivesNull(string banner) {
        Assert.That(AstapSolver.ParseAstapVersion(banner), Is.Null);
    }
}
