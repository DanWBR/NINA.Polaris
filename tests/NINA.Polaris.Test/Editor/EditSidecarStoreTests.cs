using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.Editor;
using NINA.Polaris.Services.Editor;

namespace NINA.Polaris.Test.Editor;

/// <summary>
/// Pins EditSidecarStore round-trip behaviour. The store is the entire
/// non-destructive contract, if we silently drop edits or write
/// malformed JSON the user loses work on reopening, so these tests are
/// worth keeping tight.
/// </summary>
[TestFixture]
public class EditSidecarStoreTests {
    private string _tempDir = "";

    [SetUp]
    public void SetUp() {
        _tempDir = Path.Combine(Path.GetTempPath(), "polaris-editor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void Save_ThenLoad_RoundtripsEditsExactly() {
        var sourcePath = Path.Combine(_tempDir, "M31_master.fits");
        File.WriteAllBytes(sourcePath, new byte[] { 0 });   // any file will do; just needs to exist
        var store = new EditSidecarStore(NullLogger<EditSidecarStore>.Instance);

        var edits = new EditParams(
            Light: new LightParams(Exposure: 0.25, Contrast: 0.3,
                                   Highlights: -0.4, Shadows: 0.5,
                                   Whites: 0.1, Blacks: -0.05),
            Color: new ColorParams(Vibrance: 0.2, Saturation: 0.1, Hue: 12),
            Effects: new EffectsParams(Clarity: 0.15, VignetteAmount: -0.3));

        var savedPath = store.Save(sourcePath, edits);
        Assert.That(savedPath, Is.Not.Null, "Save should succeed and return a path");
        Assert.That(File.Exists(savedPath!), "Sidecar file should be on disk");

        var loaded = store.Load(sourcePath);
        Assert.That(loaded, Is.Not.Null, "Load should hydrate the saved edits");
        Assert.That(loaded!.Light, Is.Not.Null);
        Assert.That(loaded.Light!.Exposure, Is.EqualTo(0.25).Within(1e-6));
        Assert.That(loaded.Color!.Vibrance, Is.EqualTo(0.2).Within(1e-6));
        Assert.That(loaded.Effects!.VignetteAmount, Is.EqualTo(-0.3).Within(1e-6));
    }

    [Test]
    public void Load_MissingSidecar_ReturnsNull() {
        var store = new EditSidecarStore(NullLogger<EditSidecarStore>.Instance);
        var sourcePath = Path.Combine(_tempDir, "no-such-source.fits");
        File.WriteAllBytes(sourcePath, new byte[] { 0 });
        Assert.That(store.Load(sourcePath), Is.Null);
    }

    [Test]
    public void Load_CorruptedJson_ReturnsNullDoesntThrow() {
        var store = new EditSidecarStore(NullLogger<EditSidecarStore>.Instance);
        var sourcePath = Path.Combine(_tempDir, "bad.fits");
        File.WriteAllBytes(sourcePath, new byte[] { 0 });
        File.WriteAllText(sourcePath + ".edit.json", "{ not json at all !!");

        Assert.DoesNotThrow(() => store.Load(sourcePath));
        Assert.That(store.Load(sourcePath), Is.Null);
    }

    [Test]
    public void Load_UnsupportedVersion_ReturnsNullWithWarning() {
        var store = new EditSidecarStore(NullLogger<EditSidecarStore>.Instance);
        var sourcePath = Path.Combine(_tempDir, "future.fits");
        File.WriteAllBytes(sourcePath, new byte[] { 0 });
        File.WriteAllText(sourcePath + ".edit.json", """
            { "version": 99, "source": "future.fits", "savedAt": "2030-01-01T00:00:00Z", "edits": {} }
            """);
        Assert.That(store.Load(sourcePath), Is.Null,
            "Unknown version should be skipped, not crash");
    }

    [Test]
    public void Save_AtomicWrite_DoesntLeaveTempFile() {
        var sourcePath = Path.Combine(_tempDir, "x.fits");
        File.WriteAllBytes(sourcePath, new byte[] { 0 });
        var store = new EditSidecarStore(NullLogger<EditSidecarStore>.Instance);
        store.Save(sourcePath, EditParams.Defaults);

        var leftovers = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.That(leftovers, Is.Empty, "No .tmp files should remain after a successful save");
    }

    [Test]
    public void Save_OverwritesExistingSidecar() {
        var sourcePath = Path.Combine(_tempDir, "y.fits");
        File.WriteAllBytes(sourcePath, new byte[] { 0 });
        var store = new EditSidecarStore(NullLogger<EditSidecarStore>.Instance);
        // First save: vibrance 0.3
        store.Save(sourcePath, new EditParams(Color: new ColorParams(Vibrance: 0.3)));
        // Second save: vibrance 0.7, should replace, not append.
        store.Save(sourcePath, new EditParams(Color: new ColorParams(Vibrance: 0.7)));

        var loaded = store.Load(sourcePath);
        Assert.That(loaded!.Color!.Vibrance, Is.EqualTo(0.7).Within(1e-6));
    }
}
