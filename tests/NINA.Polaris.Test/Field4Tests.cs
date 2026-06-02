using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Enum;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// FIELD4-2 + FIELD4-3 coverage. Focused unit tests for the Bayer
/// row-shift remap and the per-camera-id quirks migration. The
/// integration paths (full RelayImageAsync round-trip, REST
/// endpoint round-trip) are exercised by manual / on-hardware
/// verification; these tests pin the pure-function pieces so a
/// regression breaks CI rather than waiting for the next varanda
/// session.
/// </summary>
[TestFixture]
public class Field4RowShiftBayerTests {
    [Test]
    public void RowShift_RGGB_BecomesGBRG() {
        // FIELD4-2: a top-down row flip moves the original row-1
        // (G/B in RGGB) to row-0, so the new top-left 2x2 cell is
        // G B / R G — i.e. GBRG. Symmetric pair.
        Assert.That(ImageRelayService.RowShiftBayer(BayerPatternEnum.RGGB),
            Is.EqualTo(BayerPatternEnum.GBRG));
        Assert.That(ImageRelayService.RowShiftBayer(BayerPatternEnum.GBRG),
            Is.EqualTo(BayerPatternEnum.RGGB));
    }

    [Test]
    public void RowShift_BGGR_BecomesGRBG() {
        Assert.That(ImageRelayService.RowShiftBayer(BayerPatternEnum.BGGR),
            Is.EqualTo(BayerPatternEnum.GRBG));
        Assert.That(ImageRelayService.RowShiftBayer(BayerPatternEnum.GRBG),
            Is.EqualTo(BayerPatternEnum.BGGR));
    }

    [Test]
    public void RowShift_NonBayerValues_PassThrough() {
        // None / Auto don't get remapped; the shader treats both
        // as "no debayer", and flipping doesn't change that.
        Assert.That(ImageRelayService.RowShiftBayer(BayerPatternEnum.None),
            Is.EqualTo(BayerPatternEnum.None));
        Assert.That(ImageRelayService.RowShiftBayer(BayerPatternEnum.Auto),
            Is.EqualTo(BayerPatternEnum.Auto));
    }
}

/// <summary>
/// FIELD4-3: per-camera-id quirks migration from the legacy
/// per-rig BayerPatternOverride / VerticalFlipImage fields.
/// </summary>
[TestFixture]
public class Field4CameraQuirksMigrationTests {
    [Test]
    public void MigratesLegacyPerRigOverridesIntoPerCameraMap() {
        // Arrange: an isolated profile dir so we don't pollute the
        // operator's real profile under LocalAppData. Pre-seed an
        // active.json that has a rig with the legacy fields set
        // and no CameraQuirks entry for that camera.
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"polaris-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var profileJson = @"{
                ""name"": ""Default"",
                ""activeEquipmentProfileId"": ""rig-1"",
                ""equipmentProfiles"": [{
                    ""id"": ""rig-1"",
                    ""name"": ""SV405CC rig"",
                    ""camera"": ""SVBONY SV405CC"",
                    ""bayerPatternOverride"": ""GRBG"",
                    ""verticalFlipImage"": true
                }],
                ""cameraQuirks"": {}
            }";
            File.WriteAllText(Path.Combine(tempDir, "active.json"), profileJson);

            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Profiles:Directory"] = tempDir
                })
                .Build();

            // Act: load triggers MigrateLegacyCameraQuirks on the
            // ProfileService ctor path.
            var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);

            // Assert: the legacy values are now under the per-camera
            // map keyed by the rig's camera id.
            var quirks = profiles.GetActiveCameraQuirks();
            Assert.That(quirks.BayerPatternOverride, Is.EqualTo("GRBG"));
            Assert.That(quirks.VerticalFlipImage, Is.True);
        } finally {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Test]
    public void DoesNotOverwriteExistingPerCameraEntry() {
        // Arrange: same legacy rig, BUT a per-camera entry already
        // exists for the same camera id (operator already edited
        // it directly). Migration must NOT clobber the explicit
        // entry with stale legacy values.
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"polaris-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var profileJson = @"{
                ""name"": ""Default"",
                ""activeEquipmentProfileId"": ""rig-1"",
                ""equipmentProfiles"": [{
                    ""id"": ""rig-1"",
                    ""name"": ""SV405CC rig"",
                    ""camera"": ""SVBONY SV405CC"",
                    ""bayerPatternOverride"": ""GRBG"",
                    ""verticalFlipImage"": true
                }],
                ""cameraQuirks"": {
                    ""SVBONY SV405CC"": {
                        ""bayerPatternOverride"": ""RGGB"",
                        ""verticalFlipImage"": false
                    }
                }
            }";
            File.WriteAllText(Path.Combine(tempDir, "active.json"), profileJson);

            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Profiles:Directory"] = tempDir
                })
                .Build();

            var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);

            // Per-camera entry wins — the explicit RGGB / flip=false
            // survives, the legacy GRBG / flip=true on the rig is
            // ignored.
            var quirks = profiles.GetActiveCameraQuirks();
            Assert.That(quirks.BayerPatternOverride, Is.EqualTo("RGGB"));
            Assert.That(quirks.VerticalFlipImage, Is.False);
        } finally {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Test]
    public void GetActiveCameraQuirks_NoCameraSelected_ReturnsEmpty() {
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"polaris-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Profiles:Directory"] = tempDir
                })
                .Build();
            var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
            // Default rig is auto-created, no camera selected.
            var quirks = profiles.GetActiveCameraQuirks();
            Assert.That(quirks.BayerPatternOverride, Is.Null);
            Assert.That(quirks.VerticalFlipImage, Is.False);
        } finally {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
