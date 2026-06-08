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