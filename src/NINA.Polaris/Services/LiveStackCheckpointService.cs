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

namespace NINA.Polaris.Services;

/// <summary>
/// Partial-stack checkpoints. Subscribes to the live-stack frame stream and,
/// on a per-rig cadence (every N frames and/or every N minutes of integration),
/// writes a MASTER FITS of the current running stack into a dedicated
/// <c>checkpoints/</c> folder — a safety net against a crash mid-session, and a
/// persisted "quality vs integration time" record: each checkpoint is logged in
/// the in-memory manifest with the frame, elapsed time, cumulative SNR and HFR
/// that produced it.
///
/// Cadence config lives on <see cref="LiveStackTriggers"/>
/// (<c>CheckpointEveryNFrames</c> / <c>CheckpointEveryMinutes</c> /
/// <c>CheckpointKeepLast</c>). The frame handler is awaited sequentially inside
/// <see cref="LiveStackingService.AddFrameAsync"/>, so a checkpoint write pauses
/// the capture loop briefly, exactly like "save each frame".
/// </summary>
public sealed class LiveStackCheckpointService : IDisposable {
    private readonly LiveStackingService _stack;
    private readonly ProfileService _profiles;
    private readonly ImageWriterService _writer;
    private readonly ILogger<LiveStackCheckpointService> _logger;
    private readonly IDisposable _frameSub;
    private readonly object _lock = new();

    /// <summary>One saved checkpoint. Mirrors a <see cref="LiveStackingService.LiveStackQualitySample"/>
    /// but points at the FITS on disk.</summary>
    public sealed record CheckpointEntry(
        int Frame, double ElapsedSec, double CumulativeSnr, double MedianHfr,
        string Path, System.DateTime At);

    private readonly List<CheckpointEntry> _manifest = new();
    private int _lastCheckpointFrame;
    private double _lastCheckpointElapsedSec;
    private int _lastSeenFrame;
    private volatile bool _isSaving;

    public LiveStackCheckpointService(LiveStackingService stack,
                                      ProfileService profiles,
                                      ImageWriterService writer,
                                      ILogger<LiveStackCheckpointService> logger) {
        _stack = stack;
        _profiles = profiles;
        _writer = writer;
        _logger = logger;
        _frameSub = _stack.SubscribeFrameIntegrated(OnFrameIntegratedAsync);
        // Fresh slate per rig — a rig switch starts a new session.
        _profiles.EquipmentProfileActivated += _ => ResetState();
    }

    /// <summary>Snapshot of the checkpoint manifest for this session.</summary>
    public IReadOnlyList<CheckpointEntry> Manifest {
        get { lock (_lock) return _manifest.ToArray(); }
    }

    /// <summary>Clear the gate + manifest (called on rig switch and on a
    /// stack reset detected via the frame counter going backwards).</summary>
    public void ResetState() {
        lock (_lock) {
            _manifest.Clear();
            _lastCheckpointFrame = 0;
            _lastCheckpointElapsedSec = 0;
            _lastSeenFrame = 0;
        }
    }

    private Task OnFrameIntegratedAsync(LiveStackFrameInfo info) {
        try {
            // A Reset() (or a new Start) rewinds the frame counter; treat any
            // non-advancing count as the start of a new session.
            if (info.FrameCount <= _lastSeenFrame) ResetState();
            _lastSeenFrame = info.FrameCount;

            var cfg = _profiles.ActiveEquipmentProfile?.LiveStackTriggers;
            if (cfg == null) return Task.CompletedTask;
            int everyN = cfg.CheckpointEveryNFrames;
            int everyMin = cfg.CheckpointEveryMinutes;
            if (everyN <= 0 && everyMin <= 0) return Task.CompletedTask;
            if (_isSaving) return Task.CompletedTask;

            double elapsed = _stack.ElapsedSeconds;
            bool frameGate = everyN > 0 && info.FrameCount - _lastCheckpointFrame >= everyN;
            bool timeGate = everyMin > 0 && (elapsed - _lastCheckpointElapsedSec) >= everyMin * 60.0;
            if (!frameGate && !timeGate) return Task.CompletedTask;

            SaveCheckpoint(info, elapsed, cfg.CheckpointKeepLast);
        } catch (System.Exception ex) {
            _logger.LogWarning(ex, "Live-stack checkpoint: frame handler failed (non-fatal)");
        }
        return Task.CompletedTask;
    }

    private void SaveCheckpoint(LiveStackFrameInfo info, double elapsed, int keepLast) {
        if (!_writer.HasOutputDir) return;   // save-frames warning already covers this
        _isSaving = true;
        try {
            var image = _stack.GetCurrentStackImage();
            if (image == null) return;
            var path = _writer.SaveImage(image, imageType: "MASTER", gain: 0,
                                         stacked: true, stackedFolderName: "checkpoints");
            if (path == null) return;

            _lastCheckpointFrame = info.FrameCount;
            _lastCheckpointElapsedSec = elapsed;
            lock (_lock) {
                _manifest.Add(new CheckpointEntry(
                    info.FrameCount, elapsed, info.CumulativeSnr, info.MedianHfr,
                    path, info.At));
                PruneLocked(keepLast);
            }
            _logger.LogInformation(
                "Live-stack checkpoint: frame {Frame}, {Elapsed:F0}s, SNR {Snr:F1} -> {Path}",
                info.FrameCount, elapsed, info.CumulativeSnr, path);
        } finally {
            _isSaving = false;
        }
    }

    // Keep only the most recent keepLast checkpoints on disk; delete the files
    // of the pruned ones (best-effort). keepLast <= 0 keeps all. Caller holds _lock.
    private void PruneLocked(int keepLast) {
        if (keepLast <= 0) return;
        while (_manifest.Count > keepLast) {
            var oldest = _manifest[0];
            _manifest.RemoveAt(0);
            try {
                if (System.IO.File.Exists(oldest.Path)) System.IO.File.Delete(oldest.Path);
            } catch (System.Exception ex) {
                _logger.LogDebug(ex, "Live-stack checkpoint: could not prune {Path}", oldest.Path);
            }
        }
    }

    public void Dispose() => _frameSub?.Dispose();
}
