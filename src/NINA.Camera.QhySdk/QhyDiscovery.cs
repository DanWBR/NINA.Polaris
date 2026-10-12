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

namespace NINA.Camera.QhySdk;

/// <summary>Enumerate connected QHY cameras. Id is the SDK camera id
/// ("QHY600M-6bb54f4c1d2e3f40", what OpenQHYCCD takes), Model is the camera
/// name, and Info carries the id too, so two cameras of the same model can
/// be told apart in the picker.</summary>
public static class QhyDiscovery {

    public record QhyCameraEntry(string Id, string Model, string Info);

    public static IReadOnlyList<QhyCameraEntry> Enumerate() {
        QhyRegistry.EnsureResolver();
        var list = new List<QhyCameraEntry>();
        IReadOnlyList<QhyRegistry.QhyCameraId> found;
        try { found = QhyRegistry.Scan(); }
        catch { return list; }
        foreach (var c in found)
            list.Add(new QhyCameraEntry(c.Id, c.Model, c.Id));
        return list;
    }
}
