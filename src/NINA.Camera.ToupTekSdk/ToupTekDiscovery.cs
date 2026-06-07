namespace NINA.Camera.ToupTekSdk;

/// <summary>Enumerate connected ToupTek cameras. Id is the SDK opaque device
/// id (used by Toupcam.Open), Model is the display name.</summary>
public static class ToupTekDiscovery {

    public record ToupTekCameraEntry(string Id, string Model, string Info);

    public static IReadOnlyList<ToupTekCameraEntry> Enumerate() {
        ToupTekRegistry.EnsureResolver();
        var list = new List<ToupTekCameraEntry>();
        Toupcam.DeviceV2[] devs;
        try { devs = Toupcam.EnumV2(); }
        catch { return list; }
        if (devs == null) return list;
        foreach (var d in devs) {
            var model = string.IsNullOrWhiteSpace(d.displayname) ? d.id : d.displayname;
            list.Add(new ToupTekCameraEntry(d.id, model, model));
        }
        return list;
    }
}
