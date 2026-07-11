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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

/// <summary>
/// POLARUI: pins the guard behaviour of the single-shot manual
/// Refresh (RefineOnceAsync). The happy path needs live camera +
/// telescope + solver and is exercised in the field; what the tests
/// can and must pin is that the guards reject calls in the wrong
/// state, and that a failed step restores the job to Ok instead of
/// leaving it stuck in Refining.
/// </summary>
[TestFixture]
public class PolarAlignmentRefineOnceTests {

    private static PolarAlignmentService MakeService() {
        var cfg = new ConfigurationBuilder().Build();
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());
        var solver = new PlateSolveService(cfg, NullLogger<PlateSolveService>.Instance);
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        return new PolarAlignmentService(equip, solver, profiles,
            new NotificationService(), NullLogger<PolarAlignmentService>.Instance);
    }

    /// <summary>CurrentJob has a private setter (only RunAsync creates
    /// jobs); tests inject a synthetic completed-TPPA job through it.</summary>
    private static void InjectJob(PolarAlignmentService svc, PolarAlignmentJob job) {
        typeof(PolarAlignmentService)
            .GetProperty(nameof(PolarAlignmentService.CurrentJob))!
            .SetValue(svc, job);
    }

    private static PolarAlignmentJob MakeOkJob(int points = 3) {
        var job = new PolarAlignmentJob {
            Id = "test",
            Mode = "tppa",
            Phase = PolarAlignmentPhase.Ok,
            AzErrorArcsec = 120,
            AltErrorArcsec = -60,
            TotalErrorArcsec = 134.16,
        };
        for (int i = 0; i < points; i++) {
            job.Points.Add(new PolarPoint(i, 10.0 + i, 85.0, 0.0, DateTime.UtcNow));
        }
        return job;
    }

    [Test]
    public void RefineLoopActive_Defaults_False() {
        Assert.That(MakeService().RefineLoopActive, Is.False);
    }

    [Test]
    public void RefineOnce_WithoutBaseline_Throws409Guard() {
        // No TPPA has run: there is no 3-point baseline to refine
        // against, the endpoint maps this to a 409.
        var svc = MakeService();
        Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefineOnceAsync());
    }

    [Test]
    public void RefineOnce_WithTooFewPoints_Throws() {
        var svc = MakeService();
        InjectJob(svc, MakeOkJob(points: 2));
        Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefineOnceAsync());
    }

    [Test]
    public void RefineOnce_WhileJobNotOk_Throws() {
        // A TPPA sweep (or a failed one) is not a refreshable state.
        var svc = MakeService();
        var job = MakeOkJob();
        job.Phase = PolarAlignmentPhase.SolvingPoint2;
        InjectJob(svc, job);
        Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefineOnceAsync());
    }

    [Test]
    public async Task RefineOnce_NoCamera_ReturnsFalse_AndRestoresOk() {
        // Valid baseline but nothing connected: the step must FAIL
        // SOFT — report false + LastError and put the job back into
        // Ok/tppa so the UI's Refresh button doesn't wedge in
        // "Refining" forever.
        var svc = MakeService();
        InjectJob(svc, MakeOkJob());

        var solved = await svc.RefineOnceAsync();

        Assert.That(solved, Is.False);
        Assert.That(svc.CurrentJob!.LastError, Is.Not.Null.And.Not.Empty);
        Assert.That(svc.CurrentJob.Phase, Is.EqualTo(PolarAlignmentPhase.Ok),
            "a failed refresh must return the job to Ok, not leave it Refining");
        Assert.That(svc.CurrentJob.Mode, Is.EqualTo("tppa"));
        Assert.That(svc.CurrentJob.CompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task RefineOnce_PreservesLastErrorVector_OnFailure() {
        // The whole point of the bullseye: a failed re-solve must not
        // zero out the last good error, the dot stays where it was.
        var svc = MakeService();
        InjectJob(svc, MakeOkJob());

        await svc.RefineOnceAsync();

        Assert.That(svc.CurrentJob!.AzErrorArcsec, Is.EqualTo(120));
        Assert.That(svc.CurrentJob.AltErrorArcsec, Is.EqualTo(-60));
        Assert.That(svc.CurrentJob.TotalErrorArcsec, Is.EqualTo(134.16));
    }
}
