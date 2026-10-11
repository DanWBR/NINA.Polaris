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
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Reading the brand out of INDI's driver XML.
///
/// A field report said the Wanderer and PrimaLuceLab drivers were missing. They
/// were installed. INDI labels a device by its PRODUCT, with the brand only in
/// a manufacturer attribute, and indi-web serves the label, the binary and the
/// family. So "Sesto Senso 2" was in the list and "PrimaLuceLab" matched
/// nothing, which reads exactly like a driver that is not there.
///
/// The samples below are the real shapes, copied from a host running INDI 2.2.
/// </summary>
[TestFixture]
public class IndiDriverManifestTests {

    private static (Dictionary<string, string> byBinary, Dictionary<string, string> byLabel)
            Parse(params string[] documents) {
        var byBinary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in documents) IndiDriverManifest.Parse(d, byBinary, byLabel);
        return (byBinary, byLabel);
    }

    private const string ThirdParty = """
        <?xml version="1.0" encoding="UTF-8"?>
        <driversList>
        <devGroup group="CCDs">
                <device label="ZWO CCD" mdpd="true" manufacturer="ZWO">
                        <driver name="ZWO CCD">indi_asi_ccd</driver>
                        <version>2.7</version>
                </device>
        </devGroup>
        </driversList>
        """;

    private const string Core = """
        <?xml version="1.0" encoding="UTF-8"?>
        <driversList>
        <devGroup group="Focusers">
                <device label="Sesto Senso 2" manufacturer="Primaluce Lab">
                        <driver name="Sesto Senso 2">indi_sestosenso2_focus</driver>
                        <version>1.0</version>
                </device>
        </devGroup>
        <devGroup group="Rotators">
                <device label="Rotator Lite V2" manufacturer="Wanderer Astro">
                        <driver name="Wanderer Rotator Lite V2">indi_wanderer_rotator_lite_v2</driver>
                        <version>1.0</version>
                </device>
        </devGroup>
        <devGroup group="Telescopes">
                <device label="Telescope Simulator">
                        <driver name="Telescope Simulator">indi_simulator_telescope</driver>
                </device>
        </devGroup>
        </driversList>
        """;

    [Test]
    public void TheBrandIsFoundByBinary() {
        var (byBinary, _) = Parse(Core, ThirdParty);

        Assert.Multiple(() => {
            Assert.That(byBinary["indi_sestosenso2_focus"], Is.EqualTo("Primaluce Lab"));
            Assert.That(byBinary["indi_wanderer_rotator_lite_v2"], Is.EqualTo("Wanderer Astro"));
            Assert.That(byBinary["indi_asi_ccd"], Is.EqualTo("ZWO"));
        });
    }

    /// <summary>indi-web reports the label, so that is the lookup that has to
    /// work in practice.</summary>
    [Test]
    public void TheBrandIsFoundByTheLabelIndiWebReports() {
        var (_, byLabel) = Parse(Core);

        Assert.Multiple(() => {
            Assert.That(byLabel["Sesto Senso 2"], Is.EqualTo("Primaluce Lab"));
            Assert.That(byLabel["Rotator Lite V2"], Is.EqualTo("Wanderer Astro"));
            // The driver's own name is a third thing a custom profile may use.
            Assert.That(byLabel["Wanderer Rotator Lite V2"], Is.EqualTo("Wanderer Astro"));
        });
    }

    /// <summary>About a hundred core entries carry no manufacturer. Those are
    /// absent, not blank: a brand of "" would make the picker show an empty
    /// bracket next to every simulator.</summary>
    [Test]
    public void AnEntryWithNoManufacturer_IsNotRecorded() {
        var (byBinary, byLabel) = Parse(Core);

        Assert.Multiple(() => {
            Assert.That(byBinary.ContainsKey("indi_simulator_telescope"), Is.False);
            Assert.That(byLabel.ContainsKey("Telescope Simulator"), Is.False);
        });
    }

    /// <summary>Seventy of these files are installed by a dozen packages. One
    /// of them being malformed must cost nothing but itself.</summary>
    [Test]
    public void AMalformedFile_DoesNotTakeTheOthersDown() {
        var (byBinary, _) = Parse("<driversList><device label=\"broken\"", Core);

        Assert.That(byBinary["indi_sestosenso2_focus"], Is.EqualTo("Primaluce Lab"));
    }

    [Test]
    public void TheFirstFileToClaimABinaryKeepsIt() {
        var other = ThirdParty.Replace("manufacturer=\"ZWO\"", "manufacturer=\"Somebody Else\"");
        var (byBinary, _) = Parse(ThirdParty, other);

        Assert.That(byBinary["indi_asi_ccd"], Is.EqualTo("ZWO"),
            "a duplicate entry must not flip the answer from run to run");
    }

    [Test]
    public void LookupIsCaseInsensitive_BecauseIndiWebIsNotConsistent() {
        var (_, byLabel) = Parse(Core);

        Assert.That(byLabel["sesto senso 2"], Is.EqualTo("Primaluce Lab"));
    }

    [Test]
    public void NothingToRead_IsNotAnError() {
        var (byBinary, byLabel) = Parse("");

        Assert.Multiple(() => {
            Assert.That(byBinary, Is.Empty);
            Assert.That(byLabel, Is.Empty);
        });
    }
}
