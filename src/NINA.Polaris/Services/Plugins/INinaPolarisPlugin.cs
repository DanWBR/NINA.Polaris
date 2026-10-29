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

using NINA.Polaris.Services.PlateSolving;
using NINA.Polaris.Services.Sequencer;

namespace NINA.Polaris.Services.Plugins;

/// <summary>
/// Contract a third-party assembly implements to extend N.I.N.A. Polaris.
/// Plugins live as standalone .dll files dropped into the
/// <c>Plugins:Directory</c> folder (default <c>./plugins</c>). They
/// reference <c>NINA.Polaris.dll</c> at compile time and are loaded
/// at app startup via an isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
///
/// Capabilities: contribute custom sequencer entities (instructions,
/// containers, conditions, triggers) into the palette + JSON converter, and
/// plate solvers into the solver list. Future versions may add device-driver
/// and UI-panel hooks.
/// </summary>
public interface INinaPolarisPlugin {
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
/// Surface the plugin uses to extend the host: sequencer entities and plate
/// solvers. The interface lives in its own type so we can add more without
/// breaking existing plugins.
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

    /// <summary>
    /// Add a plate solver. It then appears in <c>/api/platesolve/solvers</c>,
    /// can be picked as the primary solver (and as the blind fallback when
    /// <see cref="IPlateSolver.SupportsBlindSolve"/> is true), and is called
    /// through the same dispatcher, timeout and cancellation as the built-in
    /// ones. The <see cref="IPlateSolver.Id"/> is what the profile stores, so
    /// keep it stable across versions. Built-in ids and an id another plugin
    /// already took are refused with <see cref="InvalidOperationException"/>.
    /// <see cref="IPlateSolver.SolveAsync"/> must honour its cancellation token
    /// and kill any process it started; <see cref="IPlateSolver.IsAvailable"/>
    /// is read often, so keep it cheap.
    /// </summary>
    void RegisterPlateSolver(IPlateSolver solver);
}