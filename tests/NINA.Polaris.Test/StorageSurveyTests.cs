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

using System.Reflection;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// STORAGE-1. The survey reads lsblk, which cannot run in a test, but the two
/// pure pieces it depends on can be wrong in ways that matter: a mis-parsed
/// pair line would offer the operator the wrong disk, and a mis-parsed size
/// would show the wrong number next to it.
/// </summary>
[TestFixture]
public class StorageSurveyTests {

    private static Dictionary<string, string> Parse(string line) {
        var m = typeof(StorageSetupService).GetMethod("ParsePairs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Dictionary<string, string>)m.Invoke(null, new object?[] { line })!;
    }

    /// <summary>lsblk -P output, with the case that breaks column parsing: a
    /// model name containing spaces.</summary>
    [Test]
    public void ParsePairs_KeepsSpacesInsideQuotedValues() {
        const string line =
            "NAME=\"nvme0n1p1\" PKNAME=\"nvme0n1\" TYPE=\"part\" SIZE=\"931.5G\" FSTYPE=\"ext4\" " +
            "UUID=\"1b7f0e2a-1111-2222-3333-444455556666\" LABEL=\"astro data\" " +
            "MOUNTPOINT=\"\" RM=\"0\" MODEL=\"Samsung SSD 990 PRO 1TB\"";

        var kv = Parse(line);
        Assert.That(kv["NAME"], Is.EqualTo("nvme0n1p1"));
        Assert.That(kv["PKNAME"], Is.EqualTo("nvme0n1"));
        Assert.That(kv["FSTYPE"], Is.EqualTo("ext4"));
        Assert.That(kv["UUID"], Is.EqualTo("1b7f0e2a-1111-2222-3333-444455556666"));
        Assert.That(kv["LABEL"], Is.EqualTo("astro data"));
        Assert.That(kv["MODEL"], Is.EqualTo("Samsung SSD 990 PRO 1TB"),
            "a model with spaces must survive; this is why the parser is pair-based");
        Assert.That(kv["MOUNTPOINT"], Is.EqualTo(""));
    }

    [Test]
    public void ParsePairs_EmptyOrGarbage_YieldsNothing() {
        Assert.That(Parse(""), Is.Empty);
        Assert.That(Parse("not a pair line"), Is.Empty);
    }

    /// <summary>The size is shown to a person choosing between two disks, so
    /// the units have to be right even though the value is approximate.</summary>
    [TestCase("931.5G", 1000_000_000_000L, 60_000_000_000L)]
    [TestCase("1.8T", 1_979_000_000_000L, 90_000_000_000L)]
    [TestCase("512M", 536_870_912L, 1_000_000L)]
    [TestCase("64G", 68_719_476_736L, 100_000L)]
    public void ParseSize_ConvertsHumanUnits(string text, long expected, long tolerance) {
        var v = StorageSetupService.ParseSize(text);
        Assert.That(v, Is.EqualTo(expected).Within(tolerance), text);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("banana")]
    public void ParseSize_Garbage_IsZeroRatherThanAThrow(string text) {
        Assert.That(StorageSetupService.ParseSize(text), Is.EqualTo(0));
    }

    /// <summary>A comma decimal separator turns up on locale-aware systems and
    /// must not read as a different number.</summary>
    [Test]
    public void ParseSize_AcceptsACommaDecimal() {
        Assert.That(StorageSetupService.ParseSize("931,5G"),
            Is.EqualTo(StorageSetupService.ParseSize("931.5G")));
    }

    /// <summary>lsblk -b reports plain byte counts. The format guard compares
    /// against this number, so the digit path has to stay exact.</summary>
    [Test]
    public void ParseSize_PassesPlainByteCountsThrough() {
        Assert.That(StorageSetupService.ParseSize("1000204886016"),
            Is.EqualTo(1000204886016L));
    }

    // ---- STORAGE-2: the guard in front of the erase ----

    /// <summary>Whole disks only. A partition means the caller wants to format
    /// something inside a partition table this tool is about to replace, and
    /// anything else means the device string was built from something other
    /// than the survey.</summary>
    [TestCase("/dev/nvme0n1")]
    [TestCase("/dev/nvme1n2")]
    [TestCase("/dev/sda")]
    [TestCase("/dev/sdab")]
    [TestCase("/dev/mmcblk0")]
    public void IsWholeDiskNode_AcceptsWholeDisks(string dev) {
        Assert.That(StorageSetupService.IsWholeDiskNode(dev), Is.True, dev);
    }

    [TestCase("/dev/nvme0n1p1")]     // a partition
    [TestCase("/dev/sda1")]
    [TestCase("/dev/mmcblk0p3")]
    [TestCase("/dev/dm-0")]          // device mapper: not ours to repartition
    [TestCase("/dev/../dev/sda")]    // traversal
    [TestCase("/dev/sda;rm -rf /")]  // metacharacters
    [TestCase("/dev/sda ")]          // trailing space would split an argv
    [TestCase("sda")]
    [TestCase("")]
    [TestCase("/dev/")]
    public void IsWholeDiskNode_RefusesEverythingElse(string dev) {
        Assert.That(StorageSetupService.IsWholeDiskNode(dev), Is.False, dev);
    }

    /// <summary>The regex is anchored at both ends. A newline would otherwise
    /// let "$" match mid-string and smuggle a second line past the guard.
    /// </summary>
    [Test]
    public void IsWholeDiskNode_RefusesAnEmbeddedNewline() {
        Assert.That(StorageSetupService.IsWholeDiskNode("/dev/sda\n/dev/sdb"), Is.False);
        Assert.That(StorageSetupService.IsWholeDiskNode("/dev/sda\n"), Is.False);
    }
}
