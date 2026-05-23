namespace NINA.Polaris.Services.Sequencer.Instructions;

public class OpenFlatCoverInstruction : SequenceInstruction {
    public override string Type => "OpenFlatCover";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.FlatDevice ?? throw new InvalidOperationException("No flat panel connected");
        await d.OpenCoverAsync(ct);
    }
}

public class CloseFlatCoverInstruction : SequenceInstruction {
    public override string Type => "CloseFlatCover";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.FlatDevice ?? throw new InvalidOperationException("No flat panel connected");
        await d.CloseCoverAsync(ct);
    }
}

public class SetFlatBrightnessInstruction : SequenceInstruction {
    public override string Type => "SetFlatBrightness";
    /// <summary>0-100; driver-specific scaling beyond that.</summary>
    public int Brightness { get; set; } = 50;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.FlatDevice ?? throw new InvalidOperationException("No flat panel connected");
        await d.SetBrightnessAsync(Brightness, ct);
    }
}

public class ToggleFlatLightInstruction : SequenceInstruction {
    public override string Type => "ToggleFlatLight";
    public bool On { get; set; } = true;
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.FlatDevice ?? throw new InvalidOperationException("No flat panel connected");
        await d.SetLightAsync(On, ct);
    }
}
