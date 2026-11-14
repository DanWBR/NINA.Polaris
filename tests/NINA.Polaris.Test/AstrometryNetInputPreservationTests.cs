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

using NINA.Polaris.Services.PlateSolving;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class AstrometryNetInputPreservationTests {
    private string _dir = null!;

    [SetUp]
    public void SetUp() => Directory.CreateDirectory(_dir = Path.Combine(
        Path.GetTempPath(), "solve-input-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    [TestCase(".xyls")]
    [TestCase(".wcs")]
    [TestCase(".axy")]
    [TestCase(".corr")]
    [TestCase(".match")]
    [TestCase(".rdls")]
    [TestCase(".solved")]
    [TestCase(".new")]
    [TestCase(".ini")]
    public void Cleanup_NeverDeletesItsInputEvenWhenItMatchesASidecar(string extension) {
        var input = Path.Combine(_dir, "frame" + extension);
        File.WriteAllText(input, "original input bytes");
        var unrelated = Path.Combine(_dir, "another-frame.xyls");
        File.WriteAllText(unrelated, "another input");
        AstrometryNetLocalSolver.CleanupSidecars(input);
        Assert.Multiple(() => {
            Assert.That(File.Exists(input), Is.True, input);
            Assert.That(File.ReadAllText(input), Is.EqualTo("original input bytes"));
            Assert.That(File.ReadAllText(unrelated), Is.EqualTo("another input"));
        });
    }

    [Test]
    public void Cleanup_FitsInputIsRetainedAndGeneratedSidecarsAreRemoved() {
        var input = Path.Combine(_dir, "frame.fits");
        File.WriteAllText(input, "original FITS");
        var sidecars = new[] { ".wcs", ".axy", ".corr", ".match", ".rdls", ".solved", ".xyls", "-indx.xyls", ".new", ".ini" };
        foreach (var ext in sidecars) File.WriteAllText(Path.Combine(_dir, "frame" + ext), "temporary");
        AstrometryNetLocalSolver.CleanupSidecars(input);
        Assert.That(File.ReadAllText(input), Is.EqualTo("original FITS"));
        Assert.That(Directory.GetFiles(_dir), Is.EquivalentTo(new[] { input }));
    }

    [Test]
    public void Cleanup_RelativeInputWithDotSegments_IsPreserved() {
        var absolute = Path.Combine(_dir, "frame.xyls");
        File.WriteAllText(absolute, "original input bytes");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory,
            Path.Combine(_dir, "frame.xyls"));
        relative = Path.Combine(Path.GetDirectoryName(relative)!, ".", "frame.xyls");
        AstrometryNetLocalSolver.CleanupSidecars(relative);
        Assert.That(File.ReadAllText(absolute), Is.EqualTo("original input bytes"));
    }

    [Test]
    public void Cleanup_ConservativelyPreservesCaseVariantsOfTheInput() {
        var input = Path.Combine(_dir, "frame.XYLS");
        File.WriteAllText(input, "original input bytes");
        var lower = Path.Combine(_dir, "frame.xyls");
        if (!File.Exists(lower)) File.WriteAllText(lower, "case-variant input");
        AstrometryNetLocalSolver.CleanupSidecars(input);
        Assert.That(File.ReadAllText(input), Is.EqualTo("original input bytes"));
        Assert.That(File.Exists(lower), Is.True);
    }
}
