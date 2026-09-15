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

/// <summary>
/// The per-rig capture settings every main-camera capture should carry, so a
/// LIVE loop, a preview snap or a solve frame runs the sensor the way the rig
/// was configured and not the way the driver happened to come up. The
/// sequencer always passed these; the other paths passed gain alone and left
/// the offset to whatever the driver held, which after a fresh driver config
/// is 1 (no pedestal, background clipped to black).
/// </summary>
public static class RigCaptureDefaults {
    /// <summary>The rig's DefaultOffset, or null when unset or zero.</summary>
    public static int? Offset(ProfileService? profiles) {
        var v = profiles?.ActiveEquipmentProfile?.DefaultOffset;
        return v is > 0 ? v : null;
    }
}
