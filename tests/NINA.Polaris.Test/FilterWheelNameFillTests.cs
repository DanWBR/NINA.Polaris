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

using System.Xml.Linq;
using NINA.INDI.Devices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class FilterWheelNameFillTests {
    /// <summary>The ZWO EFW config on the SV550 rig, 2026-11-10: 5 names saved
    /// for an 8-position wheel.</summary>
    private const string Sv550Config = """
        <INDIDriver>
        <newSwitchVector device='ZWO EFW' name='FILTER_UNIDIRECTIONAL_MOTION'>
          <oneSwitch name='INDI_ENABLED'>
              Off
          </oneSwitch>
          <oneSwitch name='INDI_DISABLED'>
              On
          </oneSwitch>
        </newSwitchVector>
        <newNumberVector device='ZWO EFW' name='FILTER_SLOT'>
          <oneNumber name='FILTER_SLOT_VALUE'>
              1
          </oneNumber>
        </newNumberVector>
        <newTextVector device='ZWO EFW' name='FILTER_NAME'>
          <oneText name='FILTER_SLOT_NAME_1'>
              S
          </oneText>
          <oneText name='FILTER_SLOT_NAME_2'>
              H
          </oneText>
          <oneText name='FILTER_SLOT_NAME_3'>
              O
          </oneText>
          <oneText name='FILTER_SLOT_NAME_4'>
              I
          </oneText>
          <oneText name='FILTER_SLOT_NAME_5'>
              U
          </oneText>
        </newTextVector>
        </INDIDriver>
        """;

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "fwfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string[] NamesIn(string file) =>
        XDocument.Load(file).Root!.Elements("newTextVector")
            .Single(e => (string?)e.Attribute("name") == "FILTER_NAME")
            .Elements("oneText").Select(e => e.Value.Trim()).ToArray();

    [Test]
    public void TheNameListGrowsToEverySlotAndKeepsTheRest() {
        var file = Path.Combine(_dir, "ZWO EFW_config.xml");
        File.WriteAllText(file, Sv550Config);
        var names = new[] { "S", "H", "O", "I", "U", "Filter 6", "Filter 7", "Filter 8" };

        IndiFilterWheel.WriteFilterNamesToConfig(file, "ZWO EFW", names);

        var doc = XDocument.Load(file);
        Assert.Multiple(() => {
            Assert.That(NamesIn(file), Is.EqualTo(names));
            Assert.That(doc.Root!.Elements().Count(), Is.EqualTo(3), "the other vectors stay");
            Assert.That(doc.Root!.Elements("newTextVector").Single()
                .Elements("oneText").Last().Attribute("name")!.Value, Is.EqualTo("FILTER_SLOT_NAME_8"));
            Assert.That(doc.Root!.Elements("newTextVector").Single().Attribute("device")!.Value,
                Is.EqualTo("ZWO EFW"));
        });
    }

    [Test]
    public void AMissingConfigIsCreated() {
        var file = Path.Combine(_dir, "sub", "ZWO EFW_config.xml");
        IndiFilterWheel.WriteFilterNamesToConfig(file, "ZWO EFW", new[] { "L", "R", "G" });
        Assert.That(NamesIn(file), Is.EqualTo(new[] { "L", "R", "G" }));
    }

    [Test]
    public void OnlyALocalIndiServerHasItsConfigHere() {
        Assert.Multiple(() => {
            Assert.That(IndiFilterWheel.IsLocalHost("localhost"));
            Assert.That(IndiFilterWheel.IsLocalHost("127.0.0.1"));
            Assert.That(IndiFilterWheel.IsLocalHost("::1"));
            Assert.That(IndiFilterWheel.IsLocalHost("192.168.3.123"), Is.False);
        });
    }
}
