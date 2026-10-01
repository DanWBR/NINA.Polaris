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

using NINA.Polaris.Services.Broadcast;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The four things the first operator to broadcast a real session asked for:
/// smaller text, a picture that fills the frame, a card that uses the column
/// it is given, and a corner camera that can be an RTSP stream.
/// </summary>
[TestFixture]
public class BroadcastLayoutOptionsTests {

    private static BroadcastLayout L(int scale = 100, bool full = false)
        => BroadcastLayout.For(1280, 720, hasHeader: true, scale, full);

    // --- text size --------------------------------------------------------

    [Test]
    public void OneHundredPercentIsExactlyWhatItAlwaysWas() {
        // The default must not move anyone's existing broadcast.
        var plain = BroadcastLayout.For(1280, 720, hasHeader: true);
        var scaled = L(100);
        Assert.That(scaled.TitleSize, Is.EqualTo(plain.TitleSize));
        Assert.That(scaled.BodySize, Is.EqualTo(plain.BodySize));
        Assert.That(scaled.BannerSize, Is.EqualTo(plain.BannerSize));
    }

    [Test]
    public void SmallerMeansSmallerEverywhereOnTheCard() {
        var big = L(100);
        var small = L(70);
        Assert.That(small.TitleSize, Is.LessThan(big.TitleSize));
        Assert.That(small.SubtitleSize, Is.LessThan(big.SubtitleSize));
        Assert.That(small.ChipSize, Is.LessThan(big.ChipSize));
        Assert.That(small.BodySize, Is.LessThan(big.BodySize));
        Assert.That(small.CreditSize, Is.LessThan(big.CreditSize));
        Assert.That(small.PipLabelSize, Is.LessThan(big.PipLabelSize));
    }

    [Test]
    public void TheScaleIsProportionalOnTheCard() {
        var half = L(50);
        var full = L(100);
        Assert.That(half.BodySize, Is.EqualTo(full.BodySize / 2).Within(0.01));
    }

    [Test]
    public void TheStripsNeverOutgrowTheirOwnBand() {
        // The header and banner are fixed-height bands. Letting their type
        // scale freely would push the rig line out of the strip it lives in,
        // which is worse than text that is a little small.
        var l = L(200);
        Assert.That(l.HeaderTitleSize, Is.LessThanOrEqualTo(l.Header.Height * 0.45f));
        Assert.That(l.HeaderRigSize, Is.LessThanOrEqualTo(l.Header.Height * 0.3f));
        Assert.That(l.BannerSize, Is.LessThanOrEqualTo(l.Banner.Height * 0.6f));
    }

    [Test]
    public void AnAbsurdScaleIsClampedBecauseItComesFromStoredConfiguration() {
        foreach (var bad in new[] { 0, -40, 5000 }) {
            var l = L(bad);
            Assert.That(l.BodySize, Is.GreaterThan(0), bad.ToString());
            Assert.That(l.TitleSize, Is.LessThan(500), bad.ToString());
        }
    }

    // --- fill -------------------------------------------------------------

    [Test]
    public void FittingASquareSensorLeavesTheSidesEmpty() {
        // The case the operator photographed: a third of a 16:9 frame black.
        var into = new BroadcastRect(0, 0, 1280, 720);
        var r = BroadcastLayout.Letterbox(1200, 1200, into);
        Assert.That(r.Height, Is.EqualTo(720));
        Assert.That(r.Width, Is.EqualTo(720));
        Assert.That(r.X, Is.GreaterThan(0), "centred, so there are bars");
    }

    [Test]
    public void FillingItCoversTheWholeFrame() {
        var into = new BroadcastRect(0, 0, 1280, 720);
        var r = BroadcastLayout.Cover(1200, 1200, into);
        Assert.That(r.Width, Is.GreaterThanOrEqualTo(1280));
        Assert.That(r.Height, Is.GreaterThanOrEqualTo(720));
        Assert.That(r.X, Is.LessThanOrEqualTo(0));
        Assert.That(r.Y, Is.LessThanOrEqualTo(0));
    }

    [Test]
    public void FillKeepsTheShapeOfThePicture() {
        // Covering by stretching would be easy and wrong: round stars.
        var r = BroadcastLayout.Cover(1200, 1600, new BroadcastRect(0, 0, 1280, 720));
        Assert.That((double)r.Width / r.Height, Is.EqualTo(1200.0 / 1600).Within(0.01));
    }

    [Test]
    public void AWideSensorIsUnchangedByEitherModeWhenItMatchesTheFrame() {
        var into = new BroadcastRect(0, 0, 1280, 720);
        var fit = BroadcastLayout.Letterbox(1920, 1080, into);
        var fill = BroadcastLayout.Cover(1920, 1080, into);
        Assert.That(fit.Width, Is.EqualTo(1280));
        Assert.That(fill.Width, Is.EqualTo(1280));
    }

    [Test]
    public void PictureFitParsesAndRejects() {
        Assert.That(PictureFits.Parse("fill"), Is.EqualTo(PictureFits.Fill));
        Assert.That(PictureFits.Parse("FILL"), Is.EqualTo(PictureFits.Fill));
        Assert.That(PictureFits.Parse("nonsense"), Is.EqualTo(PictureFits.Fit), "unknown falls back to fit");
        Assert.That(PictureFits.Parse(null), Is.EqualTo(PictureFits.Fit));
        Assert.That(PictureFits.IsValid("fill"), Is.True);
        Assert.That(PictureFits.IsValid("stretch"), Is.False);
    }

    // --- the card ---------------------------------------------------------

    [Test]
    public void TheCardOnlyClaimsTheColumnWhenAsked() {
        Assert.That(L(100, full: false).CardFullHeight, Is.False);
        Assert.That(L(100, full: true).CardFullHeight, Is.True);
    }

    [Test]
    public void TheRoomTheCardIsGivenRunsFromTheHeaderToTheBanner() {
        var l = L(100, full: true);
        Assert.That(l.Card.Y, Is.GreaterThanOrEqualTo(l.Header.Bottom));
        Assert.That(l.Card.Bottom, Is.LessThanOrEqualTo(l.Banner.Y));
        // Worth having: on a 720p frame with a header this is most of the
        // height, which is the whole reason the option exists.
        Assert.That(l.Card.Height, Is.GreaterThan(l.Height / 2));
    }

    [Test]
    public void TheCardNeverOverlapsTheCornerPicture() {
        // They are on opposite sides, and a full-height card made it worth
        // checking rather than assuming.
        foreach (var full in new[] { false, true }) {
            var l = L(100, full);
            Assert.That(l.Card.X, Is.GreaterThan(l.Pip.Right), full.ToString());
        }
    }

    // --- rtsp -------------------------------------------------------------

    [Test]
    public void AStreamUrlIsRecognisedWhateverTheScheme() {
        foreach (var u in new[] { "rtsp://192.168.1.50:554/stream1",
                                  "RTSP://cam/live", "rtsps://cam/live", "rtmp://cam/live" })
            Assert.That(BroadcastPipService.IsStreamUrl(u), Is.True, u);
    }

    [Test]
    public void ASnapshotUrlIsNotAStream() {
        foreach (var u in new[] { "http://cam/snapshot.jpg", "https://cam/snap", null })
            Assert.That(BroadcastPipService.IsStreamUrl(u), Is.False, u ?? "null");
    }

    [Test]
    public void TheGrabAsksForOneFrameOverTcp() {
        var args = NINA.Polaris.Services.External.FfmpegService.GrabArgs("rtsp://cam/live");
        var flat = string.Join(" ", args);
        // TCP on purpose: a UDP frame over WiFi arrives torn often enough to
        // be the normal case, and a torn JPEG is a corner full of grey blocks.
        Assert.That(flat, Does.Contain("-rtsp_transport tcp"));
        Assert.That(flat, Does.Contain("-frames:v 1"));
        Assert.That(args, Does.Contain("rtsp://cam/live"));
        Assert.That(args[^1], Is.EqualTo("pipe:1"), "the frame comes back on stdout");
        // It must give up rather than block the corner picture for ever.
        Assert.That(flat, Does.Contain("-rw_timeout"));
    }

    [Test]
    public void ThePasswordInAnRtspUrlIsNotLogged() {
        // Camera URLs almost always carry credentials, and this one is logged
        // when a grab fails.
        var redacted = NINA.Polaris.Services.External.FfmpegService.Redact(
            "rtsp://admin:hunter2@192.168.1.50:554/stream1");
        Assert.That(redacted, Does.Not.Contain("hunter2").And.Not.Contain("admin"));
        Assert.That(redacted, Does.Contain("192.168.1.50"), "the address itself is still useful");
    }

    [Test]
    public void AUrlWithNoCredentialsSurvivesRedactionIntact() {
        const string u = "rtsp://192.168.1.50:554/stream1";
        Assert.That(NINA.Polaris.Services.External.FfmpegService.Redact(u), Is.EqualTo(u));
    }
}
