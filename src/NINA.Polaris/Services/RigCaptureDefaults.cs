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
    /// <summary>The LIVE panel's offset, or null when unset or zero. Null is
    /// the contract for "do not write an offset to the driver": the capture
    /// leaves the driver's own value alone and the frame's header records what
    /// the driver reports (issue #26).
    ///
    /// Also the value used by captures that have no panel of their own: the
    /// plate solve, autofocus, polar alignment and the video stream.</summary>
    public static int? Offset(ProfileService? profiles) {
        var v = profiles?.ActiveEquipmentProfile?.DefaultOffset;
        return v is > 0 ? v : null;
    }

    /// <summary>The PREVIEW panel's offset, same contract.</summary>
    public static int? PreviewOffset(ProfileService? profiles) {
        var v = profiles?.ActiveEquipmentProfile?.PreviewOffset;
        return v is > 0 ? v : null;
    }

    /// <summary>The AUTORUN panel's offset, same contract.</summary>
    public static int? AutorunOffset(ProfileService? profiles) {
        var v = profiles?.ActiveEquipmentProfile?.AutorunOffset;
        return v is > 0 ? v : null;
    }

    /// <summary>The ADV panel's offset, same contract. An instruction that
    /// pins its own offset outranks it: that field is per exposure, this one
    /// is the panel's default for the ones that do not.</summary>
    public static int? AdvOffset(ProfileService? profiles) {
        var v = profiles?.ActiveEquipmentProfile?.AdvOffset;
        return v is > 0 ? v : null;
    }
}
