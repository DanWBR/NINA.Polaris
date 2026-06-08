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

using NINA.Camera.ZwoSdk.Native;

namespace NINA.Camera.ZwoSdk;

/// <summary>Enumerate connected ZWO ASI cameras. Id is the SDK CameraID
/// (used by Open/control calls), Model is the camera name.</summary>
public static class ZwoDiscovery {

    public record ZwoCameraEntry(string Id, string Model, string Info);

    public static IReadOnlyList<ZwoCameraEntry> Enumerate() {
        ZwoRegistry.EnsureResolver();
        var list = new List<ZwoCameraEntry>();
        int n;
        try { n = AsiNative.ASIGetNumOfConnectedCameras(); }
        catch { return list; }
        for (int i = 0; i < n; i++) {
            var info = new AsiNative.ASI_CAMERA_INFO();
            if (AsiNative.ASIGetCameraProperty(ref info, i) != AsiNative.ASI_ERROR_CODE.ASI_SUCCESS)
                continue;
            var model = string.IsNullOrWhiteSpace(info.Name) ? $"ASI #{info.CameraID}" : info.Name;
            list.Add(new ZwoCameraEntry(info.CameraID.ToString(), model, model));
        }
        return list;
    }
}