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
using NUnit.Framework;
using NINA.Polaris.Services.Logging;

namespace NINA.Polaris.Test;

/// <summary>Verifies the native guide log is emitted in PHD2's Guide Log
/// format (Log version 2.5) so it opens in PHD2 Log Viewer.</summary>
[TestFixture]
public class GuideLogWriterTests {
    private static readonly DateTime Utc = new(2026, 7, 7, 3, 12, 0, DateTimeKind.Utc);

    [Test]
    public void VersionLine_MatchesPhd2Shape() {
        var line = GuideLogWriter.FormatVersionLine(Utc);
        Assert.That(line, Does.StartWith("PHD2 version "));
        Assert.That(line, Does.Contain("Log version 2.5"));
        Assert.That(line, Does.Contain("Log enabled at "));
    }

    [Test]
    public void GuidingHeader_HasBeginsLineAndColumnHeader() {
        var h = GuideLogWriter.FormatGuidingBeginsHeader(
            Utc, "My Rig", "ASI120MM", 1000, 3.5, 100.0, 150.0);
        Assert.That(h, Does.Contain("Guiding Begins at "));
        Assert.That(h, Does.Contain("Equipment Profile = My Rig"));
        Assert.That(h, Does.Contain("Exposure = 1000 ms"));
        // The exact PHD2 column header PHD2 Log Viewer parses.
        Assert.That(h, Does.Contain(
            "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,"
            + "RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode"));
    }

    [Test]
    public void FrameRow_HasEighteenFields_MountQuoted_InvariantDecimals() {
        var row = GuideLogWriter.FormatFrameRow(
            frame: 1, timeSec: 0.25, raPx: 0.123, decPx: -0.456,
            raDurationMs: 100, raDir: "East", decDurationMs: 50, decDir: "N",
            starMass: 1234.5, snr: 45.32);
        var cols = row.Split(',');
        Assert.That(cols.Length, Is.EqualTo(18), "PHD2 guide row has 18 columns");
        Assert.That(cols[0], Is.EqualTo("1"));
        Assert.That(cols[1], Is.EqualTo("0.250"));      // invariant decimal point
        Assert.That(cols[2], Is.EqualTo("\"Mount\""));
        Assert.That(cols[10], Is.EqualTo("E"), "direction reduced to a single char");
        Assert.That(cols[12], Is.EqualTo("N"));
        Assert.That(cols[^1], Is.EqualTo("0"));         // ErrorCode
    }

    [Test]
    public void FrameRow_OmitsDirectionWhenNoPulse() {
        var row = GuideLogWriter.FormatFrameRow(
            2, 0.5, 0, 0, raDurationMs: 0, raDir: "East", decDurationMs: 0, decDir: "North",
            starMass: 0, snr: 0);
        var cols = row.Split(',');
        Assert.That(cols[9], Is.EqualTo("0"));   // RADuration
        Assert.That(cols[10], Is.EqualTo(""));   // RADirection blank when duration 0
        Assert.That(cols[12], Is.EqualTo(""));   // DECDirection blank
    }

    [Test]
    public void DitherInfo_IsParseablePhd2Marker() {
        var s = GuideLogWriter.FormatDitherInfo(0.5, -0.25, 100.5, 150.25);
        Assert.That(s, Does.StartWith("INFO: DITHER by "));
        Assert.That(s, Does.Contain("new lock pos = "));
    }

    [Test]
    public void Summary_HasClosedAndCounts() {
        var s = GuideLogWriter.FormatSummary(calCnt: 0, guideCnt: 42, guideDurSec: 3600);
        Assert.That(s, Does.Contain("Log closed at "));
        Assert.That(s, Does.Contain("Log Summary: calcnt:0 gcnt:42 gdur:3600 gacnt:0"));
    }

    [Test]
    public void Writer_CreatesFileAndAppends() {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "polaris_guidelog_test_" + Guid.NewGuid().ToString("N") + ".txt");
        try {
            using (var w = new GuideLogWriter(path)) {
                w.WriteLine(GuideLogWriter.FormatVersionLine(Utc));
                w.WriteLine(GuideLogWriter.FormatFrameRow(1, 0.25, 0.1, 0.2, 10, "E", 5, "N", 100, 30));
            }
            var text = System.IO.File.ReadAllText(path);
            Assert.That(text, Does.StartWith("PHD2 version "));
            Assert.That(text, Does.Contain("\"Mount\""));
        } finally {
            try { System.IO.File.Delete(path); } catch { }
        }
    }
}
