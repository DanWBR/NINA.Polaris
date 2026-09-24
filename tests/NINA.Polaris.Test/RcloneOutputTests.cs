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

using NINA.Polaris.Services.Storage;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Reading rclone's output. The shape of its JSON log is stable in practice but
/// is not a documented API, so the contract these tests pin is as much about
/// what happens when a line does NOT parse: the upload must survive a future
/// rclone that renames a field, and lose only the progress bar.
/// </summary>
[TestFixture]
public class RcloneOutputTests {
    // ---- progress ----

    [Test]
    public void StatsBytes_ReadsTheCumulativeCount() {
        const string line = """
        {"level":"info","msg":"stats","stats":{"bytes":1048576,"totalBytes":5242880,"transfers":0}}
        """;
        Assert.That(RcloneOutput.StatsBytes(line), Is.EqualTo(1048576));
    }

    [TestCase("Transferred:   1.234 MiB / 5 MiB")]
    [TestCase("{\"level\":\"info\",\"msg\":\"something else\"}")]
    [TestCase("{\"level\":\"info\",\"stats\":{\"transfers\":1}}")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    [TestCase("{ this is not json")]
    public void StatsBytes_IgnoresEverythingThatIsNotAStatLine(string? line) {
        Assert.That(RcloneOutput.StatsBytes(line), Is.Null);
    }

    // ---- errors ----

    [Test]
    public void ErrorMessage_PicksUpErrorAndCriticalOnly() {
        const string err = """{"level":"error","msg":"couldn't connect: no such host"}""";
        const string crit = """{"level":"critical","msg":"Failed to copy"}""";
        const string info = """{"level":"info","msg":"copied"}""";
        Assert.Multiple(() => {
            Assert.That(RcloneOutput.ErrorMessage(err), Is.EqualTo("couldn't connect: no such host"));
            Assert.That(RcloneOutput.ErrorMessage(crit), Is.EqualTo("Failed to copy"));
            Assert.That(RcloneOutput.ErrorMessage(info), Is.Null);
        });
    }

    [Test]
    public void JoinErrors_DeduplicatesAndKeepsItShortEnoughForTheCard() {
        var lines = new[] {
            """{"level":"error","msg":"quota exceeded"}""",
            """{"level":"error","msg":"quota exceeded"}""",
            """{"level":"info","msg":"retrying"}""",
            """{"level":"error","msg":"giving up"}"""
        };
        Assert.That(RcloneOutput.JoinErrors(lines), Is.EqualTo("quota exceeded; giving up"));
    }

    [Test]
    public void JoinErrors_TruncatesARunawayMessage() {
        var lines = new[] { "{\"level\":\"error\",\"msg\":\"" + new string('x', 500) + "\"}" };
        var joined = RcloneOutput.JoinErrors(lines, maxLength: 50);
        Assert.That(joined, Has.Length.LessThanOrEqualTo(53));
        Assert.That(joined, Does.EndWith("..."));
    }

    [Test]
    public void JoinErrors_IsNullWhenNothingWentWrong() {
        Assert.That(RcloneOutput.JoinErrors(new[] { """{"level":"info","msg":"fine"}""" }), Is.Null);
    }

    // ---- listings ----

    [Test]
    public void ParseLsjson_MapsRelativePathsToSizes() {
        const string json = """
        [
          {"Path":"rig/M31/lights/a.fits","Name":"a.fits","Size":1000,"IsDir":false},
          {"Path":"rig/M31/lights/b.fits","Name":"b.fits","Size":2000,"IsDir":false}
        ]
        """;
        var map = RcloneOutput.ParseLsjson(json);
        Assert.That(map, Is.Not.Null);
        Assert.That(map!["rig/M31/lights/a.fits"], Is.EqualTo(1000));
        Assert.That(map["rig/M31/lights/b.fits"], Is.EqualTo(2000));
    }

    [Test]
    public void ParseLsjson_LeavesOutDirectories() {
        const string json = """
        [{"Path":"rig","IsDir":true},{"Path":"rig/a.fits","Size":5,"IsDir":false}]
        """;
        var map = RcloneOutput.ParseLsjson(json);
        Assert.That(map!.Keys, Is.EquivalentTo(new[] { "rig/a.fits" }));
    }

    [Test]
    public void ParseLsjson_KeepsAnUnknownSizeDistinctFromZero() {
        // rclone reports -1 when a backend cannot say. Zero would mean "empty
        // file", and the backfill would skip a file it cannot actually vouch for.
        const string json = """[{"Path":"a.fits","Size":-1,"IsDir":false}]""";
        Assert.That(RcloneOutput.ParseLsjson(json)!["a.fits"], Is.EqualTo(-1));
    }

    [Test]
    public void ParseLsjson_AnEmptyRemoteIsAnEmptyMapNotAFailure() {
        var map = RcloneOutput.ParseLsjson("[]");
        Assert.That(map, Is.Not.Null);
        Assert.That(map, Is.Empty);
    }

    [TestCase("not json")]
    [TestCase("{\"Path\":\"a\"}")]
    [TestCase("")]
    [TestCase(null)]
    public void ParseLsjson_ReturnsNullWhenItCannotBeTrusted(string? json) {
        // Null is the interface's documented "cannot enumerate cheaply", which
        // makes the backfill queue everything rather than skip wrongly.
        Assert.That(RcloneOutput.ParseLsjson(json), Is.Null);
    }

    [Test]
    public void ParseRemotes_ReadsNameAndType() {
        const string output = "gdrive:       drive\nnas.1:        webdav\n";
        var remotes = RcloneOutput.ParseRemotes(output);
        Assert.That(remotes, Has.Count.EqualTo(2));
        Assert.That(remotes[0], Is.EqualTo(("gdrive", "drive")));
        Assert.That(remotes[1], Is.EqualTo(("nas.1", "webdav")));
    }

    [Test]
    public void ParseRemotes_OfNothingIsEmpty() {
        Assert.That(RcloneOutput.ParseRemotes(""), Is.Empty);
        Assert.That(RcloneOutput.ParseRemotes(null), Is.Empty);
    }

    // ---- exit codes ----

    [TestCase(0, true, false)]
    [TestCase(9, true, false)]     // nothing to transfer: the destination matched
    [TestCase(1, false, false)]    // our bug, retrying repeats it
    [TestCase(2, false, true)]
    [TestCase(3, false, false)]
    [TestCase(4, false, false)]    // the local file vanished
    [TestCase(5, false, true)]
    [TestCase(6, false, false)]
    [TestCase(7, false, false)]
    [TestCase(42, false, true)]    // unknown: assume it is worth another go
    public void Classify_SaysWhetherItWorkedAndWhetherToTryAgain(int code, bool ok, bool retryable) {
        var r = RcloneOutput.Classify(code);
        Assert.That(r.Success, Is.EqualTo(ok));
        Assert.That(r.Retryable, Is.EqualTo(retryable));
        if (!ok) Assert.That(r.Message, Is.Not.Null.And.Not.Empty);
    }
}
