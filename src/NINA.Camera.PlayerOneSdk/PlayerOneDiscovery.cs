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
