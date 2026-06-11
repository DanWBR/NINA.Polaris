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
using NINA.Polaris.Services.Sequencer;

namespace NINA.Polaris.Endpoints;

public static class AdvancedSequenceEndpoints {
    public static void MapAdvancedSequenceEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/sequencer");

        // ---- Document IO ----
        // NOTE: these go through SequenceJson, NOT the default minimal-API
        // (de)serializer. SequenceDocument.Root is the ISequenceEntity
        // interface, which the default System.Text.Json can neither read
        // (throws on abstract/interface types) nor write fully (drops every
        // concrete subtype property + the $type discriminator). SequenceJson
        // carries the polymorphic converter + camelCase web shape the editor
        // speaks.
        g.MapGet("/document", (AdvancedSequenceEngine engine) => {
            var payload = new {
                document = engine.Document,
                state = engine.State.ToString(),   // string, the UI compares === 'Running'
                lastError = engine.LastError,
                startedAt = engine.StartedAt,
                finishedAt = engine.FinishedAt,
                abortReason = engine.AbortReason
            };
            return Results.Text(JsonSerializer.Serialize(payload, SequenceJson.Options),
                "application/json");
        });

        g.MapPost("/document", async (HttpRequest req, AdvancedSequenceEngine engine) => {
            using var sr = new StreamReader(req.Body);
            var text = await sr.ReadToEndAsync();
            try {
                var doc = SequenceJson.Deserialize(text);
                engine.Load(doc);
                return Results.Ok(new { loaded = true, validation = engine.Validate() });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Convenience: round-trip JSON so the UI can save the current document
        // to disk via the browser's download dialog, or import a hand-edited file.
        g.MapGet("/document/json", (AdvancedSequenceEngine engine) =>
            Results.Text(SequenceJson.Serialize(engine.Document), "application/json"));

        g.MapPost("/document/json", async (HttpRequest req, AdvancedSequenceEngine engine) => {
            using var sr = new StreamReader(req.Body);
            var text = await sr.ReadToEndAsync();
            try {
                var doc = SequenceJson.Deserialize(text);
                engine.Load(doc);
                return Results.Ok(new { loaded = true, validation = engine.Validate() });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- Lifecycle ----
        g.MapPost("/start", (AdvancedSequenceEngine engine) => {
            engine.Start();
            return Results.Ok(new { state = engine.State.ToString(), error = engine.LastError });
        });

        g.MapPost("/stop", (AdvancedSequenceEngine engine) => {
            engine.Stop();
            return Results.Ok(new { state = engine.State.ToString() });
        });

        g.MapPost("/validate", (AdvancedSequenceEngine engine) =>
            Results.Ok(new { errors = engine.Validate() }));

        // ---- Palette ----
        // `defaults` carries the default scalar params of each entity so the
        // tree editor can render editable fields the instant an entity is
        // dropped (the editor derives its field list from the node's own keys).
        g.MapGet("/types", () => Results.Ok(SequenceEntityJsonConverter.KnownTypes.Select(t => new {
            type = t.Type, category = t.Category, kind = t.Class,
            defaults = SequenceEntityJsonConverter.DefaultParams(t.Type)
        })));

        // ---- Templates ----
        g.MapGet("/templates", (SequenceTemplateStore store) => Results.Ok(new {
            dir = store.Dir,
            templates = store.List().ToArray()
        }));

        g.MapGet("/templates/{name}", (string name, SequenceTemplateStore store) => {
            var doc = store.Load(name);
            // Serialize through SequenceJson for the same polymorphic reason as
            // /document (Root is an interface).
            return doc == null
                ? Results.NotFound()
                : Results.Text(SequenceJson.Serialize(doc), "application/json");
        });

        g.MapPost("/templates/{name}", async (string name, HttpRequest req, SequenceTemplateStore store) => {
            using var sr = new StreamReader(req.Body);
            var text = await sr.ReadToEndAsync();
            try {
                var doc = SequenceJson.Deserialize(text);
                store.Save(name, doc);
                return Results.Ok(new { saved = true });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapDelete("/templates/{name}", (string name, SequenceTemplateStore store) => {
            return store.Delete(name) ? Results.Ok(new { deleted = true }) : Results.NotFound();
        });
    }
}