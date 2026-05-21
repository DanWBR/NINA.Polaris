using NINA.Headless.Services.Sequencer;

namespace NINA.Headless.Services.Plugins;

/// <summary>
/// Contract a third-party assembly implements to extend NINA Headless.
/// Plugins live as standalone .dll files dropped into the
/// <c>Plugins:Directory</c> folder (default <c>./plugins</c>). They
/// reference <c>NINA.Headless.dll</c> at compile time and are loaded
/// at app startup via an isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
///
/// V1 capability: contribute custom sequencer entities (instructions,
/// containers, conditions, triggers) into the palette + JSON converter.
/// Future versions may add device-driver and UI-panel hooks.
/// </summary>
public interface INinaHeadlessPlugin {
    /// <summary>Human-readable plugin name shown in the admin UI.</summary>
    string Name { get; }

    /// <summary>Version string (semantic versioning recommended).</summary>
    string Version { get; }

    /// <summary>One-line description for the plugin list.</summary>
    string Description { get; }

    /// <summary>Author / contact (free-form).</summary>
    string Author { get; }

    /// <summary>
    /// Register the plugin's contributions. Called once at startup, after
    /// every plugin's assembly has been loaded but before the host's
    /// HTTP listener starts accepting requests.
    /// </summary>
    void Register(IPluginRegistry registry);
}

/// <summary>
/// Surface the plugin uses to extend the host. Today the only extension
/// point is the sequencer entity registry; the interface lives in its
/// own type so we can add more without breaking existing plugins.
/// </summary>
public interface IPluginRegistry {
    /// <summary>
    /// Register an additional sequencer entity type. The host will:
    /// - Add it to the polymorphic JSON converter (under the entity's <c>Type</c>),
    /// - Surface it in the palette listing (<c>/api/sequencer/types</c>) so the
    ///   Advanced Sequencer UI shows it under the supplied category,
    /// - Use the public parameterless constructor when creating fresh instances
    ///   from the UI's palette.
    /// </summary>
    void RegisterSequencerEntity<TEntity>(string category) where TEntity : ISequenceEntity, new();

    /// <summary>Same as above but with an explicit CLR type for runtime registration.</summary>
    void RegisterSequencerEntity(Type entityType, string category);
}
