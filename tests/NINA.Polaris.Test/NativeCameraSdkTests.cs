using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Smoke tests for the native vendor-SDK camera backends (#362 item 3).
/// Hardware + native libs are absent in CI, so these only assert the
/// managed surface behaves: the availability probes never throw (they
/// swallow DllNotFound and return false), and discovery degrades to an
/// empty list rather than crashing.
/// </summary>
[TestFixture]
public class NativeCameraSdkTests {

    [Test]
    public void SvbonyRegistry_IsAvailable_DoesNotThrow() {
        // No SVBCameraSDK native lib in the test output → must return false,
        // never throw a DllNotFoundException up to the caller.
        Assert.That(NINA.Camera.SvbonySdk.SvbonyRegistry.IsAvailable, Is.False);
    }

    [Test]
    public void ZwoRegistry_IsAvailable_DoesNotThrow() {
        Assert.That(NINA.Camera.ZwoSdk.ZwoRegistry.IsAvailable, Is.False);
    }

    [Test]
    public void SvbonyDiscovery_Enumerate_EmptyWhenUnavailable() {
        Assert.That(NINA.Camera.SvbonySdk.SvbonyDiscovery.Enumerate(), Is.Empty);
    }

    [Test]
    public void ZwoDiscovery_Enumerate_EmptyWhenUnavailable() {
        Assert.That(NINA.Camera.ZwoSdk.ZwoDiscovery.Enumerate(), Is.Empty);
    }

    [Test]
    public void Cameras_ConstructWithoutNativeLib() {
        // Constructors must not touch the native lib (only EnsureResolver,
        // which is guarded). Connect would throw, but construction is safe.
        Assert.That(() => new NINA.Camera.SvbonySdk.SvbonySdkCamera("0"), Throws.Nothing);
        Assert.That(() => new NINA.Camera.ZwoSdk.AsiSdkCamera("0"), Throws.Nothing);
    }
}
