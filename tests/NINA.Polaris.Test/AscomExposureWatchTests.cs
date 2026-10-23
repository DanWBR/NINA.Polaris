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

using NUnit.Framework;
using NINA.Ascom.Com;

namespace NINA.Polaris.Test;

/// <summary>An ATR2600C on the ToupTek ASCOM driver took five 60 s darks, then
/// every exposure sat until the exposure + 120 s timeout, twice per frame,
/// while the driver went idle and threw on every read.</summary>
[TestFixture]
public class AscomExposureWatchTests {
    private const int Idle = 0, Exposing = 2, Download = 4, Error = 5;
    private const double Stall = AscomExposureWatch.StallSeconds;

    [Test]
    public void ANormalExposureAndReadout_IsNeverGivenUp() {
        var w = new AscomExposureWatch(60, stateKnownToWork: true);
        for (double t = 0; t < 60; t += 1)
            Assert.That(w.Observe(t, false, null, Exposing, null), Is.Null);
        for (double t = 60; t < 150; t += 1)
            Assert.That(w.Observe(t, false, null, Download, null), Is.Null, "a slow download is not a stall");
    }

    [Test]
    public void AnErrorState_FailsAtOnceWithTheDriverMessage() {
        var w = new AscomExposureWatch(60, stateKnownToWork: true);
        Assert.That(w.Observe(5, false, null, Exposing, null), Is.Null);
        var why = w.Observe(6, true, "USB transfer failed", Error, null);
        Assert.That(why, Does.Contain("error").And.Contain("USB transfer failed"));
    }

    [Test]
    public void IdleAfterTheExposureEnds_FailsOnceTheStallHasLasted() {
        var w = new AscomExposureWatch(60, stateKnownToWork: true);
        Assert.That(w.Observe(30, false, null, Idle, null), Is.Null, "idle during the exposure is not judged");
        Assert.That(w.Observe(60, false, null, Idle, null), Is.Null);
        Assert.That(w.Observe(60 + Stall - 1, false, null, Idle, null), Is.Null);
        Assert.That(w.Observe(60 + Stall, false, null, Idle, null), Does.Contain("idle"));
    }

    [Test]
    public void AStallThatEnds_StartsTheCountAgain() {
        var w = new AscomExposureWatch(10, stateKnownToWork: true);
        Assert.That(w.Observe(10, false, null, Idle, null), Is.Null);
        Assert.That(w.Observe(30, false, null, Download, null), Is.Null);
        Assert.That(w.Observe(31, false, null, Idle, null), Is.Null);
        Assert.That(w.Observe(31 + Stall - 1, false, null, Idle, null), Is.Null);
    }

    [Test]
    public void ADriverThatStopsAnswering_FailsWithItsMessage() {
        var w = new AscomExposureWatch(60, stateKnownToWork: true);
        Assert.That(w.Observe(70, false, null, null, "Camera not connected"), Is.Null);
        var why = w.Observe(70 + Stall, false, null, null, "Camera not connected");
        Assert.That(why, Does.Contain("stopped answering").And.Contain("Camera not connected"));
    }

    [Test]
    public void ADriverWithoutCameraState_IsJudgedOnImageReadyAlone() {
        var w = new AscomExposureWatch(60, stateKnownToWork: false);
        Assert.That(w.Observe(200, false, null, null, "Property CameraState is not implemented"), Is.Null);
    }

    [Test]
    public void ImageReadyFailing_FailsAfterTheStallEvenDuringTheExposure() {
        var w = new AscomExposureWatch(300, stateKnownToWork: false);
        Assert.That(w.Observe(10, true, "Not connected", null, null), Is.Null);
        Assert.That(w.Observe(10 + Stall, true, "Not connected", null, null), Does.Contain("ImageReady"));
    }
}
