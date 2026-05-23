namespace NINA.Polaris.Services.Sequencer.Instructions;

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
/// Move the focuser by the per-filter offset configured in the active rig.
/// When <see cref="FilterName"/> is set the instruction looks up
/// <c>EquipmentProfile.FilterOffsets[FilterName]</c> on the active rig and
/// applies that as a relative step move. Filters absent from the table are
/// treated as 0 (the focuser doesn't move) so it's safe to drop one of these
/// after every filter change without curating offsets for every filter.
///
/// As a fallback, callers may set <see cref="OffsetSteps"/> directly to bypass
/// the table — useful for ad-hoc tweaks that don't deserve a profile entry.
/// </summary>
public class MoveToFilterOffsetInstruction : SequenceInstruction {
    public override string Type => "MoveToFilterOffset";

    /// <summary>Filter name to look up in the active rig's FilterOffsets table.</summary>
    public string? FilterName { get; set; }

    /// <summary>Explicit fallback when no FilterName / no table entry.</summary>
    public int OffsetSteps { get; set; }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var f = ctx.Equipment.Focuser ?? throw new InvalidOperationException("No focuser connected");

        int delta = OffsetSteps;
        if (!string.IsNullOrEmpty(FilterName)) {
            var rig = ctx.Profiles.ActiveEquipmentProfile;
            if (rig.FilterOffsets.TryGetValue(FilterName, out var configured)) {
                delta = configured;
                ctx.Logger.LogInformation("Filter offset for {Filter} → {Delta} steps (from rig '{Rig}')",
                    FilterName, delta, rig.Name);
            } else {
                ctx.Logger.LogDebug("No filter offset configured for {Filter} on rig '{Rig}', moving 0",
                    FilterName, rig.Name);
                delta = 0;
            }
        }

        if (delta != 0) await f.MoveRelativeAsync(delta, ct);
    }
}
