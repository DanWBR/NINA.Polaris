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

namespace NINA.Camera.SonySdk;

/// <summary>Connected-Sony-bodies enumeration. Returns empty until
/// the SDK binding is implemented, see <see cref="SonySdkRegistry"/>.</summary>
public static class SonySdkDiscovery {

    public record SonyCameraEntry(string Id, string Model, string PortName);

    public static IReadOnlyList<SonyCameraEntry> Enumerate()
        => Array.Empty<SonyCameraEntry>();
}