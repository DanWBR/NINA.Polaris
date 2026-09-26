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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The guard on installing an INDI driver from the UI: apt's own plan is read
/// first, and anything that removes a package is refused.
///
/// The first case below is not invented. On 2026-09-25, on a working rig at
/// night, `apt install indi-asi` on a host running the PPA's INDI removed
/// indi-bin, libindi1, libindi-dev and phd2: the archive's driver links
/// against a different INDI and apt made room for it. The mount lost its
/// driver. apt printed the plan and nobody read it, so now this does.
/// </summary>
[TestFixture]
public class IndiPackagePlannerTests {

    private const string TheNightItBrokeTheRig = """
        Reading package lists...
        Building dependency tree...
        The following packages will be REMOVED:
          indi-bin libindi-dev libindi1 phd2
        The following NEW packages will be installed:
          indi-asi libindidriver1
        Remv phd2 [2.6.14.rev20251216-0ppa1~ubuntu26.04]
        Remv indi-bin [2.2.2+202606010554~ubuntu26.04.1]
        Remv libindi-dev [2.2.2+202606010554~ubuntu26.04.1]
        Remv libindi1 [2.2.2+202606010554~ubuntu26.04.1]
        Inst libindidriver1 (1.9.9+dfsg-6 Ubuntu:26.04/resolute [arm64])
        Inst indi-asi (2.2+20221225102500-2 Ubuntu:26.04/resolute [arm64])
        Conf libindidriver1 (1.9.9+dfsg-6 Ubuntu:26.04/resolute [arm64])
        Conf indi-asi (2.2+20221225102500-2 Ubuntu:26.04/resolute [arm64])
        """;

    [Test]
    public void ThePlanThatRemovedTheMountDriverIsRefused() {
        var plan = IndiPackagePlanner.Parse(TheNightItBrokeTheRig);
        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Remove, Does.Contain("phd2"));
        Assert.That(plan.Remove, Does.Contain("indi-bin"));
        Assert.That(plan.Refusal, Does.Contain("would remove"));
        // The refusal must name what would be lost: "it failed" teaches nobody
        // anything, "it would remove phd2" teaches the whole problem.
        Assert.That(plan.Refusal, Does.Contain("phd2"));
        Assert.That(plan.Refusal, Does.Contain("indi-bin"));
    }

    [Test]
    public void AnInstallThatOnlyAddsIsAllowed() {
        const string clean = """
            Reading package lists...
            Inst indi-3rdparty-libs (2.2.0+202608250615~ubuntu26.04.1 INDI Stable Builds:26.04/resolute [arm64])
            Inst indi-3rdparty-drivers (2.2.0+202608250819~ubuntu26.04.1 INDI Stable Builds:26.04/resolute [arm64])
            Conf indi-3rdparty-libs (2.2.0+202608250615~ubuntu26.04.1 INDI Stable Builds:26.04/resolute [arm64])
            """;
        var plan = IndiPackagePlanner.Parse(clean);
        Assert.That(plan.Ok, Is.True);
        Assert.That(plan.Refusal, Is.Null);
        Assert.That(plan.Install, Is.EqualTo(new[] { "indi-3rdparty-libs", "indi-3rdparty-drivers" }));
        Assert.That(plan.Remove, Is.Empty);
    }

    [Test]
    public void RemovingSomethingUnimportantIsStillRefused() {
        // The rule is not a list of precious packages: an install that removes
        // ANYTHING is a swap, and a swap is not what the operator asked for.
        const string swap = """
            Inst indi-asi (2.2 Ubuntu:26.04/resolute [arm64])
            Remv some-other-package [1.0]
            """;
        var plan = IndiPackagePlanner.Parse(swap);
        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Refusal, Does.Contain("some-other-package"));
    }

    [Test]
    public void NothingToInstallIsNotSuccess() {
        var plan = IndiPackagePlanner.Parse("Reading package lists...\nBuilding dependency tree...\n");
        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Refusal, Does.Contain("nothing to install"));
    }

    [Test]
    public void NoOutputAtAllIsNotSuccess() {
        Assert.That(IndiPackagePlanner.Parse(null).Ok, Is.False);
        Assert.That(IndiPackagePlanner.Parse("").Ok, Is.False);
    }

    [TestCase("indi-asi", true)]
    [TestCase("indi-3rdparty-drivers", true)]
    [TestCase("libindi1", true)]
    [TestCase("phd2", false)]
    [TestCase("indi-asi; rm -rf /", false)]
    [TestCase("indi asi", false)]
    [TestCase("../../etc/passwd", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void OnlyDriverPackageNamesReachApt(string? name, bool valid) {
        // The name ends up as a systemd instance and then as an apt argument
        // in a root process, so it is checked here and again in the script.
        Assert.That(IndiPackagePlanner.IsValidPackageName(name), Is.EqualTo(valid));
    }
}
