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

using System.Runtime.Versioning;

namespace NINA.Camera.NikonSdk;

/// <summary>Connected-Nikon-bodies enumeration. Currently returns
/// an empty list because the driver is a skeleton. See
/// <see cref="NikonSdkRegistry"/> for the open work.</summary>
[SupportedOSPlatform("windows")]
public static class NikonSdkDiscovery {

    public record NikonCameraEntry(string Id, string Model, string PortName);

    public static IReadOnlyList<NikonCameraEntry> Enumerate()
        => Array.Empty<NikonCameraEntry>();
}