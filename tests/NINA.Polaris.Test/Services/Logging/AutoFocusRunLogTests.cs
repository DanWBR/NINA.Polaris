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
using NINA.Polaris.Services.Logging;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>Autofocus runs were only in the 7-day session log, so comparing
/// two nights ("focus or seeing?") meant digging them out of jsonl files. Each
/// run now gets a JSON file of its own, kept for 90 days.</summary>
[TestFixture]
public class AutoFocusRunLogTests {
    private string _dir = null!;

    [SetUp] public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-af-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown] public void TearDown() {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AutoFocusRunLog.Run Sample(bool success = true, string? filter = "Ha") => new(
        1, "main", "Gemini EAF", "AE676C", filter, 11.5,
        new AutoFocusRunLog.Settings(4, 50, 2.0, 1, 1, "HFR", 0.7, 2),
        new AutoFocusResult {
            Success = success, StartPosition = 6400, BestPosition = 6480, FinalPosition = 6480,
            FinalMeasuredHfr = 2.31, RSquared = 0.96, Method = "TrendHyperbolic", Attempts = 1,
            StartedAt = new DateTime(2026, 10, 17, 23, 10, 5, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 17, 23, 12, 40, DateTimeKind.Utc),
            Error = success ? null : "R² 0.41 below 0.70",
            Points = new() {
                new AutoFocusPoint { Position = 6300, HFR = double.NaN },   // too far out to measure
                new AutoFocusPoint { Position = 6400, HFR = 3.1, StarCount = 40 },
                new AutoFocusPoint { Position = 6500, HFR = 2.3, StarCount = 55 },
            }
        });

    [Test]
    public void ARunIsWrittenAndListedWithItsSummary() {
        var path = AutoFocusRunLog.Write(Sample(), _dir);
        Assert.That(File.Exists(path), Is.True);
        var s = AutoFocusRunLog.List(_dir).Single();
        Assert.That(s.Name, Does.StartWith("af_").And.EndWith("_Ha.json"));
        Assert.That(s.Success, Is.True);
        Assert.That(s.Filter, Is.EqualTo("Ha"));
        Assert.That(s.TemperatureC, Is.EqualTo(11.5));
        Assert.That(s.FinalHfr, Is.EqualTo(2.31));
        Assert.That(s.RSquared, Is.EqualTo(0.96));
        Assert.That(s.FinalPosition, Is.EqualTo(6480));
        Assert.That(s.Points, Is.EqualTo(3), "a point with no HFR does not break the file");
    }

    [Test]
    public void AFailedRunIsKeptToo() {
        AutoFocusRunLog.Write(Sample(success: false, filter: null), _dir);
        var s = AutoFocusRunLog.List(_dir).Single();
        Assert.That(s.Success, Is.False);
        Assert.That(s.Error, Does.Contain("below"));
        Assert.That(s.Name, Does.Not.Contain("__"));
    }

    [Test]
    public void RunsOlderThan90DaysAreSwept() {
        var path = AutoFocusRunLog.Write(Sample(), _dir);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(91));
        AutoFocusRunLog.Sweep(_dir, DateTime.UtcNow);
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void AFilterNameIsMadeSafeForTheFileName() {
        var name = AutoFocusRunLog.FileName(new DateTime(2026, 10, 17, 23, 0, 0, DateTimeKind.Utc), "L/QEF 2\"");
        Assert.That(name, Does.Not.Contain("/").And.Not.Contain("\"").And.Not.Contain(" "));
        Assert.That(name, Does.StartWith("af_").And.EndWith(".json"));
    }

    [Test]
    public void AnUnreadableFileIsListedByName() {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "af_broken.json"), "{ not json");
        var s = AutoFocusRunLog.List(_dir).Single();
        Assert.That(s.Name, Is.EqualTo("af_broken.json"));
        Assert.That(s.Error, Is.EqualTo("unreadable file"));
    }
}
