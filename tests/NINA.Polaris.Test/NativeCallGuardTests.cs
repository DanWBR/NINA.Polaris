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

using System.Reflection;
using NUnit.Framework;
using NINA.Image.NativeLibs;

namespace NINA.Polaris.Test;

/// <summary>A native SDK that crashes Polaris must not crash it again on the
/// next start: the marker its call leaves behind quarantines it.</summary>
[TestFixture]
[NonParallelizable]
public class NativeCallGuardTests {
    private string _dir = "";
    private string? _saved;

    private static string Version =>
        (Assembly.GetEntryAssembly() ?? typeof(NativeCallGuard).Assembly).GetName().Version?.ToString() ?? "0";

    [SetUp]
    public void SetUp() {
        _saved = Environment.GetEnvironmentVariable(NativeCallGuard.DirEnvVar);
        _dir = Path.Combine(Path.GetTempPath(), "polaris-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(NativeCallGuard.DirEnvVar, _dir);
        NativeCallGuard.ResetForTests();
    }

    [TearDown]
    public void TearDown() {
        Environment.SetEnvironmentVariable(NativeCallGuard.DirEnvVar, _saved);
        NativeCallGuard.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>What a process that died inside a guarded call leaves behind.</summary>
    private void LeaveMarkerOfADeadProcess(string sdk, string what, string? version = null) {
        File.WriteAllText(Path.Combine(_dir, $"{sdk}.999999.1.inflight"),
            $"{what}|2026-10-14 06:00 UTC|{version ?? Version}|999999");
    }

    [Test]
    public void ACallThatReturns_LeavesNothingBehind() {
        var v = NativeCallGuard.Run("qhy", "listing cameras", () => 42);
        Assert.That(v, Is.EqualTo(42));
        Assert.That(Directory.GetFiles(_dir), Is.Empty);
        Assert.That(NativeCallGuard.QuarantineReason("qhy"), Is.Null);
    }

    [Test]
    public void ACallThatThrows_IsNotACrash() {
        Assert.Throws<DllNotFoundException>(() =>
            NativeCallGuard.Run<int>("touptek", "loading the SDK", () => throw new DllNotFoundException()));
        Assert.That(Directory.GetFiles(_dir), Is.Empty);
        Assert.That(NativeCallGuard.QuarantineReason("touptek"), Is.Null);
    }

    [Test]
    public void AMarkerFromADeadProcess_QuarantinesThatSdkOnly() {
        LeaveMarkerOfADeadProcess("touptek", "listing cameras");

        var why = NativeCallGuard.QuarantineReason("touptek");

        Assert.That(why, Does.Contain("touptek").And.Contain("listing cameras"));
        Assert.That(NativeCallGuard.QuarantineReason("qhy"), Is.Null);
        Assert.That(Directory.GetFiles(_dir, "*.inflight"), Is.Empty);
    }

    [Test]
    public void AQuarantinedSdk_IsNotCalled() {
        LeaveMarkerOfADeadProcess("qhy", "opening the camera");
        bool called = false;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NativeCallGuard.Run("qhy", "opening the camera", () => { called = true; }));
        Assert.That(called, Is.False);
        Assert.That(ex!.Message, Does.StartWith("The qhy SDK crashed Polaris"));
    }

    [Test]
    public void ReEnabling_LiftsTheQuarantine() {
        LeaveMarkerOfADeadProcess("qhy", "opening the camera");
        Assert.That(NativeCallGuard.QuarantineReason("qhy"), Is.Not.Null);

        Assert.That(NativeCallGuard.Clear("qhy"), Is.True);

        Assert.That(NativeCallGuard.QuarantineReason("qhy"), Is.Null);
        Assert.That(NativeCallGuard.Run("qhy", "opening the camera", () => 1), Is.EqualTo(1));
    }

    [Test]
    public void AnUpdate_LiftsTheQuarantine() {
        LeaveMarkerOfADeadProcess("touptek", "listing cameras", version: "0.0.0.1");
        Assert.That(NativeCallGuard.QuarantineReason("touptek"), Is.Null);
    }

    [Test]
    public void TheMarkerOfThisProcess_IsNotACrash() {
        File.WriteAllText(Path.Combine(_dir, $"qhy.{Environment.ProcessId}.7.inflight"),
            $"listing cameras|2026-10-14 06:00 UTC|{Version}|{Environment.ProcessId}");
        Assert.That(NativeCallGuard.QuarantineReason("qhy"), Is.Null);
    }

    [Test]
    public void Listing_ShowsEveryQuarantinedSdk() {
        LeaveMarkerOfADeadProcess("qhy", "opening the camera");
        File.WriteAllText(Path.Combine(_dir, "touptek.999998.1.inflight"),
            $"listing cameras|2026-10-14 06:00 UTC|{Version}|999998");
        Assert.That(NativeCallGuard.Quarantined().Select(q => q.Sdk), Is.EquivalentTo(new[] { "qhy", "touptek" }));
    }
}
