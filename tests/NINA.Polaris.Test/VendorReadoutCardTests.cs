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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NINA.Image.ImageData;
using NINA.Image.FileFormat.FITS;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Sensor readout modes in the FITS header.
///
/// A ToupTek ATR2600C has low noise, conversion gain and high full well
/// switches. Each changes how the sensor reads out, so a light taken in one
/// mode does not calibrate against a dark taken in another, and until now
/// nothing in the saved file said which was in force (issue #31). Ekos records
/// the first and third as LOWNOISE and FULLWELL; conversion gain has no
/// established keyword.
/// </summary>
[TestFixture]
public class VendorReadoutCardTests {

    private static IndiSwitchProperty Switch(params (string Element, bool On)[] elements) {
        var p = new IndiSwitchProperty();
        foreach (var (e, on) in elements) p.Values[e] = on;
        return p;
    }

    private static Func<string, IndiSwitchProperty?> Device(
            Dictionary<string, IndiSwitchProperty> props)
        => name => props.TryGetValue(name, out var p) ? p : null;

    [Test]
    public void TheThreeToupTekModesBecomeCards() {
        var cards = IndiVendorReadoutCards.Collect(Device(new() {
            ["TC_LOW_NOISE"] = Switch(("INDI_ENABLED", true), ("INDI_DISABLED", false)),
            ["TC_HIGHFULLWELL"] = Switch(("INDI_ENABLED", false), ("INDI_DISABLED", true)),
            ["TC_CONVERSION_GAIN"] = Switch(("GAIN_LOW", false), ("GAIN_HIGH", true)),
        }));

        Assert.Multiple(() => {
            Assert.That(cards.Single(c => c.Keyword == "LOWNOISE").Value, Is.EqualTo("ON"));
            Assert.That(cards.Single(c => c.Keyword == "FULLWELL").Value, Is.EqualTo("OFF"));
            Assert.That(cards.Single(c => c.Keyword == "CONVMODE").Value, Is.EqualTo("HIGH"));
        });
    }

    /// <summary>Every astro camera that is not one of these contributes
    /// nothing, and that has to cost no header space and no error.</summary>
    [Test]
    public void ACameraWithNoneOfThem_ContributesNoCards() {
        var cards = IndiVendorReadoutCards.Collect(Device(new() {
            ["CCD_GAIN"] = Switch(("GAIN", true)),
        }));

        Assert.That(cards, Is.Empty);
    }

    /// <summary>A vector with nothing switched on is a driver mid-update, not
    /// a mode. Guessing one would put a wrong value in a file that outlives
    /// the session.</summary>
    [Test]
    public void AVectorWithNothingOn_IsSkipped() {
        var cards = IndiVendorReadoutCards.Collect(Device(new() {
            ["TC_LOW_NOISE"] = Switch(("INDI_ENABLED", false), ("INDI_DISABLED", false)),
        }));

        Assert.That(cards, Is.Empty);
    }

    /// <summary>An element the table has never seen is recorded under its own
    /// name. The point of the card is that the mode was not the default; an
    /// unfamiliar string says that, and silence does not.</summary>
    [Test]
    public void AnUnknownElement_IsRecordedVerbatim() {
        var cards = IndiVendorReadoutCards.Collect(Device(new() {
            ["TC_CONVERSION_GAIN"] = Switch(("GAIN_SOMETHING_NEW", true)),
        }));

        Assert.That(cards.Single().Value, Is.EqualTo("GAIN_SOMETHING_NEW"));
    }

    [Test]
    public void AReaderThatThrows_DoesNotTakeTheCaptureDown() {
        var cards = IndiVendorReadoutCards.Collect(
            _ => throw new System.InvalidOperationException("driver went away"));

        Assert.That(cards, Is.Empty);
    }

    /// <summary>And the whole point: they reach the file.</summary>
    [Test]
    public void TheCardsAreWrittenIntoTheFitsHeader() {
        var meta = new ImageMetaData();
        meta.Camera.Name = "ToupTek ATR2600C";
        meta.Camera.VendorCards = new List<VendorFitsCard> {
            new("LOWNOISE", "ON", "Low noise readout mode"),
            new("CONVMODE", "HIGH", "Conversion gain mode"),
        };
        var props = new ImageProperties { Width = 4, Height = 4, BitDepth = 16 };
        var data = new BaseImageData(new ushort[16], props, meta);

        var path = Path.Combine(Path.GetTempPath(), "polaris-vendorcards-" + System.Guid.NewGuid().ToString("N") + ".fits");
        try {
            FITSWriter.Write(data, path);
            var header = System.Text.Encoding.ASCII.GetString(
                File.ReadAllBytes(path).Take(14400).ToArray());

            Assert.Multiple(() => {
                Assert.That(header, Does.Contain("LOWNOISE= 'ON'"));
                Assert.That(header, Does.Contain("CONVMODE= 'HIGH'"));
                Assert.That(header, Does.Contain("Low noise readout mode"));
            });
        } finally {
            try { File.Delete(path); } catch { }
        }
    }
}
