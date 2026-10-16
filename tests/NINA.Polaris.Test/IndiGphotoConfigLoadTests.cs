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

using System.Collections.Concurrent;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>The automatic CONFIG_LOAD on connect replays a driver's saved
/// settings. A DSLR keeps its own in the camera, and replaying gphoto's saved
/// config set a Canon R8 back to JPEG on every connect.</summary>
[TestFixture]
public class IndiGphotoConfigLoadTests {
    [TestCase("indi_gphoto_ccd", false, true)]
    [TestCase("indi_canon_ccd", false, true)]
    [TestCase("indi_nikon_ccd", false, true)]
    [TestCase("indi_sony_ccd", false, true)]
    [TestCase(null, true, true)]                // gphoto's CCD_CAPTURE_TARGET alone
    [TestCase("indi_asi_ccd", false, false)]
    [TestCase("indi_toupcam_ccd", false, false)]
    [TestCase("indi_lx200am5", false, false)]
    [TestCase(null, false, false)]
    public void WhichDriversAreDslrs(string? exec, bool hasCaptureTarget, bool dslr) {
        Assert.That(IndiClient.IsGphotoDslr(exec, hasCaptureTarget), Is.EqualTo(dslr));
    }

    private static void Define(IndiClient client, string device, IndiProperty prop) {
        client.Devices.GetOrAdd(device, _ => new ConcurrentDictionary<string, IndiProperty>())[prop.Name] = prop;
    }

    [Test]
    public void ADeviceIsRecognisedFromItsDriverInfo() {
        using var client = new IndiClient();
        Define(client, "GPhoto CCD", new IndiTextProperty {
            Device = "GPhoto CCD", Name = "DRIVER_INFO",
            Values = new() { ["DRIVER_EXEC"] = "indi_gphoto_ccd", ["DRIVER_NAME"] = "GPhoto CCD" }
        });
        Define(client, "ZWO CCD ASI2600MM Pro", new IndiTextProperty {
            Device = "ZWO CCD ASI2600MM Pro", Name = "DRIVER_INFO",
            Values = new() { ["DRIVER_EXEC"] = "indi_asi_ccd" }
        });
        Assert.Multiple(() => {
            Assert.That(client.IsGphotoDslr("GPhoto CCD"), Is.True);
            Assert.That(client.IsGphotoDslr("ZWO CCD ASI2600MM Pro"), Is.False);
            Assert.That(client.IsGphotoDslr("Unknown"), Is.False);
        });
    }

    [Test]
    public void ADeviceIsRecognisedFromCaptureTarget() {
        using var client = new IndiClient();
        Define(client, "Canon DSLR EOS R8", new IndiSwitchProperty {
            Device = "Canon DSLR EOS R8", Name = "CCD_CAPTURE_TARGET",
            Values = new() { ["RAM"] = true, ["SD Card"] = false }
        });
        Assert.That(client.IsGphotoDslr("Canon DSLR EOS R8"), Is.True);
    }
}
