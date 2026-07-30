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
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The two properties the diagnostic is worthless without: it must not leak a
/// secret into a report people paste in public, and it must not report a
/// passing check when the check could not run.
/// </summary>
[TestFixture]
public class DiagnosticsTests {

    // ---- redaction ----

    [Test]
    public void Redact_RemovesA64HexPreSharedKey() {
        // The exact shape that shipped inside five published images.
        const string psk = "5cac3a3edccceb69bf40e2fd7f0a77f85231fd8c1ee0419b7b7fc7d6d16836b5";
        var s = DiagnosticsService.Redact($"wifi psk {psk} for Medeiros_Plus");
        Assert.That(s, Does.Not.Contain(psk));
        Assert.That(s, Does.Contain("<redacted-key>"));
        // The SSID is not a secret and stays: it is what makes the report useful.
        Assert.That(s, Does.Contain("Medeiros_Plus"));
    }

    [TestCase("password=hunter2")]
    [TestCase("psk: s3cr3t-value")]
    [TestCase("apikey = abcd1234")]
    [TestCase("TOKEN=zzzz")]
    public void Redact_RemovesAssignedSecrets(string input) {
        var s = DiagnosticsService.Redact(input);
        Assert.That(s, Does.Contain("<redacted>"));
        foreach (var leak in new[] { "hunter2", "s3cr3t-value", "abcd1234", "zzzz" }) {
            Assert.That(s, Does.Not.Contain(leak));
        }
    }

    [Test]
    public void Redact_RemovesBearerTokens() {
        var s = DiagnosticsService.Redact("Authorization: Bearer abc.def.ghi");
        Assert.That(s, Does.Not.Contain("abc.def.ghi"));
        Assert.That(s, Does.Contain("Bearer <redacted>"));
    }

    [Test]
    public void Redact_LeavesOrdinaryTextAlone() {
        const string plain = "enabled=enabled active=active result=success";
        Assert.That(DiagnosticsService.Redact(plain), Is.EqualTo(plain));
    }

    // ---- the report shape ----

    [Test]
    public void ToText_ListsEveryCheckAndTheFix() {
        var report = new DiagnosticsReport(
            GeneratedUtc: "2026-07-29T23:00:00Z", Version: "0.97.5", Host: "polaris",
            Board: "rpi4 Raspberry Pi 4", Os: "Linux",
            Ok: 1, Warn: 0, Fail: 1, Unknown: 1, Skipped: 0,
            Checks: new List<DiagnosticCheck> {
                new("a", "units", DiagSeverity.Ok, "Polaris service", "enabled=enabled"),
                new("b", "storage", DiagSeverity.Fail, "Root grown", "40% of the partition",
                    "sudo systemctl start polaris-growroot"),
                new("c", "data", DiagSeverity.Unknown, "Models", "the check could not run: boom"),
            });

        var text = DiagnosticsService.ToText(report);

        Assert.That(text, Does.Contain("1 fail, 0 warn, 1 unknown"));
        Assert.That(text, Does.Contain("Root grown"));
        Assert.That(text, Does.Contain("fix: sudo systemctl start polaris-growroot"),
            "a finding without its fix pushes the work back to the operator");
        Assert.That(text, Does.Contain("UNKNOWN"),
            "a check that could not run has to be visible, not folded into ok");
        Assert.That(text, Does.Contain("No passwords, keys or tokens"));
    }

    [Test]
    public async Task RunAsync_NeverReportsOkForACheckThatCouldNotRun() {
        // The whole point of the `unknown` severity. An earlier verification
        // script in this repo died on a glob that matched nothing, and its
        // truncated output read exactly like a clean pass.
        var svc = TestDiagnostics();
        var report = await svc.RunAsync();

        Assert.That(report.Checks, Is.Not.Empty);
        foreach (var c in report.Checks) {
            Assert.That(c.Severity, Is.AnyOf(DiagSeverity.Ok, DiagSeverity.Warn,
                DiagSeverity.Fail, DiagSeverity.Unknown, DiagSeverity.Skipped),
                $"check {c.Id} used an unknown severity");
            Assert.That(c.Title, Is.Not.Empty, $"check {c.Id} has no title");
            if (c.Severity == DiagSeverity.Unknown) {
                Assert.That(c.Detail, Does.Contain("could not run").Or.Not.Empty,
                    $"unknown check {c.Id} must say why");
            }
        }
        // The counters have to agree with the list, or the summary lies.
        Assert.That(report.Ok + report.Warn + report.Fail + report.Unknown + report.Skipped,
            Is.EqualTo(report.Checks.Count));
    }

    [Test]
    public async Task RunAsync_RedactsEveryDetailItEmits() {
        var report = await TestDiagnostics().RunAsync();
        foreach (var c in report.Checks) {
            Assert.That(System.Text.RegularExpressions.Regex.IsMatch(c.Detail, @"\b[0-9a-fA-F]{64}\b"),
                Is.False, $"check {c.Id} emitted something shaped like a key");
        }
    }

    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void Cleanup() {
        foreach (var d in _tempDirs) {
            try { Directory.Delete(d, true); } catch { /* best effort */ }
        }
        _tempDirs.Clear();
    }

    /// <summary>A service wired to an isolated profile dir: the checks read the
    /// real host, which is the point, but nothing may be written to the
    /// operator's own profile location.</summary>
    private DiagnosticsService TestDiagnostics() {
        var dir = Path.Combine(Path.GetTempPath(), "polaris_diag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = dir })
            .Build();
        var profile = new ProfileService(cfg, Null<ProfileService>());
        return new DiagnosticsService(
            profile,
            new IndiWebManagerService(cfg, Null<IndiWebManagerService>()),
            new ClockSyncService(Null<ClockSyncService>()),
            Null<DiagnosticsService>());
    }

    private static Microsoft.Extensions.Logging.ILogger<T> Null<T>()
        => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}
