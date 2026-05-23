namespace NINA.Polaris.Services.Sequencer.Instructions;

public class RotateToAngleInstruction : SequenceInstruction {
    public override string Type => "RotateToAngle";
    /// <summary>Sky position angle in degrees, 0 = north up.</summary>
    public double AngleDeg { get; set; }

    public override IReadOnlyList<string> Validate() =>
        (AngleDeg < 0 || AngleDeg >= 360) ? new[] { $"Angle out of range: {AngleDeg}" } : Array.Empty<string>();

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var r = ctx.Equipment.Rotator ?? throw new InvalidOperationException("No rotator connected");
        await r.MoveToAsync(AngleDeg, ct);
    }
}
