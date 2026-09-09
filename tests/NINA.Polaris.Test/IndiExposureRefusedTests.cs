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
using NINA.INDI.Devices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// When a driver refuses an exposure it says why, and the operator has to be
/// told in those words.
///
/// The case these are written from (Discord, 2026-09-08): an SVBony SV105 on
/// indi_v4l2_ccd, asked for 2 s. The driver answered with three messages and
/// put CCD_EXPOSURE into Alert. Polaris was watching for a BLOB, so it waited,
/// the browser gave up first, and the operator was told "Request timed out" and
/// shown an empty preview — for a camera that had already explained, in one
/// line, that it tops out at half a second.
/// </summary>
[TestFixture]
public class IndiExposureRefusedTests {

    /// <summary>The exact three lines indi_v4l2_ccd sent, in order.</summary>
    private static readonly string[] Sv105 = {
        "[ERROR] Failed 2.000-second manual exposure, out of device bounds [0.000,0.500], and stacking is not ready.",
        "[ERROR] Configure grayscale and stacking in order to stack streamed frames up to 2.000 seconds.",
        "[WARNING] Failed 2.000-second manual exposure, no adequate control is registered.",
    };

    [Test]
    public void TheReasonSurvives() {
        var s = IndiCamera.SummariseDriverMessages(Sv105);
        Assert.That(s, Does.Contain("out of device bounds [0.000,0.500]"),
            "the bounds are the one fact that tells the operator what to do");
        Assert.That(s, Does.Contain("2.000-second"), "and what was asked for");
    }

    /// <summary>The tags are noise once this is a toast, and a red toast has
    /// already said "error".</summary>
    [Test]
    public void TheLevelTagsComeOff() {
        var s = IndiCamera.SummariseDriverMessages(Sv105);
        Assert.That(s, Does.Not.Contain("[ERROR]"));
        Assert.That(s, Does.Not.Contain("[WARNING]"));
    }

    /// <summary>Drivers restate themselves at a lower level. The ERROR lines
    /// carry the verdict, so a trailing WARNING must not dilute them.</summary>
    [Test]
    public void ErrorsWinOverWarnings() {
        var s = IndiCamera.SummariseDriverMessages(Sv105);
        Assert.That(s, Does.Not.Contain("no adequate control is registered"),
            "the WARNING restates the ERROR and adds nothing");
    }

    /// <summary>A driver that only warns still has to be heard: without this
    /// the operator gets an empty reason, which is where this started.</summary>
    [Test]
    public void AWarningAloneIsStillReported() {
        var s = IndiCamera.SummariseDriverMessages(new[] {
            "[WARNING] Bulb capture failed, camera did not respond",
        });
        Assert.That(s, Is.EqualTo("Bulb capture failed, camera did not respond"));
    }

    [Test]
    public void EmptyInputsAreNotAnError() {
        Assert.That(IndiCamera.SummariseDriverMessages(null), Is.Empty);
        Assert.That(IndiCamera.SummariseDriverMessages(Array.Empty<string>()), Is.Empty);
        Assert.That(IndiCamera.SummariseDriverMessages(new[] { "", "   ", null! }), Is.Empty);
    }

    /// <summary>Several messages arrive as separate elements; they read as one
    /// line, with no double spaces or stray newlines from the wire.</summary>
    [Test]
    public void TheResultIsOneTidyLine() {
        var s = IndiCamera.SummariseDriverMessages(new List<string> {
            "[ERROR]  Failed\n exposure ", "[ERROR]\tout of bounds",
        });
        Assert.That(s, Is.EqualTo("Failed exposure out of bounds"));
    }
}
