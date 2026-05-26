using NINA.Polaris.Services.Plugins;
using NINA.Polaris.Services.Sequencer;

namespace SamplePlugin;

/// <summary>
/// Minimal example of a sequencer instruction shipped by a third-party plugin.
/// Drop the compiled .dll into the host's <c>Plugins:Directory</c> and the
/// "SamplePlugin.Beep" entity will appear in the Advanced Sequencer palette.
/// </summary>
public class BeepInstruction : SequenceInstruction {
    public override string Type => "SamplePlugin.Beep";

    /// <summary>Free-form message written to the host's log when executed.</summary>
    public string Message { get; set; } = "Beep!";

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        ctx.Logger.LogInformation("[SamplePlugin] {Message}", Message);
        return Task.CompletedTask;
    }
}

public class BeepPlugin : INinaPolarisPlugin {
    public string Name        => "Sample Plugin";
    public string Version     => "1.0.0";
    public string Description => "Demo plugin, contributes a 'Beep' instruction that just logs a message";
    public string Author      => "N.I.N.A. Polaris team";

    public void Register(IPluginRegistry registry) {
        registry.RegisterSequencerEntity<BeepInstruction>("Plugins / Sample");
    }
}
