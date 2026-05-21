namespace NINA.Headless.Services.Sequencer.Instructions;

/// <summary>Switch the filter wheel to a named filter (or numeric position).</summary>
public class SwitchFilterInstruction : SequenceInstruction {
    public override string Type => "SwitchFilter";

    /// <summary>Filter name as configured in the wheel. Takes precedence over <see cref="Position"/>.</summary>
    public string? FilterName { get; set; }

    /// <summary>1-based filter position fallback when <see cref="FilterName"/> isn't set.</summary>
    public int? Position { get; set; }

    public override IReadOnlyList<string> Validate() {
        if (string.IsNullOrWhiteSpace(FilterName) && !Position.HasValue)
            return new[] { "Provide either FilterName or Position" };
        return Array.Empty<string>();
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var fw = ctx.Equipment.FilterWheel ?? throw new InvalidOperationException("No filter wheel connected");
        if (!string.IsNullOrEmpty(FilterName)) {
            await fw.SetFilterByNameAsync(FilterName, ct);
        } else if (Position.HasValue) {
            await fw.SetPositionAsync(Position.Value, ct);
        }
    }
}
