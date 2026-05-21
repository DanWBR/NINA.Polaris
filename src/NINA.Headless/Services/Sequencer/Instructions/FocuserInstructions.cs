namespace NINA.Headless.Services.Sequencer.Instructions;

/// <summary>Move the focuser to an absolute step position and wait for the move to complete.</summary>
public class MoveFocuserInstruction : SequenceInstruction {
    public override string Type => "MoveFocuser";
    public int Position { get; set; }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var f = ctx.Equipment.Focuser ?? throw new InvalidOperationException("No focuser connected");
        await f.MoveAbsoluteAsync(Position, ct);
    }
}

/// <summary>
/// Run the V-curve auto-focus routine; the engine waits for it to finish
/// and bubbles up whatever HFR it landed on.
/// </summary>
public class AutoFocusInstruction : SequenceInstruction {
    public override string Type => "AutoFocus";

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        ctx.AutoFocus.Start(new AutoFocusRequest());
        while (ctx.AutoFocus.State == AutoFocusState.Running) {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
        }
        if (!string.IsNullOrEmpty(ctx.AutoFocus.LastError))
            throw new InvalidOperationException("Auto-focus failed: " + ctx.AutoFocus.LastError);
    }
}

/// <summary>
/// Move the focuser by a per-filter offset relative to the active rig's
/// reference filter. Today: simple Δsteps from the active focuser position.
/// (Filter-offset table lives in the rig profile in a later commit.)
/// </summary>
public class MoveToFilterOffsetInstruction : SequenceInstruction {
    public override string Type => "MoveToFilterOffset";
    public int OffsetSteps { get; set; }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var f = ctx.Equipment.Focuser ?? throw new InvalidOperationException("No focuser connected");
        await f.MoveRelativeAsync(OffsetSteps, ct);
    }
}
