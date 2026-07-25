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

namespace NINA.Polaris.Services;

/// <summary>How sure we are about a device, which the UI renders differently.</summary>
public static class IndiMatchConfidence {
    /// <summary>One driver, high confidence. Safe to pre-tick.</summary>
    public const string Resolved = "resolved";
    /// <summary>We know the hardware family but several drivers serve it
    /// (the OEM-rebadge case). The operator must choose.</summary>
    public const string Ambiguous = "ambiguous";
    /// <summary>A USB-serial bridge. The VID:PID identifies the CONVERTER
    /// CHIP, so the device behind it is unknowable from USB alone.</summary>
    public const string SerialBridge = "serial-bridge";
    /// <summary>Not in the catalog.</summary>
    public const string Unknown = "unknown";
}

public sealed record IndiDeviceMatch(
    string Confidence,
    string? Kind,
    IReadOnlyList<string> CandidateLabels,
    string? Note);

/// <summary>
/// Maps USB identity to candidate INDI driver labels.
///
/// <para>Kept as code rather than a shipped JSON on purpose: a new runtime data
/// file has to be copied by the csproj AND packaged by the installer to work in
/// a real install (see the Native Runtime Assets rule in AGENTS.md), and this
/// table is small and changes about as often as we cut a release anyway.</para>
///
/// <para><b>Labels here are indi-web driver LABELS</b> (the "label" field of
/// <c>GET /api/drivers</c>), not binaries. Callers must intersect them with the
/// drivers actually installed on the host, so proposing a label that a given
/// machine lacks is harmless -- it simply drops out.</para>
///
/// <para>What this can and cannot do, measured on real hardware:
/// <list type="bullet">
/// <item>Vendor-specific cameras and accessories resolve cleanly.</item>
/// <item>The ToupTek/Cypress platform is sold under a dozen brands that all
/// share VID 0547, so it can only narrow to a candidate list.</item>
/// <item>Mounts and most focusers sit behind generic USB-serial bridges and
/// are NOT identifiable. That is a property of the hardware, not a gap in this
/// table -- do not "fix" it by guessing.</item>
/// </list></para>
/// </summary>
public static class IndiDeviceCatalog {
    /// <summary>USB-serial bridge chips. A device with one of these VIDs is a
    /// wire, not an instrument: the mount, focuser, flat panel or dew
    /// controller behind it is indistinguishable over USB.</summary>
    private static readonly Dictionary<string, string> SerialBridges = new(StringComparer.OrdinalIgnoreCase) {
        ["1a86"] = "CH340/CH341",
        ["0403"] = "FTDI",
        ["10c4"] = "Silicon Labs CP210x",
        ["067b"] = "Prolific PL2303",
        // Arduino boards present as CDC-ACM serial. Plenty of DIY focusers and
        // dew controllers are Arduino-based (MyFocuserPro2 among them), and the
        // sketch running on the board is invisible from USB, so they belong in
        // the "you tell us" bucket rather than being guessed at.
        ["2341"] = "Arduino",
    };

    /// <summary>Brands that resell the ToupTek/Cypress camera platform. They
    /// share VID 0547, so USB cannot tell them apart; the operator picks.
    /// Ordered with the most common first.</summary>
    private static readonly string[] ToupTekFamily = {
        "Toupcam", "Altair", "OmegonPro", "Bresser", "Mallincam",
        "StarShootG", "Ogmacam", "Nncam", "Tscam", "Meadecam", "SVbonycam",
    };

    public static IndiDeviceMatch Identify(UsbDeviceInfo d) {
        var vid = (d.VendorId ?? "").ToLowerInvariant();
        var product = d.Product ?? "";
        var manufacturer = d.Manufacturer ?? "";

        if (SerialBridges.TryGetValue(vid, out var bridge) && !string.IsNullOrEmpty(bridge)) {
            return new IndiDeviceMatch(
                IndiMatchConfidence.SerialBridge, null, Array.Empty<string>(),
                $"{bridge} USB-serial adapter. The device behind it (mount, focuser, " +
                "flat panel) cannot be identified from USB, so pick its driver yourself.");
        }

        switch (vid) {
            // ZWO. One VID covers cameras, wheels, focusers and rotators, so the
            // PID alone would mis-route an EFW to the camera driver. ZWO sets a
            // descriptive iProduct on every device ("ZWO EFW", "ASI183MM Pro"),
            // which is both more readable and more future-proof than tracking a
            // PID per model.
            case "03c3":
                if (Contains(product, "EFW"))
                    return Resolved("ZWO EFW", "wheel");
                if (Contains(product, "EAF"))
                    return Resolved("ZWO EAF", "focuser");
                if (Contains(product, "CAA"))
                    return Resolved("ZWO CAA", "rotator");
                return Resolved("ZWO CCD", "camera");

            case "f266":
                return Resolved("SVBONY CCD", "camera");

            case "1618":
                return Resolved("QHY CCD", "camera");

            case "a0a0":
                return Resolved("PlayerOne CCD", "camera");

            case "04a9":
                return Resolved("Canon DSLR", "camera");

            case "04b0":
                return Resolved("Nikon DSLR", "camera");

            // Cypress. NOT a camera vendor -- it sells the controller chip, and
            // the ToupTek-platform cameras (plus every brand that rebadges them)
            // ship with the vendor default. Anything else using a Cypress chip
            // would land here too, so only claim a camera when a descriptor
            // string actually looks like one.
            case "0547":
                if (Contains(manufacturer, "TT") || Contains(product, "Camera")
                    || Contains(manufacturer, "Altair") || Contains(manufacturer, "Toup")) {
                    return new IndiDeviceMatch(
                        IndiMatchConfidence.Ambiguous, "camera", ToupTekFamily,
                        "ToupTek-platform camera. A dozen brands resell this hardware " +
                        "under the same USB id, so pick the one matching your camera.");
                }
                break;
        }

        return new IndiDeviceMatch(
            IndiMatchConfidence.Unknown, null, Array.Empty<string>(), null);
    }

    private static IndiDeviceMatch Resolved(string label, string kind)
        => new(IndiMatchConfidence.Resolved, kind, new[] { label }, null);

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
