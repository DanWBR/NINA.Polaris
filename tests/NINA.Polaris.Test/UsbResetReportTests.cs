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
/// The report the USB re-enumeration leaves behind. The device lists are the
/// whole value of the button: re-enumerating recovers a device the kernel lost
/// track of, but it cannot revive one that is electrically absent, and only
/// the before/after comparison tells the operator which case they are in.
///
/// The sample below is the real output from an Orange Pi 4 Pro in the field.
/// </summary>
[TestFixture]
public class UsbResetReportTests {

    private const string FieldSample = """
        {"ok":true,"method":"hub-rebind","reset":["3-1"],"skipped":[],
         "before":["2-1 f266:9a0a SVBONY SV605CC","3-1 1a40:0101 USB 2.0 Hub",
                   "3-1.3 2109:2817 USB2.0 Hub","3-1.3.2 03c3:4001 ZWO Device",
                   "3-1.3.5 2109:8817 USB Billboard Device"],
         "after":["2-1 f266:9a0a SVBONY SV605CC","3-1 1a40:0101 USB 2.0 Hub",
                  "3-1.3 2109:2817 USB2.0 Hub","3-1.3.2 03c3:4001 ZWO Device",
                  "3-1.3.5 2109:8817 USB Billboard Device"]}
        """;

    [Test]
    public void ParsesTheScriptsOwnOutput() {
        var r = UsbResetReport.Parse(FieldSample);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Method, Is.EqualTo("hub-rebind"));
        Assert.That(r.Reset, Is.EqualTo(new[] { "3-1" }));
        Assert.That(r.Skipped, Is.Empty);
        Assert.That(r.Before.Count, Is.EqualTo(5));
        Assert.That(r.After.Count, Is.EqualTo(5));
    }

    [Test]
    public void NothingChanged_IsReportedAsNothingChanged() {
        // The field case: a USB-C cable with no data pairs comes back as the
        // same Billboard device however many times it is re-enumerated. The
        // button must not claim to have fixed anything.
        var r = UsbResetReport.Parse(FieldSample)!;
        Assert.That(UsbResetReport.Appeared(r), Is.Empty);
        Assert.That(UsbResetReport.Disappeared(r), Is.Empty);
    }

    [Test]
    public void ADeviceThatCameBackIsNamed() {
        var json = """
            {"method":"hub-rebind","reset":["3-1"],"skipped":[],
             "before":["3-1 1a40:0101 USB 2.0 Hub"],
             "after":["3-1 1a40:0101 USB 2.0 Hub","3-1.2 0547:14ff USB2.0 Camera"]}
            """;
        var r = UsbResetReport.Parse(json)!;
        Assert.That(UsbResetReport.Appeared(r), Is.EqualTo(new[] { "3-1.2 0547:14ff USB2.0 Camera" }));
        Assert.That(UsbResetReport.Disappeared(r), Is.Empty);
    }

    [Test]
    public void ADeviceThatDidNotComeBackIsNamedToo() {
        // This is the one that saves an hour: the reset ran, and the focuser
        // is still gone, so it is a cable, not software.
        var json = """
            {"method":"hub-rebind","reset":["3-1"],"skipped":[],
             "before":["3-1 1a40:0101 USB 2.0 Hub","3-1.1 1a86:7523 CH340 serial converter"],
             "after":["3-1 1a40:0101 USB 2.0 Hub"]}
            """;
        var r = UsbResetReport.Parse(json)!;
        Assert.That(UsbResetReport.Disappeared(r),
            Is.EqualTo(new[] { "3-1.1 1a86:7523 CH340 serial converter" }));
    }

    [Test]
    public void TheProtectedChainIsReportedAsSkipped() {
        // A board that boots from a USB SSD must never re-enumerate its own
        // disk, and the operator should be able to see that it did not.
        var json = """
            {"method":"hub-rebind","reset":["3-1"],"skipped":["2-1","2-1.4"],
             "before":["2-1.4 174c:55aa ASMedia SSD"],"after":["2-1.4 174c:55aa ASMedia SSD"]}
            """;
        var r = UsbResetReport.Parse(json)!;
        Assert.That(r.Skipped, Is.EqualTo(new[] { "2-1", "2-1.4" }));
        Assert.That(r.Reset, Does.Not.Contain("2-1"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    public void GarbageIsNullRatherThanAnException(string? json) {
        Assert.That(UsbResetReport.Parse(json), Is.Null);
    }

    [Test]
    public void MissingFieldsDegradeToEmptyLists() {
        var r = UsbResetReport.Parse("""{"ok":true}""");
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Before, Is.Empty);
        Assert.That(r.After, Is.Empty);
        Assert.That(UsbResetReport.Appeared(r), Is.Empty);
    }
}
