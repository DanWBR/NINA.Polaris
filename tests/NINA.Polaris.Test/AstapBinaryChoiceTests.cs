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
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// ASTAP ships two binaries and only one of them works on a headless host.
///
/// The GTK <c>astap</c> prints "cannot open display" and exits with status 0,
/// having solved nothing: the exit code says success, no .ini appears, and the
/// operator is told a result file is missing. Our own installer drops
/// <c>astap</c> in /usr/local/bin and <c>astap_cli</c> in /opt/astap, and the
/// old candidate order interleaved locations with binary kinds, so the GTK one
/// won on every image without a desktop.
///
/// Diagnosed on an Orange Pi 4 Pro, 2026-08-08. It had hidden for a long time
/// because a board that happens to run a desktop solves fine.
/// </summary>
[TestFixture]
public class AstapBinaryChoiceTests {

    /// <summary>Checks BOTH platform lists whatever host this runs on.
    ///
    /// The first version of this test called AstapCandidates(), which returns
    /// only the current platform's list: on a Windows dev box it happily passed
    /// while the Linux order was still wrong. A test that cannot fail on the
    /// platform you are sitting at is not a test.</summary>
    [TestCase("linux")]
    [TestCase("windows")]
    public void EveryCliCandidateComesBeforeEveryGuiCandidate(string platform) {
        var candidates = (platform == "windows"
            ? AstapSolver.WindowsCandidates()
            : AstapSolver.LinuxCandidates()).ToList();
        static bool IsCli(string p) => Path.GetFileNameWithoutExtension(p) == "astap_cli";
        static bool IsGui(string p) => Path.GetFileNameWithoutExtension(p) == "astap";

        int lastCli = candidates.FindLastIndex(IsCli);
        int firstGui = candidates.FindIndex(IsGui);

        Assert.That(lastCli, Is.GreaterThanOrEqualTo(0), "nenhum candidato astap_cli na lista");
        Assert.That(firstGui, Is.GreaterThanOrEqualTo(0), "nenhum candidato astap na lista");
        Assert.That(lastCli, Is.LessThan(firstGui),
            "o headless astap_cli tem de ser procurado ANTES de qualquer astap grafico: "
            + "o instalador poe astap em /usr/local/bin e astap_cli em /opt/astap, e a ordem "
            + "antiga fazia o GUI vencer numa placa sem desktop");
    }

    /// <summary>The exact pair the installer produces has to resolve to the CLI.</summary>
    [Test]
    public void TheInstallerLayoutResolvesToTheHeadlessBinary() {
        var candidates = AstapSolver.LinuxCandidates().ToList();

        int cli = candidates.IndexOf("/opt/astap/astap_cli");
        int gui = candidates.IndexOf("/usr/local/bin/astap");

        Assert.That(cli, Is.GreaterThanOrEqualTo(0));
        Assert.That(gui, Is.GreaterThanOrEqualTo(0));
        Assert.That(cli, Is.LessThan(gui),
            "esta e exatamente a dupla que a nossa imagem instala, e era esta que escolhia errado");
    }

    /// <summary>"ASTAP .ini result file not found" points nowhere. When the
    /// output carries the GTK complaint, say what actually happened.</summary>
    [Test]
    public void AGtkFailureIsExplainedInsteadOfReportedAsAMissingFile() {
        var explained = AstapSolver.ExplainHeadless(
            "ASTAP .ini result file not found",
            "", "(astap:10667): Gtk-WARNING **: cannot open display: ",
            "/usr/local/bin/astap");

        Assert.That(explained, Does.Contain("display"));
        Assert.That(explained, Does.Contain("astap_cli"),
            "a mensagem tem de dizer o que instalar, nao so que faltou um arquivo");
    }

    /// <summary>An ordinary failure must keep its own message.</summary>
    [Test]
    public void AnUnrelatedFailureIsLeftAlone() {
        const string original = "ASTAP .ini result file not found";

        Assert.That(
            AstapSolver.ExplainHeadless(original, "no solution found", "", "/opt/astap/astap_cli"),
            Is.EqualTo(original));
    }
}
