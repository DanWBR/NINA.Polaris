using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Headless.Services;
using NINA.Headless.Services.External;

namespace NINA.Headless.Test;

/// <summary>
/// Pins the pure / non-process-launching parts of SirilService:
///   - script enumeration + name → path resolution
///   - user vs bundled script source labelling
///   - candidate-binary enumeration (for the Settings diagnostic)
///   - job state machine + cancel flag plumbing
///
/// Tests that actually launch siril-cli would require the binary on
/// disk + a real FITS — out of scope for unit tests; covered by the
/// end-to-end verification section of the plan.
/// </summary>
[TestFixture]
public class SirilServiceTests {

    private string _tmpAppData = null!;
    private ProfileService _profile = null!;
    private SirilService _siril = null!;

    [SetUp]
    public void Setup() {
        // Isolate profile + bundled scripts in a temp AppData so
        // tests don't pollute the developer's real Polaris install.
        _tmpAppData = Path.Combine(Path.GetTempPath(), "siril_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpAppData);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        _profile = new ProfileService(config, NullLogger<ProfileService>.Instance);
        _siril = new SirilService(config, _profile, NullLogger<SirilService>.Instance);
    }

    [TearDown]
    public void Teardown() {
        try { if (Directory.Exists(_tmpAppData)) Directory.Delete(_tmpAppData, recursive: true); } catch { }
    }

    // --- Script enumeration -----------------------------------------

    [Test]
    public void EnumerateBinaryCandidates_AlwaysHasEntries() {
        // Even when Siril isn't installed, the diagnostic must list
        // the places we looked so the Settings UI can show "we tried
        // here and here, none existed" without bombing on an empty
        // list.
        var list = _siril.EnumerateBinaryCandidates();
        Assert.That(list, Is.Not.Null);
        Assert.That(list.Count, Is.GreaterThan(0));
    }

    [Test]
    public void UserScriptDirs_IncludesPlatformDefault() {
        // The per-OS list must include the standard Siril scripts dir
        // for that platform — without it, users who never customised
        // the location would see "0 user scripts" wrongly.
        var dirs = _siril.UserScriptDirs().ToList();
        Assert.That(dirs, Is.Not.Empty);
        // At least one default location for the host OS must be present.
        if (OperatingSystem.IsWindows()) {
            Assert.That(dirs.Any(d => d.Contains("siril", StringComparison.OrdinalIgnoreCase)
                                      && d.Contains("scripts", StringComparison.OrdinalIgnoreCase)), Is.True);
        } else {
            Assert.That(dirs.Any(d => d.EndsWith("siril/scripts", StringComparison.Ordinal)
                                      || d.EndsWith("siril/scripts/", StringComparison.Ordinal)
                                      || d.EndsWith(".siril/scripts", StringComparison.Ordinal)), Is.True);
        }
    }

    [Test]
    public void UserScriptDirs_HonoursConfiguredOverride() {
        // The user can add an extra scripts dir in profile settings;
        // it must show up first in the enumeration so a custom dir
        // wins over the default.
        _profile.Active.SirilScriptsDir = "/custom/path/to/scripts";
        var dirs = _siril.UserScriptDirs().ToList();
        Assert.That(dirs.First(), Is.EqualTo("/custom/path/to/scripts"));
    }

    [Test]
    public void EnumerateScripts_PicksUpBundledFromAssembly() {
        // When the bundled .ssf resources land in the assembly, the
        // enumeration must surface them with Source="bundled". The
        // tests don't assume the user has Siril installed but they
        // DO assume Polaris ships its own scripts — this test would
        // catch a regression where the csproj <EmbeddedResource> entry
        // gets removed and the bundled set silently empties.
        var scripts = _siril.EnumerateScripts();
        Assert.That(scripts, Is.Not.Null);
        var bundled = scripts.Where(s => s.Source == "bundled").ToList();
        Assert.That(bundled.Count, Is.GreaterThanOrEqualTo(9),
            "Expected the 9 bundled preprocessing scripts to be embedded");

        // Spot-check the matrix the user explicitly asked for:
        // each variant (with/without dark/flat/DBF) for both OSC and
        // mono must be present.
        var names = bundled.Select(b => b.Name).ToList();
        foreach (var expected in new[] {
            "OSC_Preprocessing.ssf",
            "OSC_Preprocessing_WithoutDark.ssf",
            "OSC_Preprocessing_WithoutFlat.ssf",
            "OSC_Preprocessing_WithoutDBF.ssf",
            "Mono_Preprocessing.ssf",
            "Mono_Preprocessing_WithoutDark.ssf",
            "Mono_Preprocessing_WithoutFlat.ssf",
            "Mono_Preprocessing_WithoutDBF.ssf",
            "OSC_Extract_HaOIII.ssf"
        }) {
            Assert.That(names, Has.Member(expected),
                $"Bundled script {expected} is missing from the assembly");
        }
    }

    [Test]
    public void BundledScripts_ExtractedToDiskCorrectly() {
        // The extraction path should produce real .ssf files on disk,
        // not empty placeholders. Each script must have at least the
        // `requires` line and end with `close` — otherwise Siril
        // refuses them.
        var scripts = _siril.EnumerateScripts()
            .Where(s => s.Source == "bundled").ToList();
        foreach (var s in scripts) {
            Assert.That(File.Exists(s.Path), Is.True,
                $"Bundled script {s.Name} not extracted to {s.Path}");
            var content = File.ReadAllText(s.Path);
            Assert.That(content, Does.Contain("requires"),
                $"{s.Name} missing `requires` directive");
            Assert.That(content.TrimEnd().EndsWith("close", StringComparison.Ordinal),
                $"{s.Name} should end with `close` to release Siril resources");
        }
    }

    [Test]
    public void EnumerateScripts_UserScriptsOverrideBundledByName() {
        // The override behaviour is critical: a power user who edited
        // OSC_Preprocessing.ssf and put it in their personal scripts
        // dir must see their copy, not Polaris's stock copy. Test
        // by pointing the configured scripts dir at a temp folder
        // containing a known name.
        var userScriptsDir = Path.Combine(_tmpAppData, "user_scripts");
        Directory.CreateDirectory(userScriptsDir);
        var userScript = Path.Combine(userScriptsDir, "Mono_Preprocessing.ssf");
        File.WriteAllText(userScript, "// user-customised");
        _profile.Active.SirilScriptsDir = userScriptsDir;

        var scripts = _siril.EnumerateScripts();
        var found = scripts.FirstOrDefault(s =>
            string.Equals(s.Name, "Mono_Preprocessing.ssf", StringComparison.OrdinalIgnoreCase));

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Source, Is.EqualTo("user"));
        Assert.That(found.Path, Is.EqualTo(userScript));
    }

    [Test]
    public void ResolveScriptPath_AbsolutePath_ReturnsAsIsWhenExists() {
        // Passing a full path bypasses the bundled/user lookup —
        // useful for advanced users with scripts outside any standard
        // location.
        var p = Path.Combine(_tmpAppData, "ad-hoc.ssf");
        File.WriteAllText(p, "// ad hoc");
        var resolved = _siril.ResolveScriptPath(p);
        Assert.That(resolved, Is.EqualTo(p));
    }

    [Test]
    public void ResolveScriptPath_NonexistentName_ReturnsNull() {
        var resolved = _siril.ResolveScriptPath("DoesNotExist.ssf");
        Assert.That(resolved, Is.Null);
    }

    [Test]
    public void ResolveScriptPath_EmptyOrWhitespace_ReturnsNull() {
        // Defensive — UI might send "" when the user hasn't picked
        // anything yet. Resolve must not throw or return a default.
        Assert.That(_siril.ResolveScriptPath(""), Is.Null);
        Assert.That(_siril.ResolveScriptPath("   "), Is.Null);
    }

    // --- Job state --------------------------------------------------

    [Test]
    public void StartJob_WhenNotAvailable_Throws() {
        // Without Siril installed, the API must fail loudly instead
        // of silently producing a zombie job. The Settings UI uses
        // IsAvailable to gate the button — but defence in depth.
        if (_siril.IsAvailable) Assert.Ignore("Siril is installed on this host; cannot test the unavailable path");
        Assert.That(
            () => _siril.StartJob(new SirilJobRequest(
                "OSC_Preprocessing.ssf", "M81", new List<string>())),
            Throws.InvalidOperationException);
    }

    [Test]
    public void CancelJob_UnknownId_ReturnsFalse() {
        // Idempotent cancellation: cancelling a job that never
        // existed (or already completed) is a no-op, not an error.
        var ok = _siril.CancelJob("nonexistent");
        Assert.That(ok, Is.False);
    }

    [Test]
    public void ActiveJobs_StartsEmpty() {
        Assert.That(_siril.ActiveJobs, Is.Empty);
    }

    [Test]
    public void GetJob_UnknownId_ReturnsNull() {
        Assert.That(_siril.GetJob("nope"), Is.Null);
    }
}
