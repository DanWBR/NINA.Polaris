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
