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

using NINA.Polaris.Services.Plugins;

namespace NINA.Polaris.Endpoints;

public static class PluginEndpoints {
    public static void MapPluginEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/plugins");

        // List loaded plugins + the entities they contributed. The Advanced
        // Sequencer's /api/sequencer/types endpoint already includes plugin
        // entities (KnownTypes merges built-in + plugin); this is a curated
        // view scoped to plugins for the admin UI.
        g.MapGet("/", (PluginLoaderService loader) => Results.Ok(loader.LoadedPlugins));
    }
}