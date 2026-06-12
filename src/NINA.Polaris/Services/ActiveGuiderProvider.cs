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
/// Routes generic guider operations to whichever backend the active rig
/// selected. The native autoguider is the default; a rig opts into the
/// external PHD2 process by setting <c>EquipmentProfile.GuiderDriver ==
/// "phd2"</c> (anything else, including a fresh rig, uses native).
///
/// <para>GuiderEndpoints and the status WebSocket resolve <see cref="Active"/>
/// for every generic call so the switch is transparent to the frontend.
/// PHD2-only routes (profiles, GUI/VNC sessions, algo presets, smart
/// calibrate, process lifecycle) stay bound to <see cref="PHD2Client"/>
/// directly.</para>
/// </summary>
public sealed class ActiveGuiderProvider {
    private readonly ProfileService _profiles;
    private readonly PHD2Client _phd2;
    private readonly NativeGuider _native;

    public ActiveGuiderProvider(ProfileService profiles, PHD2Client phd2, NativeGuider native) {
        _profiles = profiles;
        _phd2 = phd2;
        _native = native;
    }

    /// <summary>The guider backend the active rig is configured to use.
    /// Native is the default: only an explicit <c>phd2</c> selects the
    /// external PHD2 process, so a fresh rig (or one missing the field)
    /// uses the in-process native guider.</summary>
    public IGuider Active =>
        string.Equals(_profiles.ActiveEquipmentProfile.GuiderDriver, "phd2",
            StringComparison.OrdinalIgnoreCase)
            ? _phd2
            : _native;
}