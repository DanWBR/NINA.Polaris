using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Onnx;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins OnnxModelRegistry's walk/parse + lazy-hash contract. The
/// registry is the only place the server "knows" what models exist;
/// any silent regression in path parsing here would surface as the
/// browser side showing zero models with no error in either log.
/// </summary>
[TestFixture]
public class OnnxModelRegistryTests {
    private string _tempRoot = "";
    private ProfileService _profile = null!;

    [SetUp]
    public void SetUp() {
        _tempRoot = Path.Combine(Path.GetTempPath(),
            "polaris-onnx-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // Spin up a minimal ProfileService pointing OnnxModelsPath at
        // the temp dir. ProfileService stores under {AppData}/Polaris so
        // we get a real instance; the OnnxModelsPath write is what we
        // actually exercise here.
        var cfg = new ConfigurationBuilder().Build();
        _profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        _profile.Active.OnnxModelsPath = _tempRoot;
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tempRoot)) {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { /* ignore — Windows sometimes holds locks briefly */ }
        }
    }

    [Test]
    public async Task Rescan_EmptyDir_RegistryIsEmpty() {
        var reg = new OnnxModelRegistry(_profile, NullLogger<OnnxModelRegistry>.Instance);
        await reg.RescanAsync();
        Assert.That(reg.All(), Is.Empty);
    }

    [Test]
    public async Task Rescan_PathNotSet_RegistryIsEmpty() {
        _profile.Active.OnnxModelsPath = "";
        var reg = new OnnxModelRegistry(_profile, NullLogger<OnnxModelRegistry>.Instance);
        await reg.RescanAsync();
        Assert.That(reg.All(), Is.Empty);
    }

    [Test]
    public async Task Rescan_GraXpertLayout_DiscoversAllFamilies() {
        // Mimic the GraXpert install layout for all 5 model families.
        SeedFake("bge-ai-models",                 "1.0.1");
        SeedFake("denoise-ai-models",             "2.0.0");
        SeedFake("denoise-ai-models",             "3.0.2");
        SeedFake("deconvolution-stars-ai-models", "1.0.0");
        SeedFake("deconvolution-object-ai-models", "1.0.1");

        var reg = new OnnxModelRegistry(_profile, NullLogger<OnnxModelRegistry>.Instance);
        await reg.RescanAsync();

        var all = reg.All();
        Assert.That(all.Count, Is.EqualTo(5));
        // Family aliases mapped to canonical short ids
        Assert.That(reg.Find("bge", "1.0.1"), Is.Not.Null);
        Assert.That(reg.Find("denoise", "2.0.0"), Is.Not.Null);
        Assert.That(reg.Find("denoise", "3.0.2"), Is.Not.Null);
        Assert.That(reg.Find("decon-stars", "1.0.0"), Is.Not.Null);
        Assert.That(reg.Find("decon-objects", "1.0.1"), Is.Not.Null);
    }

    [Test]
    public async Task Rescan_UnknownFamilyDir_Ignored() {
        // A foreign ONNX file in a non-matching layout should be skipped
        // silently — the GraXpert layout match is intentional, drops a
        // .onnx the user happens to dump in the same root.
        SeedFakeAtPath(Path.Combine(_tempRoot, "my-custom", "model.onnx"));
        var reg = new OnnxModelRegistry(_profile, NullLogger<OnnxModelRegistry>.Instance);
        await reg.RescanAsync();
        Assert.That(reg.All(), Is.Empty);
    }

    [Test]
    public async Task Rescan_NonVersionedDir_Ignored() {
        // A `bge-ai-models/latest/model.onnx` should be skipped — the
        // parser is strict about semver-ish version dirs.
        SeedFakeAtPath(Path.Combine(_tempRoot, "bge-ai-models", "latest", "model.onnx"));
        var reg = new OnnxModelRegistry(_profile, NullLogger<OnnxModelRegistry>.Instance);
        await reg.RescanAsync();
        Assert.That(reg.All(), Is.Empty);
    }

    [Test]
    public async Task Rescan_RemovedFile_DropsEntry() {
        SeedFake("bge-ai-models", "1.0.1");
        var reg = new OnnxModelRegistry(_profile, NullLogger<OnnxModelRegistry>.Instance);
        await reg.RescanAsync();
        Assert.That(reg.All().Count, Is.EqualTo(1));

        // Delete the file + rescan — entry should vanish.
        var entry = reg.Find("bge", "1.0.1")!;
        File.Delete(entry.Path);
        await reg.RescanAsync();
        Assert.That(reg.Find("bge", "1.0.1"), Is.Null);
    }

    [Test]
    public async Task GetHash_ComputesAndCaches() {
        SeedFake("bge-ai-models", "1.0.1", bytes: new byte[] { 1, 2, 3, 4, 5 });
        var reg = new OnnxModelRegistry(_profile, NullLogger<OnnxModelRegistry>.Instance);
        await reg.RescanAsync();

        var h1 = await reg.GetHashAsync("bge", "1.0.1");
        var h2 = await reg.GetHashAsync("bge", "1.0.1");
        Assert.That(h1, Is.Not.Null);
        Assert.That(h1!.Length, Is.EqualTo(64), "SHA-256 hex = 64 chars");
        Assert.That(h2, Is.EqualTo(h1), "Second call hits the cache, returns same hex");
    }

    [Test]
    public async Task GetHash_UnknownModel_ReturnsNull() {
        var reg = new OnnxModelRegistry(_profile, NullLogger<OnnxModelRegistry>.Instance);
        await reg.RescanAsync();
        Assert.That(await reg.GetHashAsync("nope", "9.9.9"), Is.Null);
    }

    // ─── helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Create an empty fake model.onnx at the GraXpert layout location
    /// {family}/{version}/model.onnx under the temp root. The contents
    /// don't matter for layout/hash tests; ORT Web is what cares about
    /// actual model bytes and that's exercised by the parity test, not
    /// here.
    /// </summary>
    private void SeedFake(string family, string version, byte[]? bytes = null) {
        var dir = Path.Combine(_tempRoot, family, version);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), bytes ?? Array.Empty<byte>());
    }

    private static void SeedFakeAtPath(string fullPath) {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, Array.Empty<byte>());
    }
}
