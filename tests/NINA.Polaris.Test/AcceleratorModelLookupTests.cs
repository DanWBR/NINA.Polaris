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
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Ncnn;
using NINA.Polaris.Services.Onnx;
using NINA.Polaris.Services.Qnn;
using NINA.Polaris.Services.Rknn;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The accelerator model lookup, against a tree laid out the way the shipped one
/// is. This is the test that would have caught it.
///
/// The bundled background-extraction models sit in
/// <c>bge-ai-models/graxpert-1.0.1</c>, with a source prefix, and their
/// converted siblings in <c>rknn/bge-ai-models/1.0.1</c> and
/// <c>ncnn/bge-ai-models/1.0.1</c>, without one. All three resolvers used the
/// ONNX directory name verbatim for the converted subtree, so background
/// extraction missed on every accelerator and GraXpertService fell through to
/// the Python CLI: seconds per frame against about ninety milliseconds on an
/// NPU, which is why per-frame background extraction in live stacking was
/// unusable. Denoise matched by luck (<c>2.0.0</c> on both sides) and hid it.
///
/// The string-level candidate order is pinned separately in
/// <see cref="OnnxVersionDirCandidateTests"/>. These go through the real
/// resolvers and the real filesystem, because the previous tests of the string
/// grammar would all have passed while the lookup was broken.
/// </summary>
[TestFixture]
public class AcceleratorModelLookupTests {

    private string _root = "";
    private ProfileService _profile = null!;
    private IWebHostEnvironment _env = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), "polaris-accel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var cfg = new ConfigurationBuilder().Build();
        _profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        _profile.Active.OnnxModelsPath = _root;
        _env = new StubEnv(_root);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Lay down the directories and names the product actually
    /// ships.</summary>
    private void Shipped() {
        // ONNX side: prefixed, as scripts/quantize_onnx_models.py leaves them.
        Onnx("bge-ai-models", "graxpert-1.0.1");
        Onnx("bge-ai-models", "graxpert-1.0.1-fp16");
        Onnx("denoise-ai-models", "2.0.0");
        // Converted side: unprefixed for bge, matching for denoise.
        Converted("rknn", "bge-ai-models", "1.0.1", "model.rknn");
        Converted("rknn", "denoise-ai-models", "2.0.0", "model.rknn");
        Converted("ncnn", "bge-ai-models", "1.0.1", "model.ncnn.param");
        Converted("ncnn", "bge-ai-models", "1.0.1", "model.ncnn.bin");
        Converted("qnn", "bge-ai-models", "1.0.1", "bge_v68_int16.bin");
    }

    private void Onnx(string familyDir, string version) {
        var dir = Path.Combine(_root, familyDir, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "not a real model");
    }

    private void Converted(string accel, string familyDir, string version, string file) {
        var dir = Path.Combine(_root, accel, familyDir, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), "not a real model");
    }

    private OnnxModelRegistry Registry() {
        var r = new OnnxModelRegistry(_profile, _env, NullLogger<OnnxModelRegistry>.Instance);
        r.RescanAsync().GetAwaiter().GetResult();
        return r;
    }

    /// <summary>The bug, on the RKNN lane.</summary>
    [Test]
    public void Rknn_FindsTheUnprefixedBgeModel() {
        Shipped();
        var svc = new RknnInferenceService(Registry(), NullLogger<RknnInferenceService>.Instance);

        var resolved = svc.ResolveModel("bge", "graxpert-1.0.1");

        Assert.That(resolved, Is.Not.Null,
            "background extraction fell through to the GraXpert CLI because of this");
        Assert.That(resolved!.Value.rknnPath,
            Is.EqualTo(Path.Combine(_root, "rknn", "bge-ai-models", "1.0.1", "model.rknn")));
    }

    /// <summary>The same on the ncnn lane, which is the only host lane an
    /// Allwinner board has.</summary>
    [Test]
    public void Ncnn_FindsTheUnprefixedBgeModel() {
        Shipped();
        var svc = new NcnnInferenceService(Registry(), NullLogger<NcnnInferenceService>.Instance);

        var resolved = svc.ResolveModel("bge", "graxpert-1.0.1");

        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!.Value.paramPath,
            Is.EqualTo(Path.Combine(_root, "ncnn", "bge-ai-models", "1.0.1", "model.ncnn.param")));
    }

    /// <summary>And on QNN, which resolves a directory rather than a file.</summary>
    [Test]
    public void Qnn_FindsTheUnprefixedBgeModel() {
        Shipped();
        var svc = new QnnInferenceService(Registry(), NullLogger<QnnInferenceService>.Instance);

        var resolved = svc.ResolveModel("bge", "graxpert-1.0.1");

        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!.Value.binPath, Does.Contain(
            Path.Combine("qnn", "bge-ai-models", "1.0.1")));
    }

    /// <summary>A quantised ONNX directory resolves to the accelerator's plain
    /// one: the converted model carries its own precision.</summary>
    [Test]
    public void AQuantisedOnnxDirectory_StillResolves() {
        Shipped();
        var svc = new RknnInferenceService(Registry(), NullLogger<RknnInferenceService>.Instance);

        Assert.That(svc.ResolveModel("bge", "graxpert-1.0.1-fp16"), Is.Not.Null);
    }

    /// <summary>Denoise matched before this change and must still match, by the
    /// exact-name candidate rather than by a fallback.</summary>
    [Test]
    public void Denoise_StillResolvesExactly() {
        Shipped();
        var svc = new RknnInferenceService(Registry(), NullLogger<RknnInferenceService>.Instance);

        var resolved = svc.ResolveModel("denoise", "2.0.0");

        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!.Value.rknnPath,
            Is.EqualTo(Path.Combine(_root, "rknn", "denoise-ai-models", "2.0.0", "model.rknn")));
    }

    /// <summary>An exact prefixed match wins over the stripped candidate, so a
    /// converted tree that does use the prefix is not hijacked. The shipped
    /// <c>qnn/bge-ai-models/polaris-1.0.0</c> is exactly this case.</summary>
    [Test]
    public void AnExactPrefixedMatch_Wins() {
        Onnx("bge-ai-models", "polaris-1.0.0");
        Converted("rknn", "bge-ai-models", "polaris-1.0.0", "model.rknn");
        Converted("rknn", "bge-ai-models", "1.0.0", "model.rknn");
        var svc = new RknnInferenceService(Registry(), NullLogger<RknnInferenceService>.Instance);

        var resolved = svc.ResolveModel("bge", "polaris-1.0.0");

        Assert.That(resolved!.Value.rknnPath,
            Is.EqualTo(Path.Combine(_root, "rknn", "bge-ai-models", "polaris-1.0.0", "model.rknn")),
            "the prefixed directory is the more specific match");
    }

    /// <summary>No converted model at all still means no, rather than a path
    /// that does not exist.</summary>
    [Test]
    public void NoConvertedModel_ResolvesToNull() {
        Onnx("bge-ai-models", "graxpert-1.0.1");
        var svc = new RknnInferenceService(Registry(), NullLogger<RknnInferenceService>.Instance);

        Assert.That(svc.ResolveModel("bge", "graxpert-1.0.1"), Is.Null);
    }

    private sealed class StubEnv : IWebHostEnvironment {
        public StubEnv(string root) {
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new NullFileProvider();
            WebRootFileProvider = new NullFileProvider();
        }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "NINA.Polaris.Test";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}
