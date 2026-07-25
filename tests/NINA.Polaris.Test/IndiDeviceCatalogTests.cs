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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The USB-to-INDI-driver mapping, pinned against REAL descriptors captured
/// from a live rig (Radxa Dragon Q6A, 2026-07-25) rather than invented ones.
/// Every VID:PID and every manufacturer/product string below was read out of
/// that machine's sysfs, which matters: the interesting cases here are exactly
/// the ones where a plausible-looking assumption is wrong.
/// </summary>
[TestFixture]
public class IndiDeviceCatalogTests {
    private static UsbDeviceInfo Dev(string vid, string pid, string? mfr, string? product)
        => new("test", vid, pid, mfr, product, null, 480);

    /// <summary>ZWO puts cameras, wheels, focusers and rotators behind ONE vid,
    /// so a vid-only rule would send a filter wheel to the camera driver. The
    /// product string is what separates them.</summary>
    [Test]
    public void ZwoCamera_ResolvesToTheCameraDriver() {
        var m = IndiDeviceCatalog.Identify(Dev("03c3", "183e", "ZWO", "ASI183MM Pro"));
        Assert.That(m.Confidence, Is.EqualTo(IndiMatchConfidence.Resolved));
        Assert.That(m.Kind, Is.EqualTo("camera"));
        Assert.That(m.CandidateLabels, Is.EqualTo(new[] { "ZWO CCD" }));
    }

    /// <summary>Note the manufacturer string really is "ZW0" with a ZERO on the
    /// wheel -- a good reason not to key matching off the manufacturer text.</summary>
    [Test]
    public void ZwoFilterWheel_IsNotMistakenForACamera() {
        var m = IndiDeviceCatalog.Identify(Dev("03c3", "1f01", "ZW0", "ZWO EFW"));
        Assert.That(m.Confidence, Is.EqualTo(IndiMatchConfidence.Resolved));
        Assert.That(m.Kind, Is.EqualTo("wheel"));
        Assert.That(m.CandidateLabels, Is.EqualTo(new[] { "ZWO EFW" }));
    }

    [Test]
    public void ZwoFocuserAndRotator_RouteByProductString() {
        Assert.That(IndiDeviceCatalog.Identify(Dev("03c3", "0000", "ZWO", "ZWO EAF")).CandidateLabels,
                    Is.EqualTo(new[] { "ZWO EAF" }));
        Assert.That(IndiDeviceCatalog.Identify(Dev("03c3", "0000", "ZWO", "ZWO CAA")).CandidateLabels,
                    Is.EqualTo(new[] { "ZWO CAA" }));
    }

    /// <summary>0547 is Cypress, a CHIP vendor: a dozen brands resell the same
    /// ToupTek-platform camera under it. Claiming a single driver here would be
    /// wrong roughly eleven times out of twelve, so the catalog must offer a
    /// list and let the operator choose.</summary>
    [Test]
    public void ToupTekPlatform_IsAmbiguousNotResolved() {
        var m = IndiDeviceCatalog.Identify(Dev("0547", "14ff", "TT", "USB2.0 Camera"));
        Assert.That(m.Confidence, Is.EqualTo(IndiMatchConfidence.Ambiguous));
        Assert.That(m.CandidateLabels, Does.Contain("Toupcam"));
        Assert.That(m.CandidateLabels, Does.Contain("Altair"));
        Assert.That(m.CandidateLabels.Count, Is.GreaterThan(1),
            "a single candidate would imply a certainty the USB id cannot support");
    }

    /// <summary>A Cypress chip in something that is not a camera must NOT be
    /// offered camera drivers just because it shares the vid.</summary>
    [Test]
    public void CypressChip_WithoutCameraHints_StaysUnknown() {
        var m = IndiDeviceCatalog.Identify(Dev("0547", "6572", null, "USB2.0 Hub"));
        Assert.That(m.Confidence, Is.EqualTo(IndiMatchConfidence.Unknown));
        Assert.That(m.CandidateLabels, Is.Empty);
    }

    /// <summary>The whole point of the serial-bridge bucket: a CH340 is a wire.
    /// On the reference rig this exact device is a Gemini Focuser Pro, but
    /// nothing in USB says so -- it could equally be a mount. Guessing here
    /// would be worse than admitting ignorance.</summary>
    [Test]
    public void UsbSerialBridge_IsFlaggedUnidentifiable() {
        foreach (var vid in new[] { "1a86", "0403", "10c4", "067b" }) {
            var m = IndiDeviceCatalog.Identify(Dev(vid, "7523", null, "USB Serial"));
            Assert.That(m.Confidence, Is.EqualTo(IndiMatchConfidence.SerialBridge), $"vid {vid}");
            Assert.That(m.CandidateLabels, Is.Empty, $"vid {vid} must not guess a driver");
            Assert.That(m.Note, Is.Not.Null.And.Not.Empty, $"vid {vid} should explain why");
        }
    }

    [Test]
    public void KnownCameraVendors_Resolve() {
        Assert.That(IndiDeviceCatalog.Identify(Dev("f266", "9a0a", "SVBONY", "SV405CC")).CandidateLabels,
                    Is.EqualTo(new[] { "SVBONY CCD" }));
        Assert.That(IndiDeviceCatalog.Identify(Dev("1618", "0921", "QHY", "QHY5")).CandidateLabels,
                    Is.EqualTo(new[] { "QHY CCD" }));
        Assert.That(IndiDeviceCatalog.Identify(Dev("a0a0", "0001", "PlayerOne", "Neptune")).CandidateLabels,
                    Is.EqualTo(new[] { "PlayerOne CCD" }));
    }

    [Test]
    public void UnknownVendor_ReportsUnknownWithoutGuessing() {
        var m = IndiDeviceCatalog.Identify(Dev("dead", "beef", "Nobody", "Mystery Box"));
        Assert.That(m.Confidence, Is.EqualTo(IndiMatchConfidence.Unknown));
        Assert.That(m.CandidateLabels, Is.Empty);
    }
}
