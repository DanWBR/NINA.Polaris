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

namespace NINA.Polaris.Services;

/// <summary>Dither parameters, sourced from whichever capture loop owns the
/// dither cadence. Cached by the barrier so the aux camera (which has no dither
/// config of its own) can still drive a dither when it is the slowest.</summary>
public sealed record DitherParams(
    double Pixels = 5.0, bool RaOnly = false, double SettlePixels = 1.5,
    int SettleTime = 10, int SettleTimeout = 40);

/// <summary>
/// In-process barrier that synchronizes dithering across the imaging cameras on
/// a single Polaris host (main + aux today; N-ready by design). Two OTAs on one
/// mount must never have the mount dithered while either is mid-sub, so:
///
/// <list type="number">
/// <item>every imaging capture loop registers as a participant and reports its
/// sub length;</item>
/// <item>the <b>slowest</b> participant (longest sub) owns the dither cadence —
/// it drives "every N frames", so the fast camera is never stalled waiting for a
/// dither it did not ask for;</item>
/// <item>when a dither is due the barrier waits (bounded) for every other
/// blocking participant to reach its between-subs boundary, dithers <b>once</b>
/// through the active guider, then releases everyone to start the next sub.</item>
/// </list>
///
/// The barrier only takes over when it actually owns dithering
/// (<see cref="OwnsDither"/> — sync enabled AND at least two imaging cameras
/// active). With a single imaging camera it is inert and every loop keeps its
/// existing per-loop dither exactly as before, so single-rig behavior does not
/// change. The DITHERGATE invariant is preserved: a skipped dither (guider not
/// guiding) does not advance the every-N counter.
/// </summary>
public sealed class DitherBarrier {
    private readonly ActiveGuiderProvider _guiders;
    private readonly ILogger<DitherBarrier> _logger;

    /// <summary>Master switch. Defaults on; only ever engages with >= 2 imaging
    /// cameras, so a single-camera rig is unaffected regardless.</summary>
    public bool Enabled { get; set; } = true;

    private sealed class Participant {
        public int RefCount;
        public bool Blocking;
        public bool IsPrimary;   // tie-break for cadence ownership (main beats aux)
        public double SubSeconds;
        public bool Parked;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Participant> _parts = new(StringComparer.OrdinalIgnoreCase);
    private int _roundsSinceDither;
    private bool _roundActive;
    private TaskCompletionSource<bool>? _release;

    // Cadence config, cached from the owning loop (see ConfigureCadence).
    private int _everyN = 3;
    private DitherParams _params = new();

    // Live state for the status contributor.
    public bool RoundActive { get { lock (_lock) return _roundActive; } }
    public bool Dithering { get; private set; }

    /// <summary>Number of imaging cameras currently registered as active barrier
    /// participants (dither status / technical panel).</summary>
    public int ActiveParticipants { get { lock (_lock) return ActiveImagingCount(); } }

    /// <summary>Id of the camera that currently owns the dither cadence — the
    /// slowest active imaging camera, main breaking ties. Null when nothing is
    /// participating.</summary>
    public string? CadenceOwner { get { lock (_lock) return CadenceOwnerIdLocked(); } }

    public DitherBarrier(ActiveGuiderProvider guiders, ILogger<DitherBarrier> logger) {
        _guiders = guiders;
        _logger = logger;
    }

    /// <summary>True when the barrier is the one that drives dithering: enabled
    /// and at least two imaging cameras are currently active participants. When
    /// false, capture loops must keep doing their own per-loop dither.</summary>
    public bool OwnsDither {
        get { lock (_lock) return Enabled && ActiveImagingCount() >= 2; }
    }

    private int ActiveImagingCount() {
        var n = 0;
        foreach (var p in _parts.Values) if (p.RefCount > 0) n++;
        return n;
    }

    /// <summary>Register (ref-counted) an imaging camera as a barrier
    /// participant. <paramref name="blocking"/> = the mount must not dither
    /// while this camera is mid-sub. <paramref name="isPrimary"/> breaks a
    /// cadence-ownership tie in favor of the main imaging camera.</summary>
    public void Register(string id, bool blocking = true, bool isPrimary = false) {
        lock (_lock) {
            if (!_parts.TryGetValue(id, out var p)) { p = new Participant(); _parts[id] = p; }
            p.Blocking = blocking;
            p.IsPrimary = isPrimary;
            p.RefCount++;
        }
    }

    public void Deregister(string id) {
        lock (_lock) {
            if (_parts.TryGetValue(id, out var p)) {
                p.RefCount = Math.Max(0, p.RefCount - 1);
                if (p.RefCount == 0) { p.Parked = false; p.SubSeconds = 0; }
            }
            // A round in flight releases naturally; if the owner just left, the
            // release TCS is completed by the running round itself.
        }
    }

    /// <summary>Feed the barrier the cadence (every-N) and dither parameters from
    /// the loop that has them (the main-camera loops). Cached so the barrier can
    /// dither even when the slowest participant is the aux camera.</summary>
    public void ConfigureCadence(int everyNFrames, DitherParams p) {
        lock (_lock) {
            if (everyNFrames > 0) _everyN = everyNFrames;
            if (p != null) _params = p;
        }
    }

    /// <summary>Call right before starting a sub. If a dither round is in flight,
    /// park here until it releases (bounded, so a wedged round can never hang a
    /// camera forever). No-op when the barrier does not own dithering.</summary>
    public async Task BeforeSubAsync(string id, CancellationToken ct = default) {
        Task<bool>? wait = null;
        lock (_lock) {
            if (_roundActive && _parts.TryGetValue(id, out var p)) {
                p.Parked = true;
                wait = _release?.Task;
            }
        }
        if (wait == null) return;
        var budget = TimeSpan.FromSeconds(Math.Max(30, _params.SettleTimeout * 3 + 30));
        try {
            await wait.WaitAsync(budget, ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            _logger.LogWarning("DitherBarrier: '{Id}' parked past {Sec:n0}s waiting for a dither round; proceeding.",
                id, budget.TotalSeconds);
        } catch (OperationCanceledException) { /* loop is shutting down */ }
        lock (_lock) { if (_parts.TryGetValue(id, out var p)) p.Parked = false; }
    }

    /// <summary>Call right after a sub completes. Updates this participant's sub
    /// length; if the barrier owns dithering and this participant is the cadence
    /// owner (slowest), advances the round counter and — when a dither is due —
    /// runs one synchronized dither for all cameras. Returns immediately for
    /// non-owners and single-camera rigs.</summary>
    public async Task AfterSubAsync(string id, double subSeconds, CancellationToken ct = default) {
        bool runRound = false;
        lock (_lock) {
            if (_parts.TryGetValue(id, out var self) && subSeconds > 0) self.SubSeconds = subSeconds;
            if (!Enabled || ActiveImagingCount() < 2) return;      // barrier inert
            if (!string.Equals(id, CadenceOwnerIdLocked(), StringComparison.OrdinalIgnoreCase)) return;
            _roundsSinceDither++;
            if (IsDitherDue(_roundsSinceDither, _everyN, _roundActive)) {
                _roundActive = true;
                _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                runRound = true;
            }
        }
        if (runRound) await RunDitherRoundAsync(id, ct).ConfigureAwait(false);
    }

    private async Task RunDitherRoundAsync(string ownerId, CancellationToken ct) {
        DitherParams p;
        double maxOtherSub;
        lock (_lock) {
            p = _params;
            maxOtherSub = 0;
            foreach (var kv in _parts)
                if (kv.Value.RefCount > 0 && kv.Value.Blocking
                    && !string.Equals(kv.Key, ownerId, StringComparison.OrdinalIgnoreCase))
                    maxOtherSub = Math.Max(maxOtherSub, kv.Value.SubSeconds);
        }
        // The owner is the slowest, so every other blocking camera finishes its
        // current (shorter) sub and parks within ~its own sub length. Bound the
        // rendezvous to that plus the settle budget, floored/capped for sanity.
        var rendezvous = TimeSpan.FromSeconds(Math.Clamp(maxOtherSub + p.SettleTimeout + 15, 30, 300));
        try {
            await WaitOthersParkedAsync(ownerId, rendezvous, ct).ConfigureAwait(false);
            var dithered = await DoGuiderDitherAsync(p, ct).ConfigureAwait(false);
            lock (_lock) {
                // DITHERGATE: only a real dither consumes the every-N slot.
                if (dithered) _roundsSinceDither = 0;
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DitherBarrier: synchronized dither round failed");
        } finally {
            TaskCompletionSource<bool>? release;
            lock (_lock) {
                _roundActive = false;
                release = _release;
                _release = null;
                foreach (var pp in _parts.Values) pp.Parked = false;
            }
            release?.TrySetResult(true);
        }
    }

    private async Task WaitOthersParkedAsync(string ownerId, TimeSpan max, CancellationToken ct) {
        var deadline = DateTime.UtcNow + max;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
            bool allParked = true;
            lock (_lock) {
                foreach (var kv in _parts) {
                    if (kv.Value.RefCount == 0 || !kv.Value.Blocking) continue;
                    if (string.Equals(kv.Key, ownerId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!kv.Value.Parked) { allParked = false; break; }
                }
            }
            if (allParked) return;
            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        _logger.LogInformation(
            "DitherBarrier: rendezvous window ({Sec:n0}s) elapsed before every camera parked; dithering anyway.",
            max.TotalSeconds);
    }

    /// <summary>Fire one dither on the active guider and wait for settle, exactly
    /// like the per-loop paths do. Returns false without dithering when the
    /// guider is not guiding (the caller then must not advance the counter).</summary>
    private async Task<bool> DoGuiderDitherAsync(DitherParams p, CancellationToken ct) {
        var g = _guiders.Active;
        if (g == null || !g.IsConnected || !g.IsGuiding) {
            _logger.LogInformation("DitherBarrier: dither skipped — guider not guiding; retries on the next round.");
            return false;
        }
        Dithering = true;
        var settled = new TaskCompletionSource<SettleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSettled(SettleResult r) => settled.TrySetResult(r);
        g.Settled += OnSettled;
        try {
            _logger.LogInformation("DitherBarrier: synchronized dither {Px}px (raOnly={Ra}, backend={Backend})",
                p.Pixels, p.RaOnly, g.Backend);
            await g.DitherAsync(p.Pixels, p.RaOnly, p.SettlePixels, p.SettleTime, p.SettleTimeout, ct)
                .ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(p.SettleTimeout + 5));
            try {
                var r = await settled.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                if (r.Status != 0) _logger.LogWarning("DitherBarrier: settle status {S}: {E}", r.Status, r.Error);
            } catch (OperationCanceledException) {
                _logger.LogWarning("DitherBarrier: settle timed out, continuing");
            }
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "DitherBarrier: guider dither crashed");
            return false;
        } finally {
            g.Settled -= OnSettled;
            Dithering = false;
        }
    }

    private string? CadenceOwnerIdLocked() {
        var rows = new List<CadenceRow>(_parts.Count);
        foreach (var kv in _parts)
            rows.Add(new CadenceRow(kv.Key, kv.Value.RefCount, kv.Value.IsPrimary, kv.Value.SubSeconds));
        return SelectCadenceOwner(rows);
    }

    // ----- pure decision helpers (unit-tested) -----

    /// <summary>A row of the participant table, decoupled from the private
    /// mutable participant so the cadence-owner rule is unit-testable.</summary>
    internal readonly record struct CadenceRow(string Id, int RefCount, bool Primary, double SubSeconds);

    /// <summary>The slowest active participant owns the cadence; ties break in
    /// favor of the primary (main) camera, then by id for determinism.</summary>
    internal static string? SelectCadenceOwner(IEnumerable<CadenceRow> rows) {
        string? best = null; double bestSub = -1; bool bestPrimary = false;
        foreach (var r in rows) {
            if (r.RefCount == 0) continue;
            var better = r.SubSeconds > bestSub
                || (r.SubSeconds == bestSub && r.Primary && !bestPrimary)
                || (r.SubSeconds == bestSub && r.Primary == bestPrimary
                    && (best == null || string.CompareOrdinal(r.Id, best) < 0));
            if (better) { best = r.Id; bestSub = r.SubSeconds; bestPrimary = r.Primary; }
        }
        return best;
    }

    internal static bool IsDitherDue(int roundsSinceDither, int everyN, bool roundActive) {
        if (roundActive) return false;
        if (everyN <= 0) return false;
        return roundsSinceDither >= everyN;
    }
}
