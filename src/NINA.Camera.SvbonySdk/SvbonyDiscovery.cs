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
