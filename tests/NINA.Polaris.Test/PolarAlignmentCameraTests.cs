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
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Polar alignment with a chosen camera: the imaging camera (as before), the
/// guide camera or the aux camera. Each solves at its own optics' scale.
/// </summary>
[TestFixture]
public class PolarAlignmentCameraTests {

    [TestCase(null, "main")]
    [TestCase("", "main")]
    [TestCase("MAIN", "main")]
    [TestCase("guide", "guide")]
    [TestCase(" Aux ", "imager:1")]
    [TestCase("imager:3", "imager:3")]
    [TestCase("imager:0", "main")]
    [TestCase("imager:x", "main")]
    [TestCase("something else", "main")]
    public void TheCameraNameIsNormalised(string? given, string expected) {
        Assert.That(PolarAlignmentService.NormalizeCamera(given), Is.EqualTo(expected));
    }

    [Test]
    public void ARunRemembersItsCamera_AndOldRequestsStayOnTheMainOne() {
        Assert.Multiple(() => {
            Assert.That(new PolarAlignmentOptions().Camera, Is.EqualTo("main"));
            Assert.That(new RudimentaryStartRequest(1, 2, null).Camera, Is.Null);
            Assert.That(new EquipmentProfile().PolarAlignCamera, Is.EqualTo("main"));
        });
    }

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Src(params string[] parts) {
        var all = new System.Collections.Generic.List<string> { Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris" };
        all.AddRange(parts);
        return File.ReadAllText(Path.GetFullPath(Path.Combine(all.ToArray())));
    }

    [Test]
    public void EveryCaptureAndSolveUsesTheJobsCamera() {
        var svc = Src("Services", "PolarAlignmentService.cs");
        Assert.Multiple(() => {
            Assert.That(svc, Does.Not.Contain("var camera = _equip.Camera;"), "a path still hard-wired to the main camera");
            Assert.That(Regex.Matches(svc, @"await GatedCapture\(").Count, Is.EqualTo(0));
            Assert.That(Regex.Matches(svc, @"await CaptureFor\(job, camera, ").Count, Is.EqualTo(6));
            Assert.That(Regex.Matches(svc, @"await SolveOnceAsync\(job, ").Count, Is.EqualTo(6));
        });
    }

    /// <summary>The scale handed to the solver comes from the optics behind
    /// the chosen camera; the main OTA's focal length on a guide-scope frame
    /// would be several times off.</summary>
    [Test]
    public void TheSolverGetsTheChosenCamerasFocalLength() {
        var svc = Src("Services", "PolarAlignmentService.cs");
        Assert.Multiple(() => {
            Assert.That(svc, Does.Contain("if (job.CameraSource == \"guide\") return rig.EffectiveGuiderFocalLengthMm;"));
            Assert.That(svc, Does.Contain("imagers[slot].FocalLengthMm"));
            Assert.That(svc, Does.Contain("ImagerCaptureGate.RunAsync(slot,"));
            Assert.That(svc, Does.Contain("var focal = FocalLengthFor(job);"));
        });
    }

    [Test]
    public void TheGuideCameraIsRefusedWhileTheGuiderUsesIt() {
        var svc = Src("Services", "PolarAlignmentService.cs");
        Assert.That(svc, Does.Contain("(ng.IsLooping || ng.IsGuiding || ng.IsCalibrating)"));
    }

    [Test]
    public void TheRigsChoiceIsTheDefault() {
        var ep = Src("Endpoints", "PolarAlignmentEndpoints.cs");
        Assert.Multiple(() => {
            Assert.That(ep, Does.Contain("Camera:          request?.Camera          ?? rig.PolarAlignCamera"));
            Assert.That(ep, Does.Contain("? rig.PolarAlignCamera : request.Camera"));
        });
    }
}
