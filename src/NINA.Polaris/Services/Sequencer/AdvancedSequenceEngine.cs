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

using NINA.Polaris.Services.Sequencer.Containers;

namespace NINA.Polaris.Services.Sequencer;

/// <summary>
/// Runtime host for the Advanced Sequencer tree. Owns the current
/// <see cref="Document"/>, builds a fresh <see cref="SequenceContext"/>
/// from DI on Start, and drives the root entity to completion.
///
/// Designed to coexist with the legacy <see cref="SequenceEngine"/>; the
/// C7 settings toggle picks which one is "active" from the UI's point
/// of view. Both can be in the DI container at the same time.
/// </summary>
public class AdvancedSequenceEngine {
    private readonly IServiceProvider _services;
    private readonly ILogger<AdvancedSequenceEngine> _logger;
    private readonly SequenceTemplateStore _templates;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private SequenceContext? _ctx;

    public SequenceDocument Document { get; private set; } = new();
    public AdvancedSequenceState State { get; private set; } = AdvancedSequenceState.Idle;

    /// <summary>Total light frames captured by the current/last run's context
    /// (the global counter <see cref="TakeExposureInstruction"/> bumps per
    /// frame). A fresh context is built on each <see cref="Start"/>, so this
    /// resets to 0 when a new run begins. Used by PLAN-mode progress.</summary>
    public int FramesCompleted => _ctx?.FramesCompleted ?? 0;
    public string? LastError { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }

    /// <summary>Set while the run is in progress (so the UI can show abort reason).</summary>
    public string? AbortReason { get; private set; }

    public AdvancedSequenceEngine(IServiceProvider services, SequenceTemplateStore templates,
        ILogger<AdvancedSequenceEngine> logger) {
        _services = services;
        _templates = templates;
        _logger = logger;
    }

    public void Load(SequenceDocument doc) {
        if (State == AdvancedSequenceState.Running)
            throw new InvalidOperationException("Cannot load while running");
        Document = doc;
        HydrateTemplates(doc.Root);
        LastError = null;
        StartedAt = null;
        FinishedAt = null;
        AbortReason = null;
        ResetTree(doc.Root);
        _logger.LogInformation("Advanced sequence loaded: {Name} (v{Version})", doc.Name, doc.Version);
    }

    /// <summary>
    /// Re-attach a document WITHOUT clearing its runtime state, so a
    /// subsequent <see cref="Start"/> with <c>resume=true</c> continues where
    /// the document's previous run stopped. Used by PLAN resume, which stashes
    /// the main document before the end-actions document replaces it.
    /// </summary>
    public void LoadForResume(SequenceDocument doc) {
        if (State == AdvancedSequenceState.Running)
            throw new InvalidOperationException("Cannot load while running");
        Document = doc;
        LastError = null;
        AbortReason = null;
        _logger.LogInformation("Advanced sequence re-attached for resume: {Name}", doc.Name);
    }

    /// <summary>
    /// True when the loaded document carries partial progress a resumed start
    /// can continue from: the tree ran but didn't complete, and at least one
    /// entity finished (or captured part of its frame set). Purely in-memory —
    /// a server restart clears it.
    /// </summary>
    public bool HasResumableProgress =>
        State == AdvancedSequenceState.Idle
        && Document.Root.Status is SequenceEntityStatus.Skipped
            or SequenceEntityStatus.Failed
            or SequenceEntityStatus.Running
        && HasAnyProgress(Document.Root);

    private static bool HasAnyProgress(ISequenceEntity entity) {
        if (entity is Instructions.TakeExposureInstruction tx && tx.CompletedCount > 0)
            return true;
        if (entity is SequenceContainer c) {
            foreach (var child in c.Items) {
                if (child.Status == SequenceEntityStatus.Completed) return true;
                if (HasAnyProgress(child)) return true;
            }
        }
        return false;
    }

    public IReadOnlyList<string> Validate() => Document.Root.Validate();

    public void Start() => Start(resume: false);

    /// <summary>
    /// Start the loaded document. With <paramref name="resume"/> true and
    /// retained partial progress, the tree is NOT reset: top-level entities
    /// already Completed are skipped, the interrupted one re-runs (its setup
    /// instructions repeat — the mount may have moved/parked meanwhile) and
    /// TakeExposure instructions fast-forward past frames already captured.
    /// </summary>
    public void Start(bool resume) {
        if (State == AdvancedSequenceState.Running) return;
        // Guard the restart race: Stop() only requests cancellation, so a
        // previous run may still be unwinding (an instruction mid-flight that
        // hasn't observed the token yet). Starting now would let two runs
        // mutate the same Document.Root concurrently.
        if (_runTask is { IsCompleted: false }) {
            LastError = "Previous run is still stopping; try again in a moment.";
            return;
        }
        var errors = Validate();
        if (errors.Count > 0) {
            LastError = "Validation failed: " + string.Join("; ", errors);
            _logger.LogWarning("Refusing to start: {Errors}", LastError);
            return;
        }

        bool doResume = resume && HasResumableProgress;

        _cts = new CancellationTokenSource();
        State = AdvancedSequenceState.Running;
        StartedAt = DateTime.UtcNow;
        FinishedAt = null;
        AbortReason = null;
        LastError = null;
        if (!doResume) ResetTree(Document.Root);
        else _logger.LogInformation("Advanced sequence resuming from retained progress");

        _runTask = Task.Run(() => RunAsync(_cts.Token, doResume));
    }

    public void Stop() {
        // Only request cancellation. The run task's finally clause flips State
        // back to Idle once it has actually wound down, so State stays truthful
        // ("Running" until the in-flight instruction observes the token) and
        // Start() can't kick off a second run on top of a live one.
        _cts?.Cancel();
    }

    private async Task RunAsync(CancellationToken ct, bool isResume = false) {
        // Build a fresh context from DI, pulls in whatever services are alive
        // right now (so a profile switch mid-run takes effect on the next run).
        SequenceContext ctx;
        try {
            ctx = BuildContext();
            ctx.IsResume = isResume;
            _ctx = ctx;   // expose FramesCompleted for PLAN-mode progress
        } catch (Exception ex) {
            LastError = "DI build failed: " + ex.Message;
            State = AdvancedSequenceState.Idle;
            FinishedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Failed to build SequenceContext");
            return;
        }

        try {
            Document.Root.Status = SequenceEntityStatus.Running;
            Document.Root.StartedAt = DateTime.UtcNow;
            await Document.Root.ExecuteAsync(ctx, ct);
            Document.Root.Status = ctx.AbortRequested ? SequenceEntityStatus.Skipped : SequenceEntityStatus.Completed;
            AbortReason = ctx.AbortReason;
        } catch (OperationCanceledException) {
            Document.Root.Status = SequenceEntityStatus.Skipped;
            _logger.LogInformation("Sequence cancelled");
        } catch (Exception ex) {
            Document.Root.Status = SequenceEntityStatus.Failed;
            Document.Root.Error = ex.Message;
            LastError = ex.Message;
            _logger.LogError(ex, "Sequence failed");
        } finally {
            Document.Root.FinishedAt = DateTime.UtcNow;
            FinishedAt = DateTime.UtcNow;
            State = AdvancedSequenceState.Idle;
        }
    }

    private SequenceContext BuildContext() {
        return new SequenceContext(
            equipment: _services.GetRequiredService<EquipmentManager>(),
            relay: _services.GetRequiredService<ImageRelayService>(),
            liveStack: _services.GetRequiredService<LiveStackingService>(),
            phd2: _services.GetRequiredService<PHD2Client>(),
            autoFocus: _services.GetRequiredService<AutoFocusService>(),
            meridianFlip: _services.GetRequiredService<MeridianFlipService>(),
            plateSolver: _services.GetRequiredService<PlateSolveService>(),
            slewCenter: _services.GetRequiredService<SlewCenterService>(),
            imageWriter: _services.GetRequiredService<ImageWriterService>(),
            profiles: _services.GetRequiredService<ProfileService>(),
            captureProgress: _services.GetRequiredService<CaptureProgressService>(),
            logger: _logger);
    }

    private void HydrateTemplates(ISequenceEntity entity) {
        if (entity is TemplatedContainer tc && !string.IsNullOrWhiteSpace(tc.TemplateName)) {
            var template = _templates.Load(tc.TemplateName);
            if (template?.Root is SequenceContainer sc) {
                tc.Items = new List<ISequenceEntity>(sc.Items);
                tc.Triggers = new List<SequenceTrigger>(sc.Triggers);
                tc.Conditions = new List<SequenceCondition>(sc.Conditions);
            } else {
                _logger.LogWarning("Template '{Name}' not found or root is not a container", tc.TemplateName);
            }
        }
        if (entity is SequenceContainer container) {
            foreach (var child in container.Items) HydrateTemplates(child);
        }
    }

    private void ResetTree(ISequenceEntity entity) {
        if (entity is SequenceEntityBase b) {
            b.ResetRuntimeState();
            // Fresh start / load also clears capture progress (TakeExposure's
            // completed-frame counter). ResetRuntimeState deliberately keeps
            // it (retries + resume rely on that), so clear it explicitly here.
            b.ResetProgress();
        }
        if (entity is SequenceContainer container) {
            foreach (var child in container.Items) ResetTree(child);
        }
    }
}

public enum AdvancedSequenceState { Idle, Running }