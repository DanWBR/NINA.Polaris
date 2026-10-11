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
/// 2026-09-22 and 2026-09-23: a coding session POSTed /api/system/shutdown to
/// "stop the dev server" and powered off the developer's PC, twice, mid
/// session. The endpoint is the rig's "Shut down device" button and it did
/// exactly what it promises, so the guard is on the machine, not on the caller:
/// a development build refuses device power.
///
/// <para>The rig must keep working, and that is the other half of these tests:
/// the .deb runs the published build with the Production environment and no
/// project file near its content root, so nothing here fires on it.</para>
/// </summary>
[TestFixture]
public class HostPowerGuardTests {
    private static string? Refusal(string? env = null, bool source = false,
                                   string? no = null, string? allow = null)
        => PowerService.HostPowerRefusalFor(env, source, no, allow);

    [Test]
    public void ADeployedRigIsAllowedToPowerItselfOff() {
        Assert.That(Refusal("Production"), Is.Null);
        Assert.That(Refusal(null), Is.Null, "no environment set is the normal published case");
    }

    [Test]
    public void TheDevelopmentEnvironmentIsRefused() {
        var why = Refusal("Development");
        Assert.That(why, Is.Not.Null);
        Assert.That(why, Does.Contain("stop-app"), "the refusal has to say what to do instead");
    }

    [TestCase("development")]
    [TestCase("DEVELOPMENT")]
    public void TheEnvironmentCheckIgnoresCase(string env) {
        Assert.That(Refusal(env), Is.Not.Null);
    }

    [Test]
    public void ABuildRunFromItsSourceTreeIsRefused() {
        // The dev server points --contentRoot at src/NINA.Polaris, where the
        // project file lives. A published deployment never has one.
        Assert.That(Refusal(null, source: true), Is.Not.Null);
    }

    [TestCase("1")]
    [TestCase("true")]
    [TestCase("TRUE")]
    [TestCase("yes")]
    [TestCase("on")]
    public void TheOptOutRefusesEvenOnARealRig(string value) {
        Assert.That(Refusal("Production", no: value), Is.Not.Null);
    }

    [TestCase("0")]
    [TestCase("false")]
    [TestCase("")]
    [TestCase(null)]
    public void TheOptOutIsOnlyHonouredWhenItActuallySaysYes(string? value) {
        Assert.That(Refusal("Production", no: value), Is.Null);
    }

    [Test]
    public void TheOverrideWinsOverEveryDevelopmentSignal() {
        // For the operator who really does run a development build on a rig.
        Assert.That(Refusal("Development", source: true, allow: "1"), Is.Null);
    }

    [Test]
    public void TheOverrideEvenBeatsTheOptOut() {
        // Both set is a contradiction; the explicit "yes I mean it" wins, and
        // the order is pinned here so it cannot be flipped by accident.
        Assert.That(Refusal("Production", no: "1", allow: "1"), Is.Null);
    }

    [Test]
    public void ASourceCheckoutIsRecognisedByItsProjectFile() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-power-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            Assert.That(PowerService.LooksLikeSourceCheckout(dir), Is.False);
            File.WriteAllText(Path.Combine(dir, "NINA.Polaris.csproj"), "<Project />");
            Assert.That(PowerService.LooksLikeSourceCheckout(dir), Is.True);
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AMissingContentRootIsNotASourceCheckout(string? root) {
        Assert.That(PowerService.LooksLikeSourceCheckout(root), Is.False);
    }
}
