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

/// <summary>The INDI watchdog restarts a wedged driver through indi-web, which
/// only knows its own labels. A ToupTek AE676C that stopped delivering frames
/// got three "restarts" that were each a 404: the device's DRIVER_NAME
/// ("ToupTek") was sent as the label, and indi-web calls the driver "Toupcam".</summary>
[TestFixture]
public class IndiWatchdogDriverLabelTests {
    // The running profile on that Orange Pi 5 Pro, as GET /api/server/drivers served it.
    private static readonly IndiRunningDriver[] Rig = {
        new("Toupcam", "indi_toupcam_ccd", "Touptek"),
        new("Gemini EAF", "indi_myfocuserpro2_focus", "MyFocuserPro2"),
        new("ZWO AM5 USB", "indi_lx200am5", "ZWO AM5"),
    };

    [Test]
    public void TheExecutableFindsTheLabelTheDriverNameDoesNot() {
        Assert.That(IndiDriverWatchdogService.PickDriverLabel(
            "ToupTek AE676C", "ToupTek", "indi_toupcam_ccd", Rig), Is.EqualTo("Toupcam"));
    }

    [Test]
    public void WithoutTheExecutableIndiWebsNameStillMatches() {
        // indi-web's name "Touptek" against DRIVER_NAME "ToupTek", any case.
        Assert.That(IndiDriverWatchdogService.PickDriverLabel(
            "ToupTek AE676C", "ToupTek", null, Rig), Is.EqualTo("Toupcam"));
    }

    [Test]
    public void ARenamedDriverIsFoundByItsExecutable() {
        // The profile labels the focuser by the user's own name for it.
        Assert.That(IndiDriverWatchdogService.PickDriverLabel(
            "Gemini EAF", "MyFocuserPro2", "indi_myfocuserpro2_focus", Rig), Is.EqualTo("Gemini EAF"));
    }

    [Test]
    public void TheDeviceNamePrefixStillWorks() {
        var running = new[] { new IndiRunningDriver("ZWO CCD", null, null), new IndiRunningDriver("Toupcam", null, null) };
        Assert.That(IndiDriverWatchdogService.PickDriverLabel(
            "ZWO CCD ASI2600MC Pro", null, null, running), Is.EqualTo("ZWO CCD"));
    }

    [Test]
    public void ALabelIndiWebDoesNotHaveIsNeverReturned() {
        // Nothing ties the device to a running driver: no answer, rather than
        // a made-up label that comes back 404.
        Assert.That(IndiDriverWatchdogService.PickDriverLabel(
            "QHY CCD QHY268M", "QHY CCD", "indi_qhy_ccd", Rig), Is.Null);
    }

    [Test]
    public void ASingleRunningDriverIsTheOne() {
        var running = new[] { new IndiRunningDriver("Toupcam", "indi_toupcam_ccd", "Touptek") };
        Assert.That(IndiDriverWatchdogService.PickDriverLabel(
            "Some Camera", null, null, running), Is.EqualTo("Toupcam"));
    }
}
