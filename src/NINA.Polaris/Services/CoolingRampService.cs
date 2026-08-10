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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Drives the camera cooler setpoint at a controlled rate (°C/min) instead of
/// jumping straight to the target.
///
/// WHY: slamming the setpoint runs the TEC at 100% and drops the sensor as fast
/// as the hardware allows — field log 2026-07-15 shows ~3.7°C/min going from
/// 27°C ambient to 0°C. A fast plunge is the classic way to condense dew on the
/// sensor window, and thermally shocks the stack. Warming back up uncontrolled is
/// just as bad (arguably worse for dew: a cold sensor returning to ambient is
/// exactly when moisture condenses on it).
///
/// The ramp already existed — in ONE of the four places that touch the cooler.
/// WarmCameraInstruction has walked the setpoint at 2°C/min "to protect the
/// sensor from thermal shock" all along, while CoolCameraInstruction and both UI
/// cooler buttons wrote the setpoint raw. This service is that logic pulled out
/// so all four share it, main and aux alike.
///
/// NOT delegated to INDI's CCD_TEMP_RAMP, even though indi_svbony_ccd (and every
/// INDI::CCD driver) implements one and it would survive a Polaris restart. That
/// property only covers INDI; the five native SDKs, Alpaca and ASCOM would still
/// need this service, leaving two ramp controllers with different behaviour and
/// two places to fix every future bug. One path, identical on every backend. We
/// leave CCD_TEMP_RAMP disabled (its default) on purpose: with the driver also
/// ramping, two controllers would fight over the same setpoint.
///
/// Ramps run in the background: a 27→0°C ramp at 2°C/min takes ~14 minutes, far
/// too long to hold an HTTP request open. Callers that need to wait (the
/// sequencer) await <see cref="Current"/>; the UI fires and watches the WS block.
/// </summary>
public class CoolingRampService {
    /// <summary>How often the ramp pushes a new setpoint. Matches the 10s step
    /// WarmCameraInstruction used, which the SV405CC/ASI drivers handle happily —
    /// short enough that the TEC tracks smoothly, long enough not to spam INDI.</summary>
    internal static readonly TimeSpan StepInterval = TimeSpan.FromSeconds(10);

    /// <summary>Slot key for the main imaging camera.</summary>
    public const string Main = "main";
    /// <summary>Slot key for the auxiliary (second) camera.</summary>
    public const string Aux = "aux";

    private readonly ILogger<CoolingRampService> _logger;
    private readonly object _gate = new();

    /// <summary>State is per SLOT, not global. The main and aux cameras cool
    /// independently and a rig can run both; with one shared CTS, starting an aux
    /// ramp would silently cancel the main camera's cooldown and strand its
    /// setpoint mid-descent. Keyed by slot rather than by ICamera instance so a
    /// reconnect (new instance, same slot) replaces the entry instead of leaking
    /// a stale one.</summary>
    private readonly Dictionary<string, Slot> _slots = new();

    private sealed class Slot {
        public CancellationTokenSource? Cts;
        public Task Current = Task.CompletedTask;
        public RampState? State;
    }

    public CoolingRampService(ILogger<CoolingRampService> logger) {
        _logger = logger;
    }

    private Slot SlotFor(string slot) {
        if (!_slots.TryGetValue(slot, out var s)) _slots[slot] = s = new Slot();
        return s;
    }

    /// <summary>The in-flight ramp for a slot (or a completed task). Await to block
    /// until the setpoint finishes walking; the sequencer does, the UI doesn't.</summary>
    public Task Current(string slot = Main) { lock (_gate) return SlotFor(slot).Current; }

    /// <summary>Await the in-flight ramp while observing <paramref name="ct"/>, and
    /// cancel the ramp itself if the wait is cancelled.
    ///
    /// The sequencer used to `await Current()` with no token and only check the
    /// token afterwards. A ramp is minutes long by design (2 C/min, so -10 to
    /// +20 is a quarter of an hour), and during it Stop did nothing observable:
    /// the plan sat in the warm-up with the operator unable to end it until the
    /// cooler finished. Cancelling the ramp too matters as much as returning —
    /// otherwise a stopped plan leaves a setpoint still walking in the
    /// background, moving hardware nobody is watching any more.</summary>
    public async Task WaitAsync(CancellationToken ct, string slot = Main) {
        try {
            await Current(slot).WaitAsync(ct);
        } catch (OperationCanceledException) {
            Cancel(slot);
            throw;
        }
    }

    public RampState? Snapshot(string slot = Main) { lock (_gate) return SlotFor(slot).State; }

    /// <summary>Every slot's state, for the WS status block.</summary>
    public IReadOnlyDictionary<string, RampState> SnapshotAll() {
        lock (_gate) {
            var result = new Dictionary<string, RampState>();
            foreach (var (key, s) in _slots) if (s.State != null) result[key] = s.State;
            return result;
        }
    }

    /// <summary>Size of one step, in °C. Derived from the rate so the caller only
    /// picks a °C/min and the cadence stays fixed.
    ///
    /// Floored at 0.1°C because the step is what we ADD to the setpoint each tick:
    /// let it round to 0 and the ramp writes the same value forever and never
    /// arrives. At the 10s cadence 0.1°C is 0.6°C/min, i.e. rates below that are
    /// silently treated as 0.6°C/min rather than hanging. Rates &lt;= 0 mean
    /// "no ramp" and never reach here.</summary>
    internal static double StepSizeFor(double ratePerMinute) {
        var perStep = ratePerMinute * StepInterval.TotalMinutes;
        return Math.Max(0.1, perStep);
    }

    /// <summary>Next setpoint on the way from <paramref name="current"/> to
    /// <paramref name="target"/>, never overshooting. Direction-agnostic: the same
    /// function ramps down (cooling) and up (warming), which is the whole point of
    /// having one service for both.</summary>
    internal static double NextSetpoint(double current, double target, double stepC) {
        if (target < current) return Math.Max(target, current - stepC);
        return Math.Min(target, current + stepC);
    }

    /// <summary>Start (or restart) a ramp to <paramref name="targetC"/>.
    ///
    /// A new ramp cancels any ramp in flight — the user changing their mind
    /// mid-cooldown must not leave two loops writing the setpoint. Returns once
    /// the ramp is STARTED, not once it finishes; await <see cref="Current"/> for
    /// that.
    ///
    /// <paramref name="ratePerMinute"/> &lt;= 0 disables ramping: setpoint is written
    /// once, immediately. That's the pre-ramp behaviour, kept reachable so a rig
    /// can opt out.
    ///
    /// <paramref name="coolerOffWhenDone"/> powers the TEC down after arriving —
    /// the warm-up case. Cutting the cooler at the START of a warm-up (what the
    /// UI's OFF button used to do) is precisely the uncontrolled return to ambient
    /// we're preventing, so it only ever happens at the END of a ramp.</summary>
    public void Start(ICamera camera, double targetC, double ratePerMinute,
                      bool coolerOnFirst, bool coolerOffWhenDone, string source,
                      string slot = Main) {
        lock (_gate) {
            var s = SlotFor(slot);
            // Cancel only THIS slot's ramp — the other camera keeps cooling.
            s.Cts?.Cancel();
            s.Cts?.Dispose();
            s.Cts = new CancellationTokenSource();
            s.Current = RunAsync(camera, targetC, ratePerMinute, coolerOnFirst,
                                 coolerOffWhenDone, source, slot, s.Cts.Token);
        }
    }

    /// <summary>Stop a slot's in-flight ramp and leave the setpoint wherever it got
    /// to. Safe mid-ramp: an intermediate setpoint is a valid state, the TEC just
    /// holds it.</summary>
    public void Cancel(string slot = Main) {
        lock (_gate) {
            var s = SlotFor(slot);
            s.Cts?.Cancel();
            s.State = s.State == null ? null : s.State with { Running = false };
        }
    }

    private async Task RunAsync(ICamera camera, double targetC, double ratePerMinute,
                                bool coolerOnFirst, bool coolerOffWhenDone,
                                string source, string slot, CancellationToken ct) {
        // Yield first so Start() returns to its caller (and releases _gate)
        // before any device I/O happens.
        await Task.Yield();
        var startC = camera.Temperature;
        try {
            if (coolerOnFirst) await camera.SetCoolerAsync(true, ct);

            if (ratePerMinute <= 0) {
                // Ramping disabled for this rig — old behaviour, one write.
                await camera.SetTemperatureAsync(targetC, ct);
                lock (_gate) SlotFor(slot).State = new RampState(false, source, startC, targetC, targetC, ratePerMinute);
                if (coolerOffWhenDone) await camera.SetCoolerAsync(false, ct);
                return;
            }

            var stepC = StepSizeFor(ratePerMinute);
            // Walk from where the SENSOR is now, not from the last setpoint: if the
            // TEC is lagging (or the camera was just connected and is at ambient)
            // stepping from a stale setpoint would jump the real temperature.
            var setpoint = camera.Temperature;
            _logger.LogInformation(
                "Cooling ramp [{Source}]: {Start:0.0}°C → {Target:0.0}°C at {Rate:0.#}°C/min (~{Mins:0} min)",
                source, startC, targetC, ratePerMinute,
                Math.Abs(targetC - startC) / ratePerMinute);

            while (Math.Abs(setpoint - targetC) > 1e-3) {
                ct.ThrowIfCancellationRequested();
                setpoint = NextSetpoint(setpoint, targetC, stepC);
                await camera.SetTemperatureAsync(setpoint, ct);
                lock (_gate) SlotFor(slot).State = new RampState(true, source, startC, targetC, setpoint, ratePerMinute);
                await Task.Delay(StepInterval, ct);
            }

            if (coolerOffWhenDone) await camera.SetCoolerAsync(false, ct);
            lock (_gate) SlotFor(slot).State = new RampState(false, source, startC, targetC, targetC, ratePerMinute);
            _logger.LogInformation("Cooling ramp [{Source}] finished at {Target:0.0}°C (sensor {Now:0.0}°C)",
                source, targetC, camera.Temperature);
        } catch (OperationCanceledException) {
            lock (_gate) { var st = SlotFor(slot); st.State = st.State == null ? null : st.State with { Running = false }; }
            _logger.LogInformation("Cooling ramp [{Source}] cancelled", source);
        } catch (Exception ex) {
            lock (_gate) { var st = SlotFor(slot); st.State = st.State == null ? null : st.State with { Running = false }; }
            _logger.LogWarning(ex, "Cooling ramp [{Source}] failed", source);
        }
    }
}

/// <summary>Observable ramp state for the WS status block.</summary>
/// <param name="Running">A ramp is walking the setpoint right now.</param>
/// <param name="Source">Who asked (UI / sequencer / aux), for the log + UI.</param>
/// <param name="StartC">Sensor temperature when the ramp began.</param>
/// <param name="TargetC">Where it's heading.</param>
/// <param name="SetpointC">Setpoint currently written to the driver — the
/// sensor lags this, which is exactly what makes the ramp gentle.</param>
/// <param name="RatePerMinute">Configured rate; 0 means ramping is off.</param>
public sealed record RampState(bool Running, string Source, double StartC,
                               double TargetC, double SetpointC, double RatePerMinute);
