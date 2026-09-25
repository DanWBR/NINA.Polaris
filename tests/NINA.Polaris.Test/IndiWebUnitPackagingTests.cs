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

using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The packaging contract behind "indi-web outlives Polaris".
///
/// <para>That sentence was a comment in IndiWebManagerService for a long time
/// and it was not true. indi-web was forked with Process.Start, which put it in
/// polaris.service's cgroup, and systemd's default KillMode=control-group kills
/// the whole cgroup on stop, a restart being a stop plus a start. Measured on an
/// OPi5Pro on 2026-09-24: before `systemctl restart polaris`,
/// indi_myfocuserpro2_focus was pid 1950 and indi_lx200am5 pid 1953; afterwards
/// every driver pid was new, indiserver included. The mount had stopped tracking
/// and the camera had lost its setpoint over an update to the layer above.</para>
///
/// <para>The fix is a unit of its own, because a separate unit is the only thing
/// that is a separate cgroup: setsid keeps the cgroup it was forked into. The
/// pieces checked here are each load-bearing, and each fails in a way that looks
/// like something else:</para>
/// <list type="bullet">
///   <item>no PartOf=/BindsTo= back to polaris.service, which would hand the
///         coupling straight back</item>
///   <item>the launcher is 0755 in the built .deb, which build-deb.sh's blanket
///         `chmod 0644` over /opt/polaris would otherwise undo (status=203)</item>
///   <item>a PolicyKit grant naming the unit, without which systemctl answers
///         "Interactive authentication required" and the service falls back to
///         the forked child, i.e. to the bug</item>
///   <item>postinst does not enable the unit, so IndiWeb:AutoStart keeps
///         deciding whether indi-web comes up at boot</item>
/// </list>
/// </summary>
[TestFixture]
public class IndiWebUnitPackagingTests {

    private static string Repo([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private const string Unit = "polaris-indiweb.service";

    private static string Read(params string[] parts) {
        var p = Path.Combine(new[] { Repo() }.Concat(parts).ToArray());
        Assert.That(File.Exists(p), Is.True, $"{p} is missing");
        return File.ReadAllText(p);
    }

    private static string UnitFile() =>
        Read("packaging", "deb", "lib", "systemd", "system", Unit);

    [Test]
    public void Unit_runs_the_launcher_as_the_polaris_user() {
        var u = UnitFile();
        Assert.That(u, Does.Contain("ExecStart=/opt/polaris/bin/polaris-indiweb.sh"));
        Assert.That(u, Does.Contain("User=polaris"));
        // Bottle reads $HOME at import time and falls back to / without it.
        Assert.That(u, Does.Contain("Environment=HOME=/home/polaris"));
    }

    [Test]
    public void Unit_is_not_tied_to_the_lifetime_of_polaris_service() {
        var u = UnitFile();
        var couplings = new[] { "PartOf=", "BindsTo=", "Requisite=", "Requires=polaris.service" };
        foreach (var line in u.Split('\n').Select(l => l.Trim())) {
            if (line.StartsWith('#')) continue;
            foreach (var coupling in couplings) {
                Assert.That(line.StartsWith(coupling, StringComparison.Ordinal), Is.False,
                    $"{Unit} has `{line}`. Any directive that ties it to polaris.service " +
                    "brings back the restart that killed indiserver and every driver.");
            }
        }
        // A unit nothing Wants= and postinst does not enable is one that only
        // starts when Polaris asks, which is what makes IndiWeb:AutoStart mean
        // something.
        Assert.That(u, Does.Not.Contain("WantedBy=polaris.service"));
    }

    [Test]
    public void Unit_binds_loopback_and_the_port_polaris_probes() {
        var u = UnitFile();
        var app = Read("packaging", "deb", "opt", "polaris", "appsettings.json");
        // indi-web has no authentication of its own; the LAN reaches it only
        // through Polaris's authenticated reverse proxy at /indi-web/.
        Assert.That(u, Does.Contain("Environment=INDIWEB_HOST=127.0.0.1"));
        Assert.That(app, Does.Contain("\"BindAddress\": \"127.0.0.1\""));
        // A port mismatch reads to the user as "indi-web never came up": the
        // unit listens on one port and Polaris probes the other.
        Assert.That(u, Does.Contain("Environment=INDIWEB_PORT=8624"));
        Assert.That(app, Does.Contain("\"Port\": 8624"));
    }

    [Test]
    public void Launcher_resolves_the_venv_and_a_hand_rolled_install() {
        var sh = Read("packaging", "deb", "opt", "polaris", "bin", "polaris-indiweb.sh");
        Assert.That(sh, Does.Contain("$INDIWEB_BIN"),
            "the /etc/default override is how a non-venv indi-web is pointed at");
        Assert.That(sh, Does.Contain("/opt/polaris-indiweb-venv/bin/indi-web"));
        Assert.That(sh, Does.Contain("command -v indi-web"));
        Assert.That(sh, Does.Contain("exec \"$BIN\""),
            "exec, so systemd tracks indi-web itself and not the wrapper shell");
    }

    [Test]
    public void Build_makes_the_launcher_executable_and_the_unit_not() {
        var build = Read("packaging", "build-deb.sh");
        // `find $BUILD_DIR/opt/polaris -type f -exec chmod 0644` runs over this
        // script earlier in the same file, so the 0755 has to be spelled out or
        // the unit fails with code=exited/status=203 and indi-web silently goes
        // back to being a child of polaris.service.
        Assert.That(build, Does.Contain("chmod 0755 \"$BUILD_DIR/opt/polaris/bin/polaris-indiweb.sh\""));
        Assert.That(build, Does.Contain($"chmod 0644 \"$BUILD_DIR/lib/systemd/system/{Unit}\""));
    }

    [Test]
    public void Polkit_lets_the_polaris_user_drive_the_unit() {
        var rules = Read("packaging", "deb", "etc", "polkit-1", "rules.d", "50-polaris-indiweb.rules");
        Assert.That(rules, Does.Contain("org.freedesktop.systemd1.manage-units"));
        Assert.That(rules, Does.Contain($"action.lookup(\"unit\") == \"{Unit}\""));
        Assert.That(rules, Does.Contain("subject.user != \"polaris\""));
        // polkit < 0.106 (Ubuntu 22.04) ignores .rules entirely and reads the
        // .pkla twin, whose manage-units grant is unit-agnostic and so already
        // covers this unit. Its absence would strand those hosts.
        var pkla = Read("packaging", "deb", "etc", "polkit-1", "localauthority",
                        "50-local.d", "50-polaris-update.pkla");
        Assert.That(pkla, Does.Contain("org.freedesktop.systemd1.manage-units"));
    }

    [Test]
    public void Maintainer_scripts_leave_it_running_on_upgrade_and_stop_it_on_remove() {
        var postinst = Read("packaging", "deb", "DEBIAN", "postinst");
        Assert.That(postinst, Does.Contain("chmod 0755 /opt/polaris/bin/polaris-indiweb.sh"));
        // Enabling it would start indi-web at boot whatever IndiWeb:AutoStart
        // says, which turns that setting into a lie.
        Assert.That(postinst, Does.Not.Contain($"systemctl enable {Unit}"));

        // prerm stops polaris.service on remove AND on upgrade. indi-web may
        // only follow it on remove: surviving the upgrade is the whole point.
        var prerm = Read("packaging", "deb", "DEBIAN", "prerm");
        Assert.That(prerm, Does.Contain($"systemctl stop {Unit}"));
        Assert.That(prerm, Does.Contain("[ \"$1\" = \"remove\" ]"),
            "guard the stop, or an apt upgrade drops the drivers again");
    }
}
