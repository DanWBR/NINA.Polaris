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

using System;
using System.Linq;

namespace NINA.Image.Interfaces;

/// <summary>
/// Who else has a camera open before a native SDK opens it.
///
/// <para>The ToupTek SDK opened an AE676C that indi_toupcam_ccd, still running
/// from an earlier session, had connected, and Polaris died with a segfault
/// nine seconds after it started. Two owners of one camera is not something a
/// vendor library reports as an error, so the check happens here.</para>
/// </summary>
public static class CameraClaims {
    /// <summary>Given the name a native SDK reports for a camera, the name of
    /// another client holding that camera (an INDI device), or null. Set by the
    /// host; unset means nobody else is consulted.</summary>
    public static Func<string, string?>? HolderOf { get; set; }

    /// <summary>Throws when another client holds the camera named
    /// <paramref name="cameraName"/>.</summary>
    public static void ThrowIfHeld(string? cameraName, string backend) {
        if (string.IsNullOrWhiteSpace(cameraName) || HolderOf == null) return;
        string? holder;
        try { holder = HolderOf(cameraName); } catch { return; }
        if (holder == null) return;
        throw new InvalidOperationException(
            $"{cameraName} is connected through the INDI driver '{holder}', so the {backend} cannot open it as well. "
            + "Use one of the two: disconnect the INDI device (or take its driver out of the INDI profile), "
            + "or pick the INDI camera instead of the native one.");
    }

    /// <summary>Do the INDI device name and the native SDK name refer to the
    /// same camera model? The native name's last word is the model
    /// ("AE676C", "ZWO ASI678MC"), and INDI drivers put it in the device name
    /// ("ToupTek AE676C", "ZWO CCD ASI678MC").</summary>
    public static bool SameModel(string indiDevice, string nativeName) {
        var model = Words(nativeName).LastOrDefault();
        // A model name has a number in it; a generic last word ("Camera")
        // would match unrelated devices.
        if (string.IsNullOrEmpty(model) || !model.Any(char.IsDigit)) return false;
        return Words(indiDevice).Any(w => string.Equals(w, model, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] Words(string s)
        => (s ?? "").Split(new[] { ' ', '\t', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
}
