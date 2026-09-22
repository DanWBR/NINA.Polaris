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
using NUnit.Framework;
using NINA.Camera.ZwoSdk;

namespace NINA.Polaris.Test;

/// <summary>
/// The still-capture recovery on the ZWO native path. A guide camera that had
/// been left in a bad state by another program (external PHD2, 2026-09-21)
/// answered every control read and failed every exposure, and the capture path
/// threw on the first failure and never tried anything else: 45 identical
/// "ASI exposure failed." lines in 20 minutes, with the message naming none of
/// the state that would have explained it. Only unplugging the camera cured it,
/// which nothing in the product said.
/// </summary>
[TestFixture]
public class AsiStillRecoveryTests {

    [Test]
    public void OneRetryAfterAReopen() {
        Assert.That(AsiSdkCamera.StillAttempts, Is.EqualTo(2),
            "the first attempt plus one after reopening the handle");
    }

    /// <summary>A failed exposure and a timeout are worth reopening for. A
    /// cancellation is the caller going away, and repeating the request would
    /// fight whoever cancelled it.</summary>
    [Test]
    public void RecoverableCoversTheSdkFailuresAndNotCancellation() {
        Assert.Multiple(() => {
            Assert.That(AsiSdkCamera.IsRecoverable(new InvalidOperationException("boom")), Is.True);
            Assert.That(AsiSdkCamera.IsRecoverable(new TimeoutException()), Is.True);
            Assert.That(AsiSdkCamera.IsRecoverable(new OperationCanceledException()), Is.False);
            Assert.That(AsiSdkCamera.IsRecoverable(new System.IO.IOException()), Is.False);
        });
    }

    /// <summary>The streaming USB posture asks for 40, which is not a value
    /// every model accepts. An ASI120MM Mini reports min 40, max 100, default
    /// 50; a model with a higher floor must not be written below it.</summary>
    [Test]
    public void BandwidthIsClampedToWhatTheModelReports() {
        Assert.That(AsiSdkCamera.ClampBandwidth(40, 40, 100), Is.EqualTo(40));
        Assert.That(AsiSdkCamera.ClampBandwidth(40, 80, 100), Is.EqualTo(80), "respect a higher floor");
        Assert.That(AsiSdkCamera.ClampBandwidth(40, 0, 30), Is.EqualTo(30), "and a lower ceiling");
    }

    /// <summary>With no caps read from the camera, ask for what the stream fix
    /// asked for rather than inventing a bound.</summary>
    [Test]
    public void BandwidthPassesThroughWhenTheCapsAreUnknown() {
        Assert.That(AsiSdkCamera.ClampBandwidth(40, 0, 0), Is.EqualTo(40));
        Assert.That(AsiSdkCamera.ClampBandwidth(40, 50, 10), Is.EqualTo(40), "nonsense caps are ignored");
    }
}
