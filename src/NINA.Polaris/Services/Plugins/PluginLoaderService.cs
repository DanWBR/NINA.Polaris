using System.Reflection;
using System.Runtime.Loader;
using NINA.Polaris.Services.Sequencer;

namespace NINA.Polaris.Services.Plugins;

/// <summary>
/// Scans <c>Plugins:Directory</c> (default <c>./plugins</c>) at app startup
/// for plugin .dll files, loads each into its own
/// <see cref="AssemblyLoadContext"/> (so they can be unloaded later if we
/// add a reload endpoint), and invokes <see cref="INinaPolarisPlugin.Register"/>
/// on every implementation found.
///
/// Failures are logged and isolated — one broken plugin does not stop the
/// host or block the others.
/// </summary>
public class PluginLoaderService : IHostedService {
    private readonly IConfiguration _config;
    private readonly ILogger<PluginLoaderService> _logger;
    private readonly List<LoadedPlugin> _loaded = new();

    public PluginLoaderService(IConfiguration config, ILogger<PluginLoaderService> logger) {
        _config = config;
        _logger = logger;
    }

    public IReadOnlyList<LoadedPlugin> LoadedPlugins => _loaded;

    public Task StartAsync(CancellationToken cancellationToken) {
        var dir = _config.GetValue<string?>("Plugins:Directory") ?? "plugins";
        var enabled = _config.GetValue("Plugins:Enabled", true);
        if (!enabled) {
            _logger.LogInformation("Plugin loader disabled (Plugins:Enabled=false)");
            return Task.CompletedTask;
        }
        if (!Directory.Exists(dir)) {
            _logger.LogDebug("Plugin dir {Dir} does not exist; nothing to load", dir);
            return Task.CompletedTask;
        }
        var dlls = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories);
        _logger.LogInformation("Plugin loader scanning {Dir}: {Count} .dll candidate(s)", dir, dlls.Length);
        foreach (var path in dlls) TryLoadPlugin(path);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void TryLoadPlugin(string path) {
        try {
            var ctx = new PluginLoadContext(path);
            var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(path));
            var pluginTypes = asm.GetTypes()
                .Where(t => typeof(INinaPolarisPlugin).IsAssignableFrom(t)
                         && !t.IsAbstract && !t.IsInterface)
                .ToArray();
            if (pluginTypes.Length == 0) {
                _logger.LogDebug("{Path}: no INinaPolarisPlugin implementations", path);
                return;
            }
            foreach (var t in pluginTypes) {
                try {
                    var plugin = (INinaPolarisPlugin?)Activator.CreateInstance(t)
                        ?? throw new InvalidOperationException("Activator returned null");
                    var registry = new PluginRegistry(plugin.Name, _logger);
                    plugin.Register(registry);
                    _loaded.Add(new LoadedPlugin {
                        Name = plugin.Name, Version = plugin.Version,
                        Description = plugin.Description, Author = plugin.Author,
                        Assembly = Path.GetFileName(path),
                        EntityDiscriminators = registry.RegisteredDiscriminators.ToArray()
                    });
                    _logger.LogInformation("Loaded plugin '{Name}' v{Version} from {Asm} ({Count} entities)",
                        plugin.Name, plugin.Version, Path.GetFileName(path), registry.RegisteredDiscriminators.Count);
                } catch (Exception ex) {
                    _logger.LogError(ex, "Plugin type {Type} in {Path} failed to register", t.FullName, path);
                }
            }
        } catch (BadImageFormatException) {
            // Not a managed assembly — silently skip
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not load {Path}", path);
        }
    }

    private class PluginRegistry : IPluginRegistry {
        private readonly string _pluginName;
        private readonly ILogger _logger;
        public List<string> RegisteredDiscriminators { get; } = new();

        public PluginRegistry(string pluginName, ILogger logger) {
            _pluginName = pluginName;
            _logger = logger;
        }

        public void RegisterSequencerEntity<TEntity>(string category) where TEntity : ISequenceEntity, new() {
            RegisterSequencerEntity(typeof(TEntity), category);
        }

        public void RegisterSequencerEntity(Type entityType, string category) {
            var discriminator = SequenceEntityJsonConverter.RegisterPluginEntity(entityType, category);
            RegisteredDiscriminators.Add(discriminator);
            _logger.LogInformation("  [{Plugin}] +entity '{Disc}' → {Category}", _pluginName, discriminator, category);
        }
    }
}

public class LoadedPlugin {
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string[] EntityDiscriminators { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Per-plugin AssemblyLoadContext so plugin dependencies don't leak into
/// the default context. Uses an AssemblyDependencyResolver to find native
/// + managed satellites next to the main plugin .dll.
/// </summary>
internal class PluginLoadContext : AssemblyLoadContext {
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        : base(isCollectible: false) {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName name) {
        // Defer to the default context for assemblies the host already loaded
        // (so plugins reuse the host's ISequenceEntity, ILogger, etc.).
        if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
