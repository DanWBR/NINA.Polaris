using NINA.Image.NativeLibs;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The five camera-SDK resolvers used to list only the Windows .dll and the
/// Linux .so, so on macOS a vendor .dylib next to the app was never matched by
/// name. All three branches must be pickable from any host.
/// </summary>
[TestFixture]
public class NativeSdkCandidatesTests {
    [Test]
    public void Windows_TakesTheDll() {
        Assert.That(NativeSdkProbe.CandidatesFor(true, false, "ASICamera2.dll", "libASICamera2.dylib", "libASICamera2.so"),
            Is.EqualTo(new[] { "ASICamera2.dll" }));
    }

    [Test]
    public void MacOs_TakesTheDylib() {
        Assert.That(NativeSdkProbe.CandidatesFor(false, true, "ASICamera2.dll", "libASICamera2.dylib", "libASICamera2.so"),
            Is.EqualTo(new[] { "libASICamera2.dylib" }));
    }

    [Test]
    public void EverythingElse_TakesTheSharedObjects_InOrder() {
        Assert.That(NativeSdkProbe.CandidatesFor(false, false, "PlayerOneCamera.dll", "libPlayerOneCamera.dylib",
                "libPlayerOneCamera.so", "libPlayerOneCamera.so.3"),
            Is.EqualTo(new[] { "libPlayerOneCamera.so", "libPlayerOneCamera.so.3" }));
    }

    [Test]
    public void RunningHost_PicksExactlyOneBranch() {
        var c = NativeSdkProbe.Candidates("toupcam.dll", "libtoupcam.dylib", "libtoupcam.so");
        Assert.That(c, Is.Not.Empty);
        Assert.That(c[0], Does.StartWith(OperatingSystem.IsWindows() ? "toupcam" : "libtoupcam"));
    }
}
