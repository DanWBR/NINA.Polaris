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

/// <summary>Decodes the INDI <c>DRIVER_INFO/DRIVER_INTERFACE</c> bitmask into
/// role slugs the UI can group devices by. Bit values are the
/// <c>*_INTERFACE</c> constants from INDI's <c>basedevice.h</c>; drivers
/// publish the mask as a decimal string.</summary>
public static class IndiInterfaceRoles {

    // (bit, slug) in basedevice.h order. "mount" and "filterwheel" are the
    // slugs the rest of the app already uses for those roles.
    private static readonly (int Bit, string Role)[] Bits = [
        (0x0001, "mount"),        // TELESCOPE_INTERFACE
        (0x0002, "camera"),       // CCD_INTERFACE
        (0x0004, "guider"),       // GUIDER_INTERFACE (ST4 pulse output)
        (0x0008, "focuser"),      // FOCUSER_INTERFACE
        (0x0010, "filterwheel"),  // FILTER_INTERFACE
        (0x0020, "dome"),         // DOME_INTERFACE
        (0x0040, "gps"),          // GPS_INTERFACE
        (0x0080, "weather"),      // WEATHER_INTERFACE
        (0x0100, "ao"),           // AO_INTERFACE
        (0x0200, "dustcap"),      // DUSTCAP_INTERFACE
        (0x0400, "lightbox"),     // LIGHTBOX_INTERFACE
        (0x0800, "detector"),     // DETECTOR_INTERFACE
        (0x1000, "rotator"),      // ROTATOR_INTERFACE
        (0x2000, "spectrograph"), // SPECTROGRAPH_INTERFACE
        (0x4000, "correlator"),   // CORRELATOR_INTERFACE
        (0x8000, "aux"),          // AUX_INTERFACE
    ];

    /// <summary>Role slugs for a raw DRIVER_INTERFACE value. Unparseable or
    /// empty input yields an empty list (an untyped device, not an error).</summary>
    public static IReadOnlyList<string> Decode(string? driverInterface) {
        if (string.IsNullOrWhiteSpace(driverInterface) ||
            !int.TryParse(driverInterface.Trim(), out var mask) || mask <= 0) {
            return [];
        }
        var roles = new List<string>();
        foreach (var (bit, role) in Bits) {
            if ((mask & bit) != 0) roles.Add(role);
        }
        return roles;
    }
}
