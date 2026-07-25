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

public static class SequenceEndpoints {
    public static void MapSequenceEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/sequence");

        group.MapGet("/", (SequenceEngine engine) => {
            return Results.Ok(new {
                items = engine.Items.Select(i => new {
                    i.Name, i.Exposure, i.Gain, i.Binning, i.Count,
                    i.Filter, i.Ra, i.Dec, i.ImageType
                }),
                state = engine.State.ToString().ToLowerInvariant()
            });
        });

        group.MapPost("/", (List<SequenceItem> items, SequenceEngine engine) => {
            try {
                engine.LoadSequence(items);
                return Results.Ok(new { message = "Sequence loaded", itemCount = items.Count });
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // resume=true continues a partially-completed run from where it
        // stopped (the engine retains CurrentItemIndex/CurrentFrameInItem);
        // anything else resets progress first so it starts from the top.
        group.MapPost("/start", (SequenceEngine engine, StartRequest? req) => {
            if (req?.Resume != true) engine.ResetProgress();
            engine.Start();
            return Results.Ok(new {
                state = engine.State.ToString().ToLowerInvariant(),
                resumed = req?.Resume == true
            });
        });

        // Put the schedule back to its starting state without touching the
        // items: nothing shot yet, no error, ready to run from the top. This
        // is the AUTORUN "Reset" button; "Clear" posts an empty list to /.
        group.MapPost("/reset", (SequenceEngine engine) => {
            if (engine.State == SequenceState.Running)
                return Results.Conflict(new { error = "Cannot reset while running" });

            engine.ResetProgress();
            return Results.Ok(new {
                message = "Progress reset",
                itemCount = engine.Items.Count,
                state = engine.State.ToString().ToLowerInvariant()
            });
        });

        group.MapPost("/pause", (SequenceEngine engine) => {
            engine.Pause();
            return Results.Ok(new { state = engine.State.ToString().ToLowerInvariant() });
        });

        group.MapPost("/resume", (SequenceEngine engine) => {
            engine.Resume();
            return Results.Ok(new { state = engine.State.ToString().ToLowerInvariant() });
        });

        group.MapPost("/stop", (SequenceEngine engine) => {
            engine.Stop();
            return Results.Ok(new { state = engine.State.ToString().ToLowerInvariant() });
        });

        group.MapGet("/status", (SequenceEngine engine) => {
            return Results.Ok(engine.GetStatus());
        });

        group.MapPost("/items/add", (SequenceItem item, SequenceEngine engine) => {
            if (engine.State == SequenceState.Running)
                return Results.Conflict(new { error = "Cannot modify sequence while running" });

            engine.Items.Add(item);
            return Results.Ok(new { message = "Item added", itemCount = engine.Items.Count });
        });

        group.MapDelete("/items/{index:int}", (int index, SequenceEngine engine) => {
            if (engine.State == SequenceState.Running)
                return Results.Conflict(new { error = "Cannot modify sequence while running" });

            if (index < 0 || index >= engine.Items.Count)
                return Results.NotFound(new { error = "Item index out of range" });

            engine.Items.RemoveAt(index);
            return Results.Ok(new { message = "Item removed", itemCount = engine.Items.Count });
        });

        // --- Dither settings ---

        group.MapGet("/dither", (SequenceEngine engine) => {
            return Results.Ok(engine.Dither);
        });

        // --- End-of-run actions ---

        group.MapGet("/end-actions", (SequenceEngine engine) => Results.Ok(engine.EndActions));

        group.MapPut("/end-actions", (SequenceEndActions actions, SequenceEngine engine) => {
            engine.EndActions = actions ?? new SequenceEndActions();
            return Results.Ok(engine.EndActions);
        });

        group.MapPut("/dither", (DitherSettings settings, SequenceEngine engine) => {
            // Defensive normalisation
            if (settings.EveryNFrames < 1) settings.EveryNFrames = 1;
            if (settings.Pixels < 0) settings.Pixels = 0;
            if (settings.SettlePixels < 0) settings.SettlePixels = 0;
            if (settings.SettleTime < 0) settings.SettleTime = 0;
            if (settings.SettleTimeout < 1) settings.SettleTimeout = 1;
            engine.Dither = settings;
            return Results.Ok(engine.Dither);
        });

        // --- Named sequence sets (save / reload presets) ---
        // Stored as one JSON file per set under {DataDir}/sequence-sets/. The
        // body is the raw items array (stored verbatim so client-only fields
        // like thumbUrl / fromSky survive a round-trip).

        group.MapGet("/sets", (ProfileService profiles) => {
            var dir = SetsDir(profiles);
            if (!Directory.Exists(dir)) return Results.Ok(new { sets = Array.Empty<string>() });
            var names = Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Results.Ok(new { sets = names });
        });

        group.MapPost("/sets/{name}", async (string name, JsonElement items, ProfileService profiles) => {
            var safe = SafeSetName(name);
            if (string.IsNullOrEmpty(safe)) return Results.BadRequest(new { error = "Invalid set name." });
            var dir = SetsDir(profiles);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, safe + ".json"), items.GetRawText());
            return Results.Ok(new { saved = safe });
        });

        group.MapGet("/sets/{name}", async (string name, ProfileService profiles) => {
            var safe = SafeSetName(name);
            var path = Path.Combine(SetsDir(profiles), safe + ".json");
            if (!File.Exists(path)) return Results.NotFound(new { error = "Set not found." });
            // Return the stored items array verbatim.
            return Results.Content(await File.ReadAllTextAsync(path), "application/json");
        });

        group.MapDelete("/sets/{name}", (string name, ProfileService profiles) => {
            var safe = SafeSetName(name);
            var path = Path.Combine(SetsDir(profiles), safe + ".json");
            if (File.Exists(path)) File.Delete(path);
            return Results.Ok(new { deleted = safe });
        });
    }

    private static string SetsDir(ProfileService profiles)
        => Path.Combine(profiles.DataDir, "sequence-sets");

    private static string SafeSetName(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 64 ? s[..64] : s;
    }

    public record StartRequest(bool? Resume);
}