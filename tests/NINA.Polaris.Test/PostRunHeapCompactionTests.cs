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
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Every heavy path hands the large-object heap back when it finishes.
///
/// <para>The collector does get there on its own: measured on an OPi 4 Pro
/// with an ASI2600MC Pro, ten flats took the process to a 2.4 GB peak on a
/// 3.8 GB board and it was back to about 540 MB minutes later with nothing
/// restarted. Nothing was leaking. The high water mark is the problem, because
/// that is what decides whether the next run meets the out-of-memory killer,
/// and one frame off a 26 MP one-shot-colour camera is 52 MB before the
/// preview path debayers it into three planes.</para>
///
/// <para>Live stacking, the native guider and the GraXpert hook all compacted
/// after their heavy phase. AUTORUN, which churns more than any of them, did
/// not. This is the pair that was missed.</para>
/// </summary>
[TestFixture]
public class PostRunHeapCompactionTests {

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Source(params string[] parts) {
        var here = Path.GetDirectoryName(Here())!;
        var all = new List<string> { here, "..", "..", "src", "NINA.Polaris", "Services" };
        all.AddRange(parts);
        var path = Path.GetFullPath(Path.Combine(all.ToArray()));
        Assert.That(File.Exists(path), $"nao achei {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The compaction mode on its own does nothing: it is a hint
    /// consumed by the next blocking gen-2 collection, so both halves have to
    /// be there.</summary>
    private static void AssertCompacts(string src, string where) {
        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("LargeObjectHeapCompactionMode"),
                where + " does not ask for the LOH to be compacted");
            Assert.That(src, Does.Match(@"GC\.Collect\(\s*(2|GC\.MaxGeneration)"),
                where + " sets the mode but never runs the collection that honours it");
        });
    }

    [Test]
    public void TheRunCompactsWhenItFinishes() {
        var src = Source("SequenceEngine.cs");
        AssertCompacts(src, "SequenceEngine");

        Assert.Multiple(() => {
            // In the finally, so a stop and a failure give the memory back too,
            // not just a run that reached its last frame.
            Assert.That(src, Does.Contain("CompactAfterRun();"));
            Assert.That(src.IndexOf("CompactAfterRun();", StringComparison.Ordinal),
                Is.GreaterThan(src.IndexOf("ReportGuidingForSession();", StringComparison.Ordinal)),
                "the compaction belongs at the very end of the run's finally");
        });
    }

    /// <summary>Per frame this would be a blocking full collection between
    /// every sub, which is the opposite of what anyone wants on an SBC.</summary>
    [Test]
    public void TheCompactionIsNotPerFrame() {
        var src = Source("SequenceEngine.cs");
        Assert.That(src.Split("CompactAfterRun();").Length - 1, Is.EqualTo(1),
            "one call site: the end of the run");
    }

    /// <summary>The paths that already did this have to keep doing it.</summary>
    [TestCase("LiveStackingService.cs")]
    [TestCase("NativeGuider.Loop.cs")]
    public void TheOtherHeavyPathsStillCompact(string file) {
        AssertCompacts(Source(file), file);
    }
}
