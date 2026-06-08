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

using NINA.Camera.SvbonySdk.Native;

namespace NINA.Camera.SvbonySdk;

/// <summary>Enumerate connected SVBony cameras. The returned Id is the SDK
/// camera index (stable for the current USB enumeration); Model is the
/// friendly name; Sn is the serial. Empty when the SDK is unavailable.</summary>
public static class SvbonyDiscovery {

    public record SvbonyCameraEntry(string Id, string Model, string Sn);

    public static IReadOnlyList<SvbonyCameraEntry> Enumerate() {
        SvbonyRegistry.EnsureResolver();
        var list = new List<SvbonyCameraEntry>();
        int n;
        try { n = SvbonyNative.SVBGetNumOfConnectedCameras(); }
        catch { return list; }
        for (int i = 0; i < n; i++) {
            var info = new SvbonyNative.SVB_CAMERA_INFO();
            if (SvbonyNative.SVBGetCameraInfo(ref info, i) != SvbonyNative.SVB_ERROR_CODE.SVB_SUCCESS)
                continue;
            var model = string.IsNullOrWhiteSpace(info.FriendlyName) ? $"SVBony #{i}" : info.FriendlyName;
            list.Add(new SvbonyCameraEntry(info.CameraID.ToString(), model, info.CameraSN ?? ""));
        }
        return list;
    }
}