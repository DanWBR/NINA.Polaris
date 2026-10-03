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

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services.Bge;

/// <summary>What happened to one frame, for the operator and the status feed.</summary>
/// <param name="Pixels">The corrected frame, or the input unchanged when no
/// background was available.</param>
/// <param name="Corrected">False means this sub went into the stack with its
/// gradient intact. It is counted, not hidden.</param>
/// <param name="BackgroundAgeFrames">How many frames old the model is, so the
/// operator can see the cadence working.</param>
/// <param name="Producer">Where the model ran, or null when there is none.</param>
public readonly record struct BgeFrameResult(
    ushort[] Pixels, bool Corrected, int BackgroundAgeFrames, string? Producer);

/// <summary>
/// Per-frame background correction for live stacking.
///
/// The shape of the thing: **subtract on every frame, infer rarely.** A light
/// pollution gradient is optics plus sky in sensor coordinates and barely moves
/// between subs, so the model runs once every <see cref="RecomputeEveryFrames"/>
/// frames while every sub still gets corrected. On an accelerator the forward
/// pass is about a tenth of a second and this hardly matters; through the
/// browser, or on a board with no accelerator, it is the difference between
/// usable and not.
///
/// **The host never waits for a producer.** A request is started and the frame
/// is corrected with whatever background is already in hand; when the answer
/// turns up it is adopted for the next frame. That matters because
/// <c>LiveStackingService.AddFrameAsync</c> is awaited by the capture loop, so
/// anything blocking in here is time taken out of the session. It is also why
/// the first subs of a session are stacked uncorrected and counted rather than
/// held: a stacked sub with a gradient is worth more than a missing one.
///
/// What this replaces: a full-frame FITS written to a temporary file, a GraXpert
/// subprocess (a venv Python interpreter on an SBC), and another FITS read back,
/// per sub. About 100 MB of file traffic on a 26 MP frame, and seconds to tens
/// of seconds of a session spent per frame.
/// </summary>
public sealed class BgeFrameCorrector {

    private readonly IBgeModelProducer _producer;
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    private BgeBackground? _current;
    private Task<float[]?>? _inFlight;
    private InFlightContext? _inFlightFor;
    private bool _warnedCannotRun;

    private sealed record InFlightContext(
        int Tile, int Width, int Height, int Channels, string? Filter,
        long StartedOnFrame, long StartedTicks);

    public BgeFrameCorrector(IBgeModelProducer producer, ILogger? logger = null) {
        _producer = producer;
        _logger = logger;
    }

    /// <summary>How many frames a background serves before a new one is asked
    /// for. 1 means every frame.</summary>
    public int RecomputeEveryFrames { get; set; } = 10;

    /// <summary>"Subtraction" or "Division", as the rig's setting says.</summary>
    public string Correction { get; set; } = "Subtraction";

    /// <summary>The model window. 256 for every GraXpert background model.</summary>
    public int Tile { get; set; } = 256;

    /// <summary>The background currently in hand, for the status feed.</summary>
    public BgeBackground? Current { get { lock (_lock) return _current; } }

    /// <summary>Subs stacked without a correction, cumulative for the session.
    /// Not an error count: the common cause is the first few frames of a
    /// session, before any model has come back.</summary>
    public int FramesUncorrected { get; private set; }

    /// <summary>Forget the model. Called when the session restarts, or when the
    /// operator changes something the gradient depends on.</summary>
    public void Reset() {
        lock (_lock) {
            _current = null;
            _inFlight = null;
            _inFlightFor = null;
            _warnedCannotRun = false;
        }
        FramesUncorrected = 0;
    }

    /// <summary>
    /// Correct one sub, and start a new model request if one is due.
    ///
    /// <paramref name="pixels"/> is plane-sequential and is never modified; the
    /// corrected frame is a new array, which is what the caller's copy-on-write
    /// discipline around the relay and the writer expects.
    /// </summary>
    public BgeFrameResult Apply(ushort[] pixels, int width, int height, int channels,
                                long frameIndex, string? filter) {
        AdoptCompletedRequest(width, height, channels, filter);

        var have = Current;
        bool valid = have != null && have.MatchesGeometry(width, height, channels, filter);
        bool due = !valid
                   || frameIndex - have!.ComputedOnFrame >= Math.Max(1, RecomputeEveryFrames);

        // Build the tensor only when a request is actually going out, and keep
        // its statistics: on that frame they are the frame's own, so there is no
        // reason to measure twice.
        BgeChannelStats[]? statsFromRequest = null;
        if (due) statsFromRequest = StartRequest(pixels, width, height, channels, frameIndex, filter);

        if (!valid) {
            FramesUncorrected++;
            return new BgeFrameResult(pixels, false, 0, null);
        }

        // The reused half: the shape comes from the model, the level from THIS
        // frame. Denormalising a held background with the median it was computed
        // against is what would let the correction drift as the sky brightens.
        var stats = statsFromRequest
                    ?? BgeTensor.StatsOnly(pixels, width, height, channels, have!.Tile);

        var corrected = BgeApply.Correct(pixels, width, height, channels,
            have!.Output, stats, have.Tile, Correction, saveBackground: false, out _);

        return new BgeFrameResult(corrected, true,
            (int)Math.Max(0, frameIndex - have.ComputedOnFrame), have.Producer);
    }

    /// <summary>Take the result of a finished request, when it still describes
    /// the frames being stacked. A request whose geometry no longer matches is
    /// dropped: the operator changed binning or filter while it was out.</summary>
    private void AdoptCompletedRequest(int width, int height, int channels, string? filter) {
        Task<float[]?>? task;
        InFlightContext? ctx;
        lock (_lock) {
            task = _inFlight;
            ctx = _inFlightFor;
            if (task == null || !task.IsCompleted) return;
            _inFlight = null;
            _inFlightFor = null;
        }

        float[]? output = null;
        if (task.IsCompletedSuccessfully) output = task.Result;
        else if (task.IsFaulted)
            _logger?.LogWarning(task.Exception?.GetBaseException(),
                "Background extraction request failed on the {Where} path", _producer.Name);

        if (output == null || ctx == null) return;

        if (ctx.Width != width || ctx.Height != height || ctx.Channels != channels
                || !string.Equals(ctx.Filter ?? "", filter ?? "", StringComparison.OrdinalIgnoreCase)) {
            _logger?.LogInformation(
                "Discarding a background model: it was computed for {W}x{H}c{C} and the frames are now {W2}x{H2}c{C2}",
                ctx.Width, ctx.Height, ctx.Channels, width, height, channels);
            return;
        }
        if (output.Length != ctx.Tile * ctx.Tile * 3) {
            _logger?.LogWarning("Background model has {N} values, expected {M}; discarding",
                output.Length, ctx.Tile * ctx.Tile * 3);
            return;
        }

        var elapsed = (Stopwatch.GetTimestamp() - ctx.StartedTicks) * 1000.0 / Stopwatch.Frequency;
        lock (_lock) {
            _current = new BgeBackground(output, ctx.Tile, ctx.Width, ctx.Height, ctx.Channels,
                ctx.Filter, ctx.StartedOnFrame, _producer.Name, elapsed);
        }
        _logger?.LogInformation(
            "Background model from the {Where} path in {Ms:F0} ms (frame {Frame})",
            _producer.Name, elapsed, ctx.StartedOnFrame);
    }

    /// <summary>Start one request, at most one at a time, and return the
    /// statistics measured while building its input.</summary>
    private BgeChannelStats[]? StartRequest(ushort[] pixels, int width, int height, int channels,
                                            long frameIndex, string? filter) {
        lock (_lock) {
            if (_inFlight != null) return null;      // one in flight is enough
        }
        if (!_producer.CanRun) {
            if (!_warnedCannotRun) {
                _warnedCannotRun = true;
                _logger?.LogInformation(
                    "Background extraction has nowhere to run ({Where} is not ready); "
                    + "subs are being stacked uncorrected and counted", _producer.Name);
            }
            return null;
        }

        var (tensor, stats) = BgeTensor.Build(pixels, width, height, channels, Tile);
        var ctx = new InFlightContext(Tile, width, height, channels, filter,
            frameIndex, Stopwatch.GetTimestamp());
        lock (_lock) {
            _inFlightFor = ctx;
            // Detached on purpose: the frame that asked for this is not going to
            // wait for it, and neither is the capture loop behind it.
            _inFlight = Task.Run(() => _producer.RunAsync(tensor, Tile, CancellationToken.None));
            _warnedCannotRun = false;
        }
        return stats;
    }
}
