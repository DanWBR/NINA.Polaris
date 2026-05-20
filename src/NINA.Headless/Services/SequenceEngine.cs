using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services;

public class SequenceEngine {
    private readonly EquipmentManager _equip;
    private readonly ImageRelayService _relay;
    private readonly LiveStackingService _liveStack;
    private readonly ILogger<SequenceEngine> _logger;

    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private Task? _runTask;

    public List<SequenceItem> Items { get; private set; } = [];
    public SequenceState State { get; private set; } = SequenceState.Idle;
    public int CurrentItemIndex { get; private set; } = -1;
    public int CurrentFrameInItem { get; private set; }
    public int TotalFramesCompleted { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? StartedAt { get; private set; }

    public SequenceEngine(EquipmentManager equip, ImageRelayService relay,
        LiveStackingService liveStack, ILogger<SequenceEngine> logger) {
        _equip = equip;
        _relay = relay;
        _liveStack = liveStack;
        _logger = logger;
    }

    public void LoadSequence(List<SequenceItem> items) {
        if (State == SequenceState.Running)
            throw new InvalidOperationException("Cannot load sequence while running");

        Items = items;
        CurrentItemIndex = -1;
        CurrentFrameInItem = 0;
        TotalFramesCompleted = 0;
        LastError = null;
        State = SequenceState.Idle;
        _logger.LogInformation("Sequence loaded: {Count} items, {Frames} total frames",
            items.Count, items.Sum(i => i.Count));
    }

    public void Start() {
        if (State == SequenceState.Running) return;

        if (Items.Count == 0) {
            LastError = "No items in sequence";
            return;
        }

        _cts = new CancellationTokenSource();
        State = SequenceState.Running;
        StartedAt = DateTime.UtcNow;
        LastError = null;

        if (_pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        _runTask = Task.Run(() => RunAsync(_cts.Token));
        _logger.LogInformation("Sequence started");
    }

    public void Pause() {
        if (State != SequenceState.Running) return;

        if (_pauseGate.CurrentCount > 0)
            _pauseGate.Wait(0);

        State = SequenceState.Paused;
        _logger.LogInformation("Sequence paused at item {Index}, frame {Frame}",
            CurrentItemIndex, CurrentFrameInItem);
    }

    public void Resume() {
        if (State != SequenceState.Paused) return;

        State = SequenceState.Running;
        if (_pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        _logger.LogInformation("Sequence resumed");
    }

    public void Stop() {
        if (State == SequenceState.Idle) return;

        _cts?.Cancel();

        if (State == SequenceState.Paused && _pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        State = SequenceState.Idle;
        _logger.LogInformation("Sequence stopped");
    }

    public SequenceStatus GetStatus() {
        var totalFrames = Items.Sum(i => i.Count);
        var elapsed = StartedAt.HasValue ? DateTime.UtcNow - StartedAt.Value : TimeSpan.Zero;

        double estimatedRemainingSeconds = 0;
        if (TotalFramesCompleted > 0 && totalFrames > TotalFramesCompleted) {
            var avgFrameTime = elapsed.TotalSeconds / TotalFramesCompleted;
            estimatedRemainingSeconds = avgFrameTime * (totalFrames - TotalFramesCompleted);
        }

        return new SequenceStatus {
            State = State.ToString().ToLowerInvariant(),
            Items = Items.Select((item, i) => new SequenceItemStatus {
                Name = item.Name,
                Exposure = item.Exposure,
                Count = item.Count,
                Completed = i < CurrentItemIndex ? item.Count :
                            i == CurrentItemIndex ? CurrentFrameInItem : 0,
                IsActive = i == CurrentItemIndex && State == SequenceState.Running
            }).ToList(),
            CurrentItemIndex = CurrentItemIndex,
            CurrentFrameInItem = CurrentFrameInItem,
            TotalFrames = totalFrames,
            TotalFramesCompleted = TotalFramesCompleted,
            ElapsedSeconds = elapsed.TotalSeconds,
            EstimatedRemainingSeconds = estimatedRemainingSeconds,
            LastError = LastError
        };
    }

    private async Task RunAsync(CancellationToken ct) {
        try {
            for (int i = Math.Max(0, CurrentItemIndex); i < Items.Count; i++) {
                ct.ThrowIfCancellationRequested();
                CurrentItemIndex = i;
                var item = Items[i];

                _logger.LogInformation("Sequence item {Index}/{Total}: {Name} ({Exposure}s x {Count})",
                    i + 1, Items.Count, item.Name, item.Exposure, item.Count);

                // Slew to target if coordinates provided
                if (item.Ra.HasValue && item.Dec.HasValue && _equip.Telescope != null) {
                    _logger.LogInformation("Slewing to {Name} (RA={Ra:F4}, Dec={Dec:F4})",
                        item.Name, item.Ra, item.Dec);

                    try {
                        await _equip.Telescope.SlewAsync(item.Ra.Value, item.Dec.Value, ct);
                        await WaitForSlewComplete(ct);
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Slew failed for {Name}, continuing with capture", item.Name);
                    }
                }

                // Set binning if specified
                if (item.Binning > 0 && _equip.Camera != null) {
                    try {
                        await _equip.Camera.SetBinningAsync(item.Binning, item.Binning, ct);
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Set binning failed");
                    }
                }

                // Capture frames
                int startFrame = (i == Math.Max(0, CurrentItemIndex)) ? CurrentFrameInItem : 0;
                for (int f = startFrame; f < item.Count; f++) {
                    ct.ThrowIfCancellationRequested();

                    // Check pause gate
                    await _pauseGate.WaitAsync(ct);
                    _pauseGate.Release();

                    CurrentFrameInItem = f;

                    if (_equip.Camera == null) {
                        LastError = "No camera connected";
                        _logger.LogError("Sequence aborted: no camera");
                        State = SequenceState.Idle;
                        return;
                    }

                    _logger.LogDebug("Capturing frame {Frame}/{Total} for {Name}",
                        f + 1, item.Count, item.Name);

                    try {
                        var imageData = await _equip.Camera.CaptureAsync(item.Exposure, ct);

                        if (_liveStack.IsRunning) {
                            await _liveStack.AddFrameAsync(imageData, ct);
                        } else {
                            await _relay.RelayImageAsync(imageData, ct);
                        }

                        CurrentFrameInItem = f + 1;
                        TotalFramesCompleted++;
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Frame {Frame} capture failed for {Name}, retrying once",
                            f + 1, item.Name);

                        // Single retry after brief pause
                        try {
                            await Task.Delay(2000, ct);
                            var imageData = await _equip.Camera.CaptureAsync(item.Exposure, ct);

                            if (_liveStack.IsRunning)
                                await _liveStack.AddFrameAsync(imageData, ct);
                            else
                                await _relay.RelayImageAsync(imageData, ct);

                            CurrentFrameInItem = f + 1;
                            TotalFramesCompleted++;
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception retryEx) {
                            _logger.LogError(retryEx, "Retry also failed for frame {Frame}, skipping", f + 1);
                            LastError = $"Frame {f + 1} of {item.Name} failed: {retryEx.Message}";
                        }
                    }
                }

                _logger.LogInformation("Completed item: {Name}", item.Name);
            }

            State = SequenceState.Idle;
            _logger.LogInformation("Sequence completed: {Frames} frames in {Elapsed}",
                TotalFramesCompleted,
                StartedAt.HasValue ? (DateTime.UtcNow - StartedAt.Value).ToString(@"hh\:mm\:ss") : "??");

        } catch (OperationCanceledException) {
            _logger.LogInformation("Sequence cancelled");
        } catch (Exception ex) {
            LastError = ex.Message;
            State = SequenceState.Idle;
            _logger.LogError(ex, "Sequence failed");
        }
    }

    private async Task WaitForSlewComplete(CancellationToken ct) {
        if (_equip.Telescope == null) return;

        for (int i = 0; i < 300; i++) {
            ct.ThrowIfCancellationRequested();
            if (!_equip.Telescope.IsSlewing) return;
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Slew did not complete within 5 minutes");
    }
}

public enum SequenceState { Idle, Running, Paused }

public class SequenceItem {
    public string Name { get; set; } = "";
    public double Exposure { get; set; } = 1.0;
    public int Gain { get; set; } = 100;
    public int Binning { get; set; } = 1;
    public int Count { get; set; } = 1;
    public string? Filter { get; set; }
    public double? Ra { get; set; }
    public double? Dec { get; set; }
}

public class SequenceStatus {
    public string State { get; set; } = "idle";
    public List<SequenceItemStatus> Items { get; set; } = [];
    public int CurrentItemIndex { get; set; }
    public int CurrentFrameInItem { get; set; }
    public int TotalFrames { get; set; }
    public int TotalFramesCompleted { get; set; }
    public double ElapsedSeconds { get; set; }
    public double EstimatedRemainingSeconds { get; set; }
    public string? LastError { get; set; }
}

public class SequenceItemStatus {
    public string Name { get; set; } = "";
    public double Exposure { get; set; }
    public int Count { get; set; }
    public int Completed { get; set; }
    public bool IsActive { get; set; }
}
