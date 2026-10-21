// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.

using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>Regression coverage for restoring the Accessories rotator choice
/// when INDI discovery and backend auto-connect finish after the rig loads.</summary>
[TestFixture]
public class RotatorSelectionHydrationTests {
    private static string Here([CallerFilePath] string path = "") => path;

    private static string Read(params string[] parts) {
        var path = Path.GetFullPath(Path.Combine(
            new[] { Path.GetDirectoryName(Here())!, "..", ".." }.Concat(parts).ToArray()));
        Assert.That(File.Exists(path), $"missing {path}");
        return File.ReadAllText(path);
    }

    [Test]
    public void LateIndiOptionSelectsTheSavedOrConnectedRotator() {
        var html = Read("src", "NINA.Polaris", "wwwroot", "index.html");

        Assert.That(html, Does.Contain(
            @":selected=""(equipRotatorChoice ? equipRotatorChoice : rotator.name) === dev.name"""));
    }

    [Test]
    public void EquipmentChoiceSyncIncludesRoleFilteredRotators() {
        var js = Read("src", "NINA.Polaris", "wwwroot", "js", "app.js");
        var start = js.IndexOf("_syncEquipChoicesFromConnected() {", StringComparison.Ordinal);

        Assert.That(start, Is.GreaterThanOrEqualTo(0), "equipment choice sync is gone");
        var body = js.Substring(start, Math.Min(5000, js.Length - start));
        Assert.Multiple(() => {
            Assert.That(body, Does.Contain("const rotatorNames = new Set(this.rotatorIndiDevices.map(d => d.name));"));
            Assert.That(body, Does.Contain("this.equipRotatorChoice = this.rotator.name;"));
        });
    }
}
