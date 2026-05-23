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
