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
    public void PlayerOneRegistry_IsAvailable_DoesNotThrow() {
        Assert.That(NINA.Camera.PlayerOneSdk.PlayerOneRegistry.IsAvailable, Is.False);
    }

    [Test]
    public void ToupTekRegistry_IsAvailable_DoesNotThrow() {
        Assert.That(NINA.Camera.ToupTekSdk.ToupTekRegistry.IsAvailable, Is.False);
    }

    [Test]
    public void PlayerOneDiscovery_Enumerate_EmptyWhenUnavailable() {
        Assert.That(NINA.Camera.PlayerOneSdk.PlayerOneDiscovery.Enumerate(), Is.Empty);
    }

    [Test]
    public void ToupTekDiscovery_Enumerate_EmptyWhenUnavailable() {
        Assert.That(NINA.Camera.ToupTekSdk.ToupTekDiscovery.Enumerate(), Is.Empty);
    }

    [Test]
    public void Cameras_ConstructWithoutNativeLib() {
        // Constructors must not touch the native lib (only EnsureResolver,
        // which is guarded). Connect would throw, but construction is safe.
        Assert.That(() => new NINA.Camera.SvbonySdk.SvbonySdkCamera("0"), Throws.Nothing);
        Assert.That(() => new NINA.Camera.ZwoSdk.AsiSdkCamera("0"), Throws.Nothing);
        Assert.That(() => new NINA.Camera.PlayerOneSdk.PlayerOneSdkCamera("0"), Throws.Nothing);
        Assert.That(() => new NINA.Camera.ToupTekSdk.ToupTekSdkCamera("dev0"), Throws.Nothing);
    }
}