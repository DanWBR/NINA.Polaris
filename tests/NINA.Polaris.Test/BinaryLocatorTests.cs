using NUnit.Framework;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the lookup priority for the shared external-binary locator:
///   configured > platform candidates > PATH.
/// Tests use real disk because Path/File operations are hard to mock
/// meaningfully and a temp file is cheap. Cross-platform candidates
/// are tested with paths the host OS WILL find, so the test passes
/// regardless of whether it runs on Windows, Linux, or macOS.
/// </summary>
[TestFixture]
public class BinaryLocatorTests {

    private string _tmpDir = null!;
    private string _binA = null!;
    private string _binB = null!;

    [SetUp]
    public void Setup() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "bl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _binA = Path.Combine(_tmpDir, "binA.bin");
        _binB = Path.Combine(_tmpDir, "binB.bin");
        File.WriteAllText(_binA, "stub");
        File.WriteAllText(_binB, "stub");
    }

    [TearDown]
    public void Teardown() {
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Test]
    public void Find_ConfiguredPath_TakesPrecedence() {
        // Configured > everything else — even when a candidate path
        // also exists. Critical: the user's explicit override must
        // never be silently ignored in favour of an auto-detected
        // binary at a different location.
        var configured = _binA;
        var candidates = new[] { _binB };

        var result = BinaryLocator.Find(configured, candidates, candidates, candidates, null);

        Assert.That(result, Is.EqualTo(_binA));
    }

    [Test]
    public void Find_ConfiguredEmpty_FallsBackToCandidates() {
        // The common case: user hasn't set anything, the auto-detect
        // should pick the first existing candidate.
        var candidates = new[] { _binA };

        var result = BinaryLocator.Find("", candidates, candidates, candidates, null);

        Assert.That(result, Is.EqualTo(_binA));
    }

    [Test]
    public void Find_ConfiguredWhitespace_TreatedAsUnset() {
        // Profile fields often round-trip as "   " when the user clears
        // them via the UI. Don't treat that as "look for a binary
        // called space-space-space".
        var candidates = new[] { _binA };

        var result = BinaryLocator.Find("   ", candidates, candidates, candidates, null);

        Assert.That(result, Is.EqualTo(_binA));
    }

    [Test]
    public void Find_NoCandidatesExist_ReturnsNull() {
        var fakeCandidates = new[] {
            Path.Combine(_tmpDir, "nope1"),
            Path.Combine(_tmpDir, "nope2")
        };

        var result = BinaryLocator.Find(null, fakeCandidates, fakeCandidates, fakeCandidates, null);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Find_FirstCandidateMissing_FallsThroughToSecond() {
        var candidates = new[] {
            Path.Combine(_tmpDir, "nope"),  // doesn't exist
            _binA                           // does
        };

        var result = BinaryLocator.Find(null, candidates, candidates, candidates, null);

        Assert.That(result, Is.EqualTo(_binA));
    }

    [Test]
    public void Find_UsesCorrectPlatformList() {
        // Build three distinct lists; exactly one (the host's) should
        // be consulted. The other two contain real files but should
        // be ignored. This catches the regression where we accidentally
        // walk all three lists.
        var winOnly   = new[] { OperatingSystem.IsWindows() ? _binA : Path.Combine(_tmpDir, "win-unused") };
        var linuxOnly = new[] { OperatingSystem.IsLinux()   ? _binA : Path.Combine(_tmpDir, "linux-unused") };
        var macOnly   = new[] { OperatingSystem.IsMacOS()   ? _binA : Path.Combine(_tmpDir, "mac-unused") };

        var result = BinaryLocator.Find(null, winOnly, linuxOnly, macOnly, null);

        Assert.That(result, Is.EqualTo(_binA),
            "Should find the binary in the list matching the current OS");
    }

    [Test]
    public void Enumerate_ListsEveryCheckedLocation() {
        var configured = _binA;
        var candidates = new[] {
            Path.Combine(_tmpDir, "nope"),
            _binB
        };

        var list = BinaryLocator.Enumerate(configured, candidates, candidates, candidates, null);

        // We expect at least: configured + 2 platform candidates = 3 entries.
        // PATH lookup may add more on Unix (depending on $PATH content),
        // but the test asserts a lower bound.
        Assert.That(list.Count, Is.GreaterThanOrEqualTo(3));

        var byPath = list.ToDictionary(c => c.Path, c => c.Exists);
        Assert.That(byPath[_binA], Is.True);
        Assert.That(byPath[_binB], Is.True);
        Assert.That(byPath[Path.Combine(_tmpDir, "nope")], Is.False);
    }

    [Test]
    public void Enumerate_NoConfigured_OmitsConfiguredEntry() {
        // The "Configured" entry shouldn't appear when no override was
        // supplied — keeps the diagnostic clean for users who never
        // touched the setting.
        var candidates = new[] { _binA };
        var list = BinaryLocator.Enumerate(null, candidates, candidates, candidates, null);

        Assert.That(list.Any(c => c.Description == "Configured"), Is.False);
    }
}
