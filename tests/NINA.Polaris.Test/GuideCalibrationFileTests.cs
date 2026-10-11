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
using System.IO;
using System.Runtime.CompilerServices;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The native guider's calibration file and matching rules. Field, 2026-10-10:
/// a rig with a ZWO AM3 reported "no calibration" after only the host's
/// network settings had changed, and the operator got going again with a file
/// saved by hand.
/// </summary>
[TestFixture]
public class GuideCalibrationFileTests {

    private const string Key = "cam=native:asi120mm|bin=1|fl=240|mount=indi:lx200 am5";

    private static NativeCalibrationData Cal(string key, string when, double rate = 0.01)
        => new() { Key = key, XRate = rate, YRate = rate, SavedAtUtc = when };

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-guidecal-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- matching ----

    [Test]
    public void TheSameMountOnANewAddressIsTheSameMount() {
        Assert.Multiple(() => {
            Assert.That(GuideCalibrationFile.SameKey(
                "cam=native:asi120mm|bin=1|fl=240|mount=alpaca:192.168.3.20:11111:0",
                "cam=native:asi120mm|bin=1|fl=240|mount=alpaca:192.168.3.57:11111:0"), Is.True);
            Assert.That(GuideCalibrationFile.SameKey(
                "cam=native:x|bin=1|fl=240|mount=synscan-wifi:192.168.3.159",
                "cam=native:x|bin=1|fl=240|mount=synscan-wifi:10.0.0.4"), Is.True);
            Assert.That(GuideCalibrationFile.SameKey(Key, Key.Replace("bin=1", "bin=2")), Is.False);
        });
    }

    [Test]
    public void AnExactMatchWins_NewestFirst() {
        var list = new List<NativeCalibrationData> {
            Cal(Key, "2026-10-01T00:00:00Z", 0.01),
            Cal(Key, "2026-10-09T00:00:00Z", 0.02),
            Cal(Key.Replace("lx200 am5", "eqmod"), "2026-10-10T00:00:00Z", 0.03),
        };
        var (m, exact) = GuideCalibrationFile.Find(list, Key);
        Assert.That(exact, Is.True);
        Assert.That(m!.XRate, Is.EqualTo(0.02));
    }

    /// <summary>Same guide camera, binning and focal length: the geometry and
    /// pixel rates are its; the mount was recorded differently (or not at all,
    /// when the rig's selection had gone blank).</summary>
    [Test]
    public void TheSameGuideCameraIsANearMatch() {
        var list = new List<NativeCalibrationData> { Cal(Key, "2026-10-09T00:00:00Z") };
        var (m, exact) = GuideCalibrationFile.Find(list, "cam=native:asi120mm|bin=1|fl=240|mount=indi:");
        Assert.That(m, Is.Not.Null);
        Assert.That(exact, Is.False);
    }

    [Test]
    public void AnotherGuideCameraOrScaleIsNoMatch() {
        var list = new List<NativeCalibrationData> { Cal(Key, "2026-10-09T00:00:00Z") };
        Assert.Multiple(() => {
            Assert.That(GuideCalibrationFile.Find(list, Key.Replace("asi120mm", "asi174mm")).Match, Is.Null);
            Assert.That(GuideCalibrationFile.Find(list, Key.Replace("fl=240", "fl=400")).Match, Is.Null);
        });
    }

    [Test]
    public void ProfileAndFileMerge_OneEntryPerKey_NewestKept() {
        var profile = new List<NativeCalibrationData> { Cal(Key, "2026-10-01T00:00:00Z", 0.01) };
        var file = new List<NativeCalibrationData> {
            Cal(Key, "2026-10-09T00:00:00Z", 0.02),
            Cal(Key.Replace("bin=1", "bin=2"), "2026-10-05T00:00:00Z", 0.05),
        };
        var merged = GuideCalibrationFile.Merge(profile, file);
        Assert.Multiple(() => {
            Assert.That(merged.Count, Is.EqualTo(2));
            Assert.That(GuideCalibrationFile.Find(merged, Key).Match!.XRate, Is.EqualTo(0.02));
        });
    }

    // ---- the file ----

    [Test]
    public void TheFileKeepsEveryGearsCalibration_AndRemovesOnlyTheOneAskedFor() {
        var path = GuideCalibrationFile.PathFor(_dir, "rig-1");
        GuideCalibrationFile.Upsert(path, Cal(Key, "2026-10-01T00:00:00Z", 0.01));
        GuideCalibrationFile.Upsert(path, Cal(Key.Replace("bin=1", "bin=2"), "2026-10-02T00:00:00Z", 0.02));
        GuideCalibrationFile.Upsert(path, Cal(Key, "2026-10-03T00:00:00Z", 0.03));   // replaces the first
        var list = GuideCalibrationFile.Load(path);
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(GuideCalibrationFile.Find(list, Key).Match!.XRate, Is.EqualTo(0.03));

        GuideCalibrationFile.Remove(path, Key);
        list = GuideCalibrationFile.Load(path);
        Assert.Multiple(() => {
            Assert.That(list.Count, Is.EqualTo(1));
            Assert.That(list[0].Key, Does.Contain("bin=2"));
            Assert.That(File.Exists(path + ".tmp"), Is.False, "written through a temp file and moved");
        });
    }

    [Test]
    public void ADamagedFileReadsAsEmpty_NotAsAnError() {
        var path = GuideCalibrationFile.PathFor(_dir, "rig-1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Assert.That(GuideCalibrationFile.Load(path), Is.Empty);
    }

    // ---- the guider reads it on Loop ----

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Src(string file) => File.ReadAllText(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris", "Services", file)));

    [Test]
    public void LoopAlwaysReadsTheSavedCalibration() {
        var g = Src("NativeGuider.cs");
        var at = g.IndexOf("public Task LoopAsync(CancellationToken ct = default) {", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        var body = g.Substring(at, 1400);
        Assert.Multiple(() => {
            Assert.That(body, Does.Contain("TryRestoreCalibration()"));
            Assert.That(body.IndexOf("TryRestoreCalibration()", StringComparison.Ordinal),
                Is.LessThan(body.IndexOf("StartLoopAsync(LoopMode.Loop)", StringComparison.Ordinal)));
        });
    }

    /// <summary>Load file lives inside the Review panel, which drew its plot
    /// only when opened: a loaded calibration showed an empty plot.</summary>
    [Test]
    public void LoadingAFileRedrawsTheReviewPlot() {
        var js = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..",
            "src", "NINA.Polaris", "wwwroot", "js", "app.js")));
        var at = js.IndexOf("async importGuideCalibration()", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        Assert.That(js.Substring(at, 1600), Does.Contain("this._redrawCalReviewSoon();"));
    }

    [Test]
    public void SaveFileFindsThePersistedRecordWithItsPlotPoints() {
        var cal = Src("NativeGuider.Calibration.cs");
        var at = cal.IndexOf("public NativeCalibrationData? ExportCalibrationData()", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        Assert.That(cal.Substring(at, 900), Does.Contain("GuideCalibrationFile.Find("));
    }

    [Test]
    public void EveryPlaceThatChangesACalibrationAlsoWritesTheFile() {
        var cal = Src("NativeGuider.Calibration.cs");
        var g = Src("NativeGuider.cs");
        Assert.Multiple(() => {
            Assert.That(System.Text.RegularExpressions.Regex.Matches(cal, @"FileUpsert\(d(ata)?\);").Count,
                Is.GreaterThanOrEqualTo(3), "after calibrating, importing, and restoring");
            Assert.That(g, Does.Contain("FileRemove(key);"), "clear");
            Assert.That(g, Does.Contain("FileUpsert(Rig.NativeCalibrations?.LastOrDefault("), "flip");
            Assert.That(cal, Does.Contain("GuideCalibrationFile.Merge(Rig.NativeCalibrations,"),
                "restore reads the profile and the file together");
        });
    }
}
