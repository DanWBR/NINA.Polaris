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

using System.Text.Json;
using System.Text.Json.Nodes;
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Merges a PARTIAL rig body onto the stored rig before PUT /api/equipment/rigs/{id}
/// applies it.
///
/// Why this exists: model binding turns an absent JSON property into the C#
/// property INITIALISER, not into "nothing". <c>EquipmentProfile.Name</c>
/// initialises to "Default", so a body like <c>{"liveStackComputeMode":"server"}</c>
/// bound to a fresh EquipmentProfile arrived at the handler carrying
/// <c>Name = "Default"</c> — a perfectly non-blank string that sailed past the
/// blank-guard and RENAMED the operator's rig. Field report: a rig called
/// "SV503" came back as "Default" after an update, with the guide algorithms,
/// pier-side handling and Dec guide mode likewise reset to their initialisers
/// (same shape: non-empty string defaults).
///
/// Nullable value types already made "absent" detectable (RIGPUT-1). This closes
/// the same hole for every string and collection in one place, and it is
/// self-maintaining: a new property with a non-empty default cannot reintroduce
/// the bug, because the merge seeds the model from the STORED rig instead of
/// from the initialisers. Present properties still flow through the handler's
/// existing validation, clamping and normalisation.
/// </summary>
public static class RigPatch {

    // Web defaults = camelCase out, case-insensitive in — matching both what
    // the client sends and what ProfileService persists.
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    /// <summary>Returns the rig the handler should treat as "the update":
    /// the stored rig with the body's properties laid over it. Absent ⇒ stored
    /// value, so an unconditional assignment in the handler is a no-op.</summary>
    public static EquipmentProfile Merge(EquipmentProfile stored, JsonObject? patch) {
        ArgumentNullException.ThrowIfNull(stored);
        var merged = JsonSerializer.SerializeToNode(stored, Opts)?.AsObject()
                     ?? new JsonObject();
        if (patch != null) {
            foreach (var kv in patch) {
                // Deserialization is case-insensitive, so a client sending
                // "Name" alongside our "name" would leave two candidates for
                // the same property. Drop the stored spelling first.
                foreach (var existing in merged.Select(e => e.Key)
                                               .Where(k => k != kv.Key
                                                   && string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase))
                                               .ToList()) {
                    merged.Remove(existing);
                }
                merged[kv.Key] = kv.Value?.DeepClone();
            }
        }
        return merged.Deserialize<EquipmentProfile>(Opts) ?? stored;
    }
}
