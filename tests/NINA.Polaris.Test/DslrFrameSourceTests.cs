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

using NINA.INDI.Devices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// What a DSLR frame's pixels really are, and whether the operator is told.
///
/// A Canon R8 on a field rig delivered 8-bit data for a whole evening: every
/// value a multiple of 257, 145 distinct levels in a 16-bit FITS, stars in
/// visible steps and autofocus measuring a quantised profile. Polaris had
/// fallen back from the RAW decoder to the embedded preview JPEG, which is a
/// legitimate fallback, and said nothing anywhere: not in the log, not on
/// screen. The night was spent looking at the camera.
///
/// Three different causes produce that identical picture, and each needs a
/// different action, so the message has to name which one it was.
/// </summary>
[TestFixture]
public class DslrFrameSourceTests {

    [Test]
    public void AJpegFromTheCamera_PointsAtTheCamera() {
        var why = IndiCamera.DslrFallbackReason(blobIsJpeg: true, librawAvailable: true);

        Assert.Multiple(() => {
            Assert.That(why, Does.Contain("JPEG"));
            Assert.That(why, Does.Contain("image quality"),
                "nothing on the host can recover what the camera never recorded");
            Assert.That(why, Does.Not.Contain("libraw"),
                "blaming the decoder here sends the operator to the wrong place");
        });
    }

    [Test]
    public void ARawWithNoDecoder_PointsAtTheHost() {
        var why = IndiCamera.DslrFallbackReason(blobIsJpeg: false, librawAvailable: false);

        Assert.Multiple(() => {
            Assert.That(why, Does.Contain("libraw"));
            Assert.That(why, Does.Contain("install"), "this one is fixable with a package");
        });
    }

    /// <summary>The case that is nobody's mistake: libraw is there and refuses
    /// the file, which on a recent body usually means the camera is newer than
    /// the distribution's libraw.</summary>
    [Test]
    public void ARawTheDecoderRefuses_SaysSo() {
        var why = IndiCamera.DslrFallbackReason(blobIsJpeg: false, librawAvailable: true);

        Assert.Multiple(() => {
            Assert.That(why, Does.Contain("libraw"));
            Assert.That(why, Does.Contain("installed but could not decode"));
            Assert.That(why, Does.Contain("newer"));
            Assert.That(why, Does.Not.Contain("install libraw"),
                "it is installed; telling them to install it again wastes the night");
        });
    }

    [Test]
    public void TheThreeCausesAreDistinguishable() {
        var a = IndiCamera.DslrFallbackReason(true, true);
        var b = IndiCamera.DslrFallbackReason(false, false);
        var c = IndiCamera.DslrFallbackReason(false, true);

        Assert.That(new[] { a, b, c }, Is.Unique,
            "one message for three causes is what made this invisible in the first place");
    }

    /// <summary>The report carries the real precision, not the container's. The
    /// FITS says 16 bits either way, which is exactly how an 8-bit frame went
    /// unnoticed.</summary>
    [Test]
    public void TheReportedDepthIsTheDataDepth_NotTheContainers() {
        var jpeg = new IndiCamera.DslrFrameSource("embedded-jpeg", ".cr3", "whatever", 8);
        var raw = new IndiCamera.DslrFrameSource("raw", ".cr3", "Decoded with libraw.", 16);

        Assert.Multiple(() => {
            Assert.That(jpeg.Bits, Is.EqualTo(8));
            Assert.That(raw.Bits, Is.EqualTo(16));
            Assert.That(jpeg.Source, Is.Not.EqualTo(raw.Source));
        });
    }
}
