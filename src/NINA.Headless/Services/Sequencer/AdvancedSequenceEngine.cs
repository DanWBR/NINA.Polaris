using NINA.Headless.Services.Sequencer.Containers;

namespace NINA.Headless.Services.Sequencer;

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

    public SequenceDocument Document { get; private set; } = new();
    public AdvancedSequenceState State { get; private set; } = AdvancedSequenceState.Idle;
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

    public IReadOnlyList<string> Validate() => Document.Root.Validate();

    public void Start() {
        if (State == AdvancedSequenceState.Running) return;
        var errors = Validate();
        if (errors.Count > 0) {
            LastError = "Validation failed: " + string.Join("; ", errors);
            _logger.LogWarning("Refusing to start: {Errors}", LastError);
            return;
        }

        _cts = new CancellationTokenSource();
        State = AdvancedSequenceState.Running;
        StartedAt = DateTime.UtcNow;
        FinishedAt = null;
        AbortReason = null;
        LastError = null;
        ResetTree(Document.Root);

        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop() {
        _cts?.Cancel();
        State = AdvancedSequenceState.Idle;
    }

    private async Task RunAsync(CancellationToken ct) {
        // Build a fresh context from DI — pulls in whatever services are alive
        // right now (so a profile switch mid-run takes effect on the next run).
        SequenceContext ctx;
        try {
            ctx = BuildContext();
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
        if (entity is SequenceEntityBase b) b.ResetRuntimeState();
        if (entity is SequenceContainer container) {
            foreach (var child in container.Items) ResetTree(child);
        }
    }
}

public enum AdvancedSequenceState { Idle, Running }
