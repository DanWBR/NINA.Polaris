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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Telling apart the two things that both look like "it keeps disconnecting".
///
/// <para>Users report this as a network fault, because from the browser that
/// is exactly what it looks like. It has been the service dying and systemd
/// restarting it five seconds later, and it has been an adapter dropping its
/// link. The diagnostic has to name which, and these are the two judgements
/// that do it.</para>
/// </summary>
[TestFixture]
public class DiagnosticsDisconnectTests {

    // --- restarts ---------------------------------------------------------

    [Test]
    public void AServiceThatHasNeverRestartedIsFine() {
        var (sev, detail, fix) = DiagnosticsService.JudgeRestarts(0);
        Assert.That(sev, Is.EqualTo(DiagSeverity.Ok));
        Assert.That(detail, Does.Contain("0"));
        Assert.That(fix, Is.Null);
    }

    [Test]
    public void OneOrTwoRestartsWarnWithoutCryingWolf() {
        // An update restarts it. So does the operator. A warning that fires on
        // every normal day teaches people to ignore the report.
        foreach (var n in new[] { 1, 2 }) {
            var (sev, _, fix) = DiagnosticsService.JudgeRestarts(n);
            Assert.That(sev, Is.EqualTo(DiagSeverity.Warn), $"{n} restarts");
            Assert.That(fix, Is.Not.Null);
        }
    }

    [Test]
    public void ManyRestartsAreAFailureAndSayWhereToLook() {
        var (sev, detail, fix) = DiagnosticsService.JudgeRestarts(37);
        Assert.That(sev, Is.EqualTo(DiagSeverity.Fail));
        Assert.That(detail, Does.Contain("37"));
        Assert.That(fix, Does.Contain("journalctl -u polaris"));
        // The point of the sentence: stop the operator blaming the network.
        Assert.That(fix, Does.Contain("network"));
    }

    [Test]
    public void ANegativeCountIsTreatedAsNone() {
        // systemd has no reason to report one, but a parse that went wrong
        // must not turn into a red Fail on a healthy host.
        Assert.That(DiagnosticsService.JudgeRestarts(-1).Sev, Is.EqualTo(DiagSeverity.Ok));
    }

    // --- link flaps -------------------------------------------------------

    private static (string Sev, string Detail, string? Fix) Link(int changes, double hours)
        => DiagnosticsService.JudgeLink("enp1s0", "r8169", changes, TimeSpan.FromHours(hours));

    [Test]
    public void ACableThatCameUpOnceIsFine() {
        // A link brought up at boot counts one transition, sometimes two.
        foreach (var n in new[] { 0, 1, 2 })
            Assert.That(Link(n, 8).Sev, Is.EqualTo(DiagSeverity.Ok), $"{n} changes");
    }

    [Test]
    public void AFlappingLinkFailsAndBlamesTheAdapterNotPolaris() {
        var (sev, detail, fix) = Link(120, 6);   // 20 an hour
        Assert.That(sev, Is.EqualTo(DiagSeverity.Fail));
        Assert.That(detail, Does.Contain("enp1s0").And.Contain("120"));
        Assert.That(fix, Does.Contain("not Polaris"));
        // The mini PC case that prompted this: 2.5GbE Realtek or Intel.
        Assert.That(fix, Does.Contain("r8169").Or.Contain("igc"));
    }

    [Test]
    public void AFewFlapsOverALongUptimeOnlyWarn() {
        // Six transitions across two days is someone moving a cable, not a
        // fault worth a red line in the report.
        var (sev, _, _) = Link(6, 48);
        Assert.That(sev, Is.EqualTo(DiagSeverity.Warn));
    }

    [Test]
    public void TheRateIsWhatDecides() {
        // The same count means different things at different uptimes, which
        // is the whole reason uptime is a parameter.
        Assert.That(Link(10, 40).Sev, Is.EqualTo(DiagSeverity.Warn), "10 in 40 h");
        Assert.That(Link(10, 1).Sev, Is.EqualTo(DiagSeverity.Fail), "10 in 1 h");
    }

    [Test]
    public void AHostUpForSecondsDoesNotDivideByZero() {
        // Called right after boot, which is exactly when a diagnostic gets
        // run, and where an uptime in hours is near enough to zero to hurt.
        Assert.That(() => DiagnosticsService.JudgeLink("eth0", "igc", 3, TimeSpan.Zero),
            Throws.Nothing);
        Assert.That(DiagnosticsService.JudgeLink("eth0", "igc", 3, TimeSpan.Zero).Sev,
            Is.EqualTo(DiagSeverity.Fail));
    }

    [Test]
    public void TheDriverNameIsInTheDetailBecauseItIsTheClue() {
        // "r8169 on a 2.5GbE box" is the whole diagnosis in a lot of these
        // reports, and it has to survive into the pasted report.
        Assert.That(DiagnosticsService.JudgeLink("enp2s0", "r8169", 40, TimeSpan.FromHours(2)).Detail,
            Does.Contain("r8169"));
    }

    [Test]
    public void NothingInEitherJudgementLeaksThroughRedaction() {
        // The report is pasted into Discord. Whatever these two produce goes
        // through Redact, so make sure that is not a no-op on them.
        var fix = Link(90, 3).Fix!;
        Assert.That(DiagnosticsService.Redact(fix), Is.EqualTo(fix),
            "no secret-shaped text expected here, so redaction must leave it intact");
    }
}
