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

using System.Text.Json.Serialization;

namespace NINA.Polaris.Services.Sequencer;

/// <summary>
/// Convenience base class, most entities only override <c>Type</c>,
/// <c>ExecuteAsync</c>, and optionally <c>Validate</c>.
/// </summary>
public abstract class SequenceEntityBase : ISequenceEntity {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonIgnore] public abstract string Type { get; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    [JsonIgnore] public SequenceEntityStatus Status { get; set; } = SequenceEntityStatus.Idle;
    [JsonIgnore] public string? Error { get; set; }
    [JsonIgnore] public DateTime? StartedAt { get; set; }
    [JsonIgnore] public DateTime? FinishedAt { get; set; }

    public virtual IReadOnlyList<string> Validate() => Array.Empty<string>();

    public abstract Task ExecuteAsync(SequenceContext ctx, CancellationToken ct);

    /// <summary>
    /// Reset transient state in-place. Called by the engine before every run
    /// so the tree shows a clean slate even if it was edited mid-run.
    /// </summary>
    public virtual void ResetRuntimeState() {
        Status = SequenceEntityStatus.Idle;
        Error = null;
        StartedAt = null;
        FinishedAt = null;
    }
}

/// <summary>
/// Base for things that aggregate child entities. Holds the child list and a
/// list of triggers that are polled before each child step.
///
/// Subclasses control HOW children are executed (sequential, parallel, with a
/// preamble like a DSO target slew, etc).
/// </summary>
public abstract class SequenceContainer : SequenceEntityBase, IErrorHandlingEntity {
    public List<ISequenceEntity> Items { get; set; } = new();
    public List<SequenceTrigger> Triggers { get; set; } = new();
    public List<SequenceCondition> Conditions { get; set; } = new();

    /// <summary>If true the container loops until all conditions stop returning true.</summary>
    public bool IsLoop { get; set; } = false;

    /// <summary>Retry count for the container as a step of its parent. Min 1.</summary>
    public int Attempts { get; set; } = 1;

    /// <summary>Failure policy for the container as a step of its parent.</summary>
    public InstructionErrorBehavior ErrorBehavior { get; set; } = InstructionErrorBehavior.AbortRun;

    /// <summary>
    /// Triggers handed down from ancestor containers. Set by the parent right
    /// before this container runs so an ancestor's trigger is also evaluated
    /// between THIS container's steps (NINA-style cascade). Runtime-only.
    /// </summary>
    [JsonIgnore] public IReadOnlyList<SequenceTrigger> InheritedTriggers { get; set; } = Array.Empty<SequenceTrigger>();

    /// <summary>Backoff between retry attempts. Internal so tests can zero it.</summary>
    internal static TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Outcome of running a single child step.</summary>
    protected enum ChildOutcome { Continue, StopContainer }

    /// <summary>Inherited (ancestor) triggers + this container's own triggers,
    /// inherited first so flips/AF/dither defined higher up still fire here.</summary>
    protected IReadOnlyList<SequenceTrigger> EffectiveTriggers() {
        if (InheritedTriggers.Count == 0) return Triggers;
        if (Triggers.Count == 0) return InheritedTriggers;
        var list = new List<SequenceTrigger>(InheritedTriggers.Count + Triggers.Count);
        list.AddRange(InheritedTriggers);
        list.AddRange(Triggers);
        return list;
    }

    public override IReadOnlyList<string> Validate() {
        var errors = new List<string>();
        for (int i = 0; i < Items.Count; i++) {
            foreach (var e in Items[i].Validate())
                errors.Add($"[{Name}/{Items[i].Name ?? "#" + i}] {e}");
        }
        foreach (var t in Triggers) {
            foreach (var e in t.Validate()) errors.Add($"[{Name}/trigger:{t.Name}] {e}");
        }
        foreach (var c in Conditions) {
            foreach (var e in c.Validate()) errors.Add($"[{Name}/condition:{c.Name}] {e}");
        }
        return errors;
    }

    public override void ResetRuntimeState() {
        base.ResetRuntimeState();
        foreach (var item in Items)
            if (item is SequenceEntityBase b) b.ResetRuntimeState();
        foreach (var t in Triggers) t.ResetRuntimeState();
        foreach (var c in Conditions) c.ResetRuntimeState();
    }

    /// <summary>
    /// Walks <see cref="Triggers"/>, asking each whether it wants to fire now;
    /// runs the trigger's action body if so. Used by subclasses between
    /// child steps.
    /// </summary>
    protected async Task EvaluateTriggersAsync(SequenceContext ctx, CancellationToken ct) {
        foreach (var trigger in EffectiveTriggers()) {
            if (ctx.AbortRequested) return;
            try {
                if (await trigger.ShouldFireAsync(ctx, ct)) {
                    trigger.Status = SequenceEntityStatus.Running;
                    trigger.StartedAt = DateTime.UtcNow;
                    try {
                        await trigger.ExecuteAsync(ctx, ct);
                        trigger.Status = SequenceEntityStatus.Completed;
                    } catch (Exception ex) {
                        trigger.Status = SequenceEntityStatus.Failed;
                        trigger.Error = ex.Message;
                        ctx.Logger.LogWarning(ex, "Trigger {Name} crashed", trigger.Name);
                    } finally {
                        trigger.FinishedAt = DateTime.UtcNow;
                    }
                }
            } catch (OperationCanceledException) { throw; }
        }
    }

    /// <summary>True while every condition's <c>StillTrueAsync</c> returns true.</summary>
    protected async Task<bool> AllConditionsHoldAsync(SequenceContext ctx, CancellationToken ct) {
        foreach (var c in Conditions) {
            if (!await c.StillTrueAsync(ctx, ct)) return false;
        }
        return true;
    }

    /// <summary>
    /// Runs one child step with its retry <c>Attempts</c> and failure policy
    /// (<see cref="InstructionErrorBehavior"/>), sets its runtime status, and
    /// cascades this container's effective triggers into the child if the child
    /// is itself a container. Returns whether the caller should keep going or
    /// stop the rest of this container.
    /// </summary>
    protected async Task<ChildOutcome> RunChildAsync(ISequenceEntity item, SequenceContext ctx, CancellationToken ct) {
        var eh = item as IErrorHandlingEntity;
        var attempts = Math.Max(1, eh?.Attempts ?? 1);
        var behavior = eh?.ErrorBehavior ?? InstructionErrorBehavior.AbortRun;

        // Push ancestor + own triggers down so a flip/AF/dither defined on this
        // (or a higher) container fires between the child's own steps too.
        if (item is SequenceContainer childContainer)
            childContainer.InheritedTriggers = EffectiveTriggers();

        Exception? last = null;
        for (int attempt = 1; attempt <= attempts; attempt++) {
            ct.ThrowIfCancellationRequested();
            if (item is SequenceEntityBase rb) rb.ResetRuntimeState();
            item.Status = SequenceEntityStatus.Running;
            item.StartedAt = DateTime.UtcNow;
            try {
                await item.ExecuteAsync(ctx, ct);
                item.Status = SequenceEntityStatus.Completed;
                item.FinishedAt = DateTime.UtcNow;
                return ChildOutcome.Continue;
            } catch (OperationCanceledException) {
                item.Status = SequenceEntityStatus.Skipped;
                item.FinishedAt = DateTime.UtcNow;
                throw;
            } catch (Exception ex) {
                last = ex;
                item.Error = ex.Message;
                item.FinishedAt = DateTime.UtcNow;
                if (attempt < attempts) {
                    ctx.Logger.LogWarning(ex, "Step {Name} failed (attempt {Attempt}/{Max}); retrying", item.Name, attempt, attempts);
                    if (RetryBackoff > TimeSpan.Zero) {
                        try { await Task.Delay(RetryBackoff, ct); }
                        catch (OperationCanceledException) { item.Status = SequenceEntityStatus.Skipped; throw; }
                    }
                }
            }
        }

        // Out of attempts — apply the failure policy.
        item.Status = SequenceEntityStatus.Failed;
        switch (behavior) {
            case InstructionErrorBehavior.ContinueOnError:
                ctx.Logger.LogWarning(last, "Step {Name} failed after {Max} attempt(s); continuing (ContinueOnError)", item.Name, attempts);
                return ChildOutcome.Continue;
            case InstructionErrorBehavior.SkipBlock:
                ctx.Logger.LogWarning(last, "Step {Name} failed after {Max} attempt(s); skipping rest of '{Container}' (SkipBlock)", item.Name, attempts, Name);
                return ChildOutcome.StopContainer;
            default:
                throw last ?? new InvalidOperationException($"Step {item.Name} failed");
        }
    }

    /// <summary>
    /// Shared sequential body: evaluate triggers before each child, run each
    /// child with retry/error-policy, honour <see cref="IsLoop"/> + conditions.
    /// Used by both <c>SequentialContainer</c> and <c>DeepSkyObjectContainer</c>.
    /// </summary>
    protected async Task RunChildrenSequentialAsync(SequenceContext ctx, CancellationToken ct) {
        if (IsLoop && Conditions.Count == 0)
            ctx.Logger.LogInformation(
                "Container '{Name}' loops with no exit condition; "
                + "it will repeat until the sequence is stopped.", Name);
        do {
            for (int i = 0; i < Items.Count; i++) {
                if (ctx.AbortRequested) return;
                ct.ThrowIfCancellationRequested();

                await EvaluateTriggersAsync(ctx, ct);
                if (ctx.AbortRequested) return;

                if (await RunChildAsync(Items[i], ctx, ct) == ChildOutcome.StopContainer)
                    return;
            }
        } while (IsLoop && !ctx.AbortRequested && await AllConditionsHoldAsync(ctx, ct));
    }
}

/// <summary>Atomic action, does one thing and returns.</summary>
public abstract class SequenceInstruction : SequenceEntityBase, IErrorHandlingEntity {
    // Subclasses live in Sequencer/Instructions/*.

    /// <summary>How many times to attempt this instruction before applying
    /// <see cref="ErrorBehavior"/>. Default 1 (no retry).</summary>
    public int Attempts { get; set; } = 1;

    /// <summary>What to do once all <see cref="Attempts"/> have failed.
    /// Default <see cref="InstructionErrorBehavior.AbortRun"/> preserves the
    /// historical "one failure stops the run" behavior unless the user opts in.</summary>
    public InstructionErrorBehavior ErrorBehavior { get; set; } = InstructionErrorBehavior.AbortRun;
}

/// <summary>Boolean predicate used by containers / loops to decide whether to keep going.</summary>
public abstract class SequenceCondition : SequenceEntityBase {
    /// <summary>Returns true while the loop should keep running.</summary>
    public abstract Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct);

    /// <summary>
    /// Conditions don't execute as standalone steps, the container consults
    /// them. Implement no-op here so subclasses don't have to.
    /// </summary>
    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Event-based hook polled between child steps. <see cref="ShouldFireAsync"/>
/// decides; <see cref="ExecuteAsync"/> runs the side-effect (auto-focus,
/// dither, meridian flip, …).
/// </summary>
public abstract class SequenceTrigger : SequenceEntityBase {
    public abstract Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct);
}