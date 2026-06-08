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

using NINA.Camera.PlayerOneSdk.Native;

namespace NINA.Camera.PlayerOneSdk;

/// <summary>Enumerate connected PlayerOne cameras. Id is the SDK cameraID
/// (used by Open/config calls), Model is the camera name.</summary>
public static class PlayerOneDiscovery {

    public record PlayerOneCameraEntry(string Id, string Model, string Info);

    public static IReadOnlyList<PlayerOneCameraEntry> Enumerate() {
        PlayerOneRegistry.EnsureResolver();
        var list = new List<PlayerOneCameraEntry>();
        int n;
        try { n = PoaNative.POAGetCameraCount(); }
        catch { return list; }
        for (int i = 0; i < n; i++) {
            var info = new PoaNative.POACameraProperties();
            if (PoaNative.POAGetCameraProperties(i, ref info) != PoaNative.POAErrors.POA_OK)
                continue;
            var model = string.IsNullOrWhiteSpace(info.cameraModelName) ? $"PlayerOne #{info.cameraID}" : info.cameraModelName;
            list.Add(new PlayerOneCameraEntry(info.cameraID.ToString(), model, model));
        }
        return list;
    }
}