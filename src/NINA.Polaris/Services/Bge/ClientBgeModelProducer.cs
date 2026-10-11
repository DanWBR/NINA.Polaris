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

using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services.Bge;

/// <summary>
/// The background model computed in the operator's browser, which has a GPU and
/// ONNX Runtime Web, and is where every other AI operation in this project
/// already runs.
///
/// This is the path that makes background extraction usable on a board with no
/// accelerator: a Raspberry Pi, an Allwinner Orange Pi, anything where neither
/// a Rockchip NPU nor a Hexagon nor a working Vulkan stack exists. What crosses
/// the network is the 256x256x3 tensor and the same-sized answer, a few hundred
/// kilobytes each way whatever the sensor, because the model window is fixed and
/// the frame-sized arithmetic stays on the host.
///
/// <b>One job at a time, and the host never waits.</b> A request parks a job;
/// the browser collects it, runs it and posts the answer back. If no browser
/// does, the job expires and the frame was stacked uncorrected and counted long
/// before. Client-driven live stacking was removed from this project for good
/// reasons (a hidden tab deferred a loop and a retry that then fired together,
/// two loops fighting over one camera), and none of them apply here: there is no
/// capability handshake, no watchdog, no mode evaluator, the host owns the stack
/// throughout, and the worst a stalled browser can do is nothing at all.
/// </summary>
public sealed class ClientBgeModelProducer : IBgeModelProducer {

    private readonly ILogger<ClientBgeModelProducer>? _logger;
    private readonly object _lock = new();

    private Job? _job;
    private DateTime _lastClientPollUtc = DateTime.MinValue;

    private sealed class Job {
        public required string Id { get; init; }
        public required float[] Tensor { get; init; }
        public required int Tile { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public required TaskCompletionSource<float[]?> Completion { get; init; }
        public bool Collected { get; set; }
    }

    public ClientBgeModelProducer(ILogger<ClientBgeModelProducer>? logger = null) {
        _logger = logger;
    }

    public string Name => "client";

    /// <summary>How long a browser's last visit counts as "there is a browser".
    /// The poll doubles as the heartbeat, so this is the only presence signal
    /// needed and there is no handshake to get out of step.</summary>
    public TimeSpan ClientPresenceWindow { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long an uncollected or unanswered job stays interesting. A
    /// browser that went away mid-job must not leave the corrector believing a
    /// model is on the way for the rest of the night.</summary>
    public TimeSpan JobTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>True when a browser has asked for work recently. False is the
    /// ordinary state with no tab open, and costs the session nothing: subs are
    /// stacked with their gradient and counted.</summary>
    public bool CanRun {
        get {
            lock (_lock) return DateTime.UtcNow - _lastClientPollUtc <= ClientPresenceWindow;
        }
    }

    /// <summary>True when a job is waiting to be collected. Rides the live-stack
    /// status block so the browser can come and get it at once instead of
    /// polling for something that is usually not there.</summary>
    public bool JobPending {
        get {
            lock (_lock) {
                ExpireLocked();
                return _job is { Collected: false };
            }
        }
    }

    public Task<float[]?> RunAsync(float[] nhwcTensor, int tile, CancellationToken ct) {
        var tcs = new TaskCompletionSource<float[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) {
            ExpireLocked();
            if (_job != null) {
                // One at a time. The corrector does not ask twice, so this is a
                // belt-and-braces refusal rather than a queue.
                return Task.FromResult<float[]?>(null);
            }
            _job = new Job {
                Id = Guid.NewGuid().ToString("N"),
                Tensor = nhwcTensor,
                Tile = tile,
                CreatedUtc = DateTime.UtcNow,
                Completion = tcs,
            };
        }
        return tcs.Task;
    }

    /// <summary>The browser's GET. Also the presence heartbeat: asking is what
    /// makes <see cref="CanRun"/> true, which is why a tab that has just opened
    /// does not have to announce itself any other way.</summary>
    public bool TryTakeJob(out string id, out float[] tensor, out int tile) {
        lock (_lock) {
            _lastClientPollUtc = DateTime.UtcNow;
            ExpireLocked();
            if (_job is { Collected: false } job) {
                job.Collected = true;
                id = job.Id;
                tensor = job.Tensor;
                tile = job.Tile;
                return true;
            }
        }
        id = ""; tensor = Array.Empty<float>(); tile = 0;
        return false;
    }

    /// <summary>The browser's POST. An id that is not the job in hand is
    /// ignored: it is an answer to a job that has already expired, and applying
    /// it would put a background from a previous geometry into the stack.</summary>
    public bool TryCompleteJob(string id, float[] output) {
        Job? job;
        lock (_lock) {
            _lastClientPollUtc = DateTime.UtcNow;
            if (_job == null || !string.Equals(_job.Id, id, StringComparison.Ordinal)) {
                _logger?.LogInformation(
                    "Ignoring a background model for job {Id}: it is not the one outstanding", id);
                return false;
            }
            if (output.Length != _job.Tile * _job.Tile * 3) {
                _logger?.LogWarning("Background model for job {Id} has {N} values, expected {M}",
                    id, output.Length, _job.Tile * _job.Tile * 3);
                return false;
            }
            job = _job;
            _job = null;
        }
        job.Completion.TrySetResult(output);
        return true;
    }

    /// <summary>Drop the outstanding job, if any. Called when the session stops
    /// or the operator switches away from the client path.</summary>
    public void Reset() {
        Job? job;
        lock (_lock) { job = _job; _job = null; }
        job?.Completion.TrySetResult(null);
    }

    /// <summary>A job nobody came for, or nobody answered, stops being
    /// interesting. Completing it with null is what lets the corrector ask
    /// again on a later frame instead of waiting forever.</summary>
    private void ExpireLocked() {
        if (_job == null) return;
        if (DateTime.UtcNow - _job.CreatedUtc <= JobTtl) return;
        var job = _job;
        _job = null;
        _logger?.LogInformation(
            "Background model job {Id} expired after {Sec:F0}s {State}", job.Id, JobTtl.TotalSeconds,
            job.Collected ? "with no answer from the browser" : "uncollected");
        job.Completion.TrySetResult(null);
    }
}
