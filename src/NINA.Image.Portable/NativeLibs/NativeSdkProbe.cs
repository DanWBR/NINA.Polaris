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
using System.Collections.Generic;

namespace NINA.Image.NativeLibs;

/// <summary>
/// Shared list of directories the per-vendor camera-SDK resolvers should probe
/// for their native library, in priority order. Keeps the five SDK projects
/// (ZWO/SVBony/PlayerOne/ToupTek/Altair) decoupled from the host app: the host
/// exports the writable native-SDK pack directory via the
/// <c>POLARIS_NATIVE_SDK_DIR</c> environment variable at startup (the download
/// target for the on-demand camera-SDK pack), because on a .deb install the
/// app base dir (/opt/polaris) is not writable by the service user.
/// </summary>
public static class NativeSdkProbe {
    /// <summary>Environment variable the host sets to the writable directory
    /// where the downloadable camera-SDK pack is extracted.</summary>
    public const string EnvVar = "POLARIS_NATIVE_SDK_DIR";

    /// <summary>Directories to search for a bundled/downloaded native SDK lib,
    /// highest priority first: the app base directory (per-RID bundled Content),
    /// then the host-exported pack directory. The OS loader default search path
    /// is used by callers as a final fallback.</summary>
    public static IEnumerable<string> Dirs() {
        yield return AppContext.BaseDirectory;
        var extra = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(extra)) yield return extra;
    }
}
