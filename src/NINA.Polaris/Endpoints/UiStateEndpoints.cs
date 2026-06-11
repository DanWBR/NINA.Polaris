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
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Generic, free-form persistence for sticky UI input fields (panel
/// exposure/gain/binning, target name, auto-focus parameters, ...). The client
/// owns the schema: it PUTs a JSON object of the fields it wants to remember and
/// re-applies them on load, so reopening Polaris from any browser restores the
/// last values the operator typed. Stored as one JSON blob under the active
/// profile's DataDir; not per-rig (these are "last value I entered" prefs).
/// </summary>
public static class UiStateEndpoints {
    public static void MapUiStateEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/ui-state");

        group.MapGet("/", (ProfileService profiles) => {
            var path = StatePath(profiles);
            if (!File.Exists(path)) return Results.Content("{}", "application/json");
            try { return Results.Content(File.ReadAllText(path), "application/json"); }
            catch { return Results.Content("{}", "application/json"); }
        });

        // Shallow-merge the incoming object into the stored one so independent
        // features can persist their own keys without clobbering each other.
        group.MapPut("/", async (JsonElement body, ProfileService profiles) => {
            if (body.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Body must be a JSON object." });

            var path = StatePath(profiles);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var merged = new Dictionary<string, JsonElement>();
            if (File.Exists(path)) {
                try {
                    var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        File.ReadAllText(path));
                    if (existing != null) merged = existing;
                } catch { /* corrupt file → start fresh */ }
            }
            foreach (var prop in body.EnumerateObject()) merged[prop.Name] = prop.Value.Clone();

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(merged));
            return Results.Ok(new { saved = true });
        });
    }

    private static string StatePath(ProfileService profiles)
        => Path.Combine(profiles.DataDir, "ui-state.json");
}
