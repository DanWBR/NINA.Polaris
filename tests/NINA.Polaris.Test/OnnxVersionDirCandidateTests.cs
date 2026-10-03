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

using NINA.Polaris.Services.Onnx;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Why background extraction was never accelerated, found 2026-10-03.
///
/// The ONNX model directories and the converted ones are named by different
/// hands and they drifted apart. Background extraction ships as
/// <c>bge-ai-models/graxpert-1.0.1</c> with a source prefix, while its converted
/// siblings are <c>rknn/bge-ai-models/1.0.1</c> and
/// <c>ncnn/bge-ai-models/1.0.1</c> without one. All three accelerator resolvers
/// took the ONNX directory name verbatim, so every lookup missed, each lane
/// returned null, and GraXpertService fell through to the Python CLI without
/// saying why: seconds per frame where the NPU costs about ninety milliseconds.
///
/// Denoise hid it. Its directories are <c>2.0.0</c> on both sides, so they match
/// by luck, and the interface badge reads "BGE + Denoise accelerated" while only
/// the second half was ever true.
///
/// These pin the candidate order, and in particular that the real on-disk
/// layout now resolves.
/// </summary>
[TestFixture]
public class OnnxVersionDirCandidateTests {

    /// <summary>The case that was broken: the prefixed BGE directory has to
    /// reach the unprefixed converted one.</summary>
    [Test]
    public void PrefixedVersion_OffersTheUnprefixedDirectory() {
        var candidates = OnnxModelRegistry.VersionDirCandidates("graxpert-1.0.1");

        Assert.That(candidates, Does.Contain("1.0.1"),
            "this is the directory rknn/ and ncnn/ actually use");
        Assert.That(candidates[0], Is.EqualTo("graxpert-1.0.1"),
            "an exact match still has to win, so a tree that does use the prefix"
            + " (qnn/bge-ai-models/polaris-1.0.0) is unaffected");
    }

    /// <summary>A quantised ONNX directory may resolve to the accelerator's
    /// plain one: a converted model carries its own precision, so the tag is
    /// not part of its identity.</summary>
    [Test]
    public void QuantTag_IsDroppedAsALastResort() {
        var candidates = OnnxModelRegistry.VersionDirCandidates("graxpert-1.0.1-fp16");

        Assert.That(candidates, Is.EqualTo(new[] {
            "graxpert-1.0.1-fp16",
            "1.0.1-fp16",
            "graxpert-1.0.1",
            "1.0.1",
        }), "most specific first, then prefix off, then tag off, then bare");
    }

    /// <summary>Denoise, which matched by luck and must keep matching. One
    /// candidate, no churn.</summary>
    [Test]
    public void PlainVersion_IsItsOwnOnlyCandidate() {
        Assert.That(OnnxModelRegistry.VersionDirCandidates("2.0.0"),
            Is.EqualTo(new[] { "2.0.0" }));
    }

    /// <summary>The tile and normalisation tags are part of which model this is,
    /// not of its precision, so they ride along with every candidate.</summary>
    [Test]
    public void TileAndNormTags_AreKept() {
        var candidates = OnnxModelRegistry.VersionDirCandidates("polaris-1.2-pct-512-fp16");

        Assert.That(candidates, Is.EqualTo(new[] {
            "polaris-1.2-pct-512-fp16",
            "1.2-pct-512-fp16",
            "polaris-1.2-pct-512",
            "1.2-pct-512",
        }));
    }

    /// <summary>A directory the grammar does not recognise is passed through
    /// untouched rather than guessed at.</summary>
    [TestCase("not-a-version")]
    [TestCase("1")]
    [TestCase("")]
    public void UnparseableVersion_IsPassedThroughOrEmpty(string version) {
        var candidates = OnnxModelRegistry.VersionDirCandidates(version);

        if (string.IsNullOrWhiteSpace(version))
            Assert.That(candidates, Is.Empty);
        else
            Assert.That(candidates, Is.EqualTo(new[] { version }));
    }

    /// <summary>The whole point, stated against the layout that actually ships:
    /// every bundled BGE directory has to offer the converted trees' name.</summary>
    [TestCase("graxpert-1.0.1", "1.0.1")]
    [TestCase("graxpert-1.0.1-fp16", "1.0.1")]
    [TestCase("polaris-1.0.0", "1.0.0")]
    [TestCase("polaris-1.0.0-fp16", "1.0.0")]
    [TestCase("polaris-1.0.0-int16", "1.0.0")]
    public void EveryBundledBgeDirectory_ReachesTheConvertedName(string onnxDir, string wanted) {
        Assert.That(OnnxModelRegistry.VersionDirCandidates(onnxDir), Does.Contain(wanted));
    }
}
