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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

/// <summary>
/// COOLRAMP-1: the setpoint-walking maths behind <see cref="CoolingRampService"/>.
///
/// Tested as pure functions rather than by running a ramp: a real 27→0°C ramp at
/// 2°C/min takes ~14 minutes of wall clock. The step/next-setpoint pair is where
/// every interesting mistake lives (overshoot, never-arriving, wrong direction),
/// so pinning those covers the risk without a clock.
/// </summary>
[TestFixture]
public class CoolingRampTests {
    /// <summary>Records the ORDER of cooler operations, not just the final state —
    /// "cooler off after the last setpoint write" is a sequencing rule, and a
    /// snapshot of end state can't tell a correct order from a wrong one.</summary>
    private sealed class FakeCamera : ICamera {
        public readonly List<string> Ops = new();
        public double LastTarget;
        public int TargetWrites;
        public bool CoolerOn { get; private set; }
        public double Temperature { get; set; }

        public Task SetTemperatureAsync(double t, CancellationToken ct = default) {
            LastTarget = t;
            TargetWrites++;
            Ops.Add($"target:{t}");
            return Task.CompletedTask;
        }
        public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
            CoolerOn = on;
            Ops.Add(on ? "cooler:on" : "cooler:off");
            return Task.CompletedTask;
        }

        public string DeviceName => "fake";
        public bool IsConnected => true;
        public NINA.Core.Enum.CameraStates State => NINA.Core.Enum.CameraStates.Idle;
        public double CoolerPower => 0;
        public int BinX => 1;
        public int BinY => 1;
        public int BitDepth => 16;
        public int MaxX => 1000;
        public int MaxY => 1000;
        public double PixelSizeX => 3.76;
        public double PixelSizeY => 3.76;
        public int Gain => 0;
        public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
        public int SelectedIso => 0;
        public CameraCapabilities Capabilities => CameraCapabilities.Astro;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IImageData> CaptureAsync(double exp, CaptureOptions? opts = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task SetBinningAsync(int bx, int by, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbortExposureAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>2°C/min over 10s steps = 0.333°C per step. The default rate,
    /// inherited from WarmCameraInstruction.</summary>
    [Test]
    public void StepSizeFor_DefaultRate_IsRatePerStepInterval() {
        Assert.That(CoolingRampService.StepSizeFor(2.0), Is.EqualTo(2.0 / 6).Within(1e-9));
    }

    /// <summary>A rate so low the per-step delta would round toward zero must be
    /// floored, not allowed to become 0 — a 0°C step writes the same setpoint
    /// forever and the ramp never arrives (a hang, in a loop with device I/O).</summary>
    [Test]
    public void StepSizeFor_AbsurdlyLowRate_IsFlooredNotZero() {
        var step = CoolingRampService.StepSizeFor(0.001);
        Assert.That(step, Is.GreaterThan(0), "a zero step never converges");
        Assert.That(step, Is.EqualTo(0.1).Within(1e-9));
    }

    /// <summary>Cooling: walks down by one step.</summary>
    [Test]
    public void NextSetpoint_Cooling_StepsDown() {
        Assert.That(CoolingRampService.NextSetpoint(20, 0, 0.5), Is.EqualTo(19.5).Within(1e-9));
    }

    /// <summary>Warming: the SAME function walks up. One service for both
    /// directions is the whole point — the ramp used to exist only on the
    /// warm-up side.</summary>
    [Test]
    public void NextSetpoint_Warming_StepsUp() {
        Assert.That(CoolingRampService.NextSetpoint(0, 20, 0.5), Is.EqualTo(0.5).Within(1e-9));
    }

    /// <summary>Never overshoot the target, in either direction. Overshooting
    /// while cooling would drive the TEC past the setpoint the user asked for.</summary>
    [Test]
    public void NextSetpoint_LastStep_ClampsToTargetBothWays() {
        Assert.That(CoolingRampService.NextSetpoint(0.2, 0, 0.5), Is.EqualTo(0).Within(1e-9),
            "cooling must not undershoot past target");
        Assert.That(CoolingRampService.NextSetpoint(19.8, 20, 0.5), Is.EqualTo(20).Within(1e-9),
            "warming must not overshoot past target");
    }

    /// <summary>Already at the target → stays put (the loop's exit condition).</summary>
    [Test]
    public void NextSetpoint_AtTarget_DoesNotMove() {
        Assert.That(CoolingRampService.NextSetpoint(0, 0, 0.5), Is.EqualTo(0).Within(1e-9));
    }

    /// <summary>End-to-end on the maths: walking 27→0°C at 2°C/min must converge,
    /// land exactly on target, never pass it, and take the time the rate implies
    /// (~13.5 min). This is the field scenario — the log showed the real camera
    /// doing this drop at ~3.7°C/min with the TEC pinned.</summary>
    [Test]
    public void Walk_AmbientToZero_ConvergesAtTheConfiguredRate() {
        const double start = 27.2, target = 0.0, rate = 2.0;
        var step = CoolingRampService.StepSizeFor(rate);
        var setpoint = start;
        var steps = 0;
        var seen = new List<double>();

        while (Math.Abs(setpoint - target) > 1e-3) {
            setpoint = CoolingRampService.NextSetpoint(setpoint, target, step);
            seen.Add(setpoint);
            Assert.That(setpoint, Is.GreaterThanOrEqualTo(target), "must never pass the target");
            if (++steps > 10_000) Assert.Fail("ramp did not converge");
        }

        Assert.That(setpoint, Is.EqualTo(target).Within(1e-9));
        Assert.That(seen, Is.Ordered.Descending, "cooling setpoints must fall monotonically");

        // steps * 10s should match |ΔT| / rate minutes, within one step.
        var expectedMinutes = Math.Abs(start - target) / rate;
        var actualMinutes = steps * CoolingRampService.StepInterval.TotalMinutes;
        Assert.That(actualMinutes, Is.EqualTo(expectedMinutes).Within(CoolingRampService.StepInterval.TotalMinutes),
            "ramp duration must follow the configured °C/min");
    }

    /// <summary>Slots are independent: starting an AUX ramp must not cancel the
    /// MAIN camera's ramp. Caught while wiring this up — the service began life
    /// with a single CTS, so an aux cooldown would have silently stranded the main
    /// camera's setpoint mid-descent and left it cooling to nowhere.
    ///
    /// Uses rate 0 (write-once) so the ramps complete instantly instead of
    /// stepping on a 10s timer; the slot bookkeeping is what's under test.</summary>
    [Test]
    public async Task Slots_AuxRamp_DoesNotCancelMainRamp() {
        var svc = new CoolingRampService(NullLogger<CoolingRampService>.Instance);
        var main = new FakeCamera();
        var aux = new FakeCamera();

        svc.Start(main, -10, 0, coolerOnFirst: true, coolerOffWhenDone: false,
                  source: "test main", slot: CoolingRampService.Main);
        svc.Start(aux, -5, 0, coolerOnFirst: true, coolerOffWhenDone: false,
                  source: "test aux", slot: CoolingRampService.Aux);

        await svc.Current(CoolingRampService.Main);
        await svc.Current(CoolingRampService.Aux);

        Assert.That(main.LastTarget, Is.EqualTo(-10), "main ramp must still have run");
        Assert.That(aux.LastTarget, Is.EqualTo(-5), "aux ramp must have run too");

        var mainState = svc.Snapshot(CoolingRampService.Main);
        var auxState = svc.Snapshot(CoolingRampService.Aux);
        Assert.That(mainState?.TargetC, Is.EqualTo(-10));
        Assert.That(auxState?.TargetC, Is.EqualTo(-5), "aux state must not have overwritten main's");
    }

    /// <summary>Rate 0 = ramping off: setpoint written once, straight to target.
    /// The opt-out has to keep working, it's the pre-COOLRAMP behaviour.</summary>
    [Test]
    public async Task RateZero_WritesTargetOnce_NoRamp() {
        var svc = new CoolingRampService(NullLogger<CoolingRampService>.Instance);
        var cam = new FakeCamera();

        svc.Start(cam, -10, 0, coolerOnFirst: true, coolerOffWhenDone: false, source: "test");
        await svc.Current();

        Assert.That(cam.TargetWrites, Is.EqualTo(1), "rate 0 must not step");
        Assert.That(cam.LastTarget, Is.EqualTo(-10));
        Assert.That(cam.CoolerOn, Is.True);
    }

    /// <summary>Warm-up powers the TEC down only AFTER the setpoint arrives, never
    /// before. Cutting it first is exactly the uncontrolled return to ambient this
    /// whole thing exists to prevent — and on SVBony, writing the target after a
    /// disable re-asserts SVB_COOLER_ENABLE and bounces the cooler back on.</summary>
    [Test]
    public async Task WarmUp_PowersCoolerOffOnlyAfterTheLastSetpointWrite() {
        var svc = new CoolingRampService(NullLogger<CoolingRampService>.Instance);
        var cam = new FakeCamera { Temperature = 0 };

        svc.Start(cam, 20, 0, coolerOnFirst: false, coolerOffWhenDone: true, source: "test warm");
        await svc.Current();

        Assert.That(cam.CoolerOn, Is.False, "cooler must end up off");
        Assert.That(cam.Ops, Is.EqualTo(new[] { "target:20", "cooler:off" }),
            "the cooler must be cut only after the final setpoint write, never before");
    }

    /// <summary>Warm-up converges too, and rises monotonically. The UI's cooler-OFF
    /// button used to cut the TEC dead at this point, letting a 0°C sensor race back
    /// to ambient — the textbook way to condense water on the window.</summary>
    [Test]
    public void Walk_ZeroToAmbient_ConvergesUpward() {
        const double start = 0.0, target = 20.0;
        var step = CoolingRampService.StepSizeFor(2.0);
        var setpoint = start;
        var steps = 0;
        var seen = new List<double>();

        while (Math.Abs(setpoint - target) > 1e-3) {
            setpoint = CoolingRampService.NextSetpoint(setpoint, target, step);
            seen.Add(setpoint);
            Assert.That(setpoint, Is.LessThanOrEqualTo(target), "must never pass the target");
            if (++steps > 10_000) Assert.Fail("ramp did not converge");
        }

        Assert.That(setpoint, Is.EqualTo(target).Within(1e-9));
        Assert.That(seen, Is.Ordered.Ascending, "warm-up setpoints must rise monotonically");
    }
}
