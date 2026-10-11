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

using System.Runtime.CompilerServices;
using NINA.Polaris.Services.Broadcast;
using NUnit.Framework;
using SkiaSharp;

namespace NINA.Polaris.Test;

/// <summary>
/// The geometry of a broadcast frame. Pure arithmetic, so the rules that
/// decide whether the overlay is readable on a phone can be checked at every
/// resolution without drawing anything.
/// </summary>
[TestFixture]
public class BroadcastLayoutTests {

    [TestCase(854, 480)]
    [TestCase(1280, 720)]
    [TestCase(1920, 1080)]
    public void EverythingFitsOnTheFrameWithARoomToSpare(int w, int h) {
        var l = BroadcastLayout.For(w, h);
        Assert.That(l.Card.Right, Is.LessThanOrEqualTo(w - l.Pad + 1), "the card touches the right edge");
        Assert.That(l.Card.X, Is.GreaterThan(w / 2), "the card is on the right, and not over half the picture");
        Assert.That(l.Banner.Bottom, Is.LessThanOrEqualTo(h - l.Pad + 1));
        Assert.That(l.Card.Bottom, Is.LessThanOrEqualTo(l.Banner.Y), "the card must not run into the banner");
        Assert.That(l.CardThumb.Right, Is.LessThanOrEqualTo(l.Card.Right - l.CardInset + 1));
        Assert.That(l.CardTextWidth, Is.GreaterThan(0));
    }

    [Test]
    public void TypeAndPanelsScaleWithTheFrame() {
        // The same layout has to read at 480p on a phone over a hotspot and at
        // 1080p full screen, so nothing may be a fixed pixel count.
        var small = BroadcastLayout.For(854, 480);
        var large = BroadcastLayout.For(1920, 1080);
        Assert.That(large.TitleSize, Is.GreaterThan(small.TitleSize));
        Assert.That(large.BannerSize, Is.GreaterThan(small.BannerSize));
        Assert.That(large.Card.Width, Is.GreaterThan(small.Card.Width));
        // And the card takes about the same SHARE of the frame at both ends,
        // which is what keeps it readable rather than merely bigger.
        Assert.That((double)large.Card.Width / large.Width,
            Is.EqualTo((double)small.Card.Width / small.Width).Within(0.03));
    }

    [TestCase(854, 480)]
    [TestCase(1280, 720)]
    [TestCase(1920, 1080)]
    public void TheHeaderPushesTheCardDownInsteadOfSittingOnIt(int w, int h) {
        var without = BroadcastLayout.For(w, h, hasHeader: false);
        Assert.That(without.Header.IsEmpty, Is.True);
        Assert.That(without.Card.Y, Is.EqualTo(without.Pad));

        var with = BroadcastLayout.For(w, h, hasHeader: true);
        Assert.That(with.Header.IsEmpty, Is.False);
        Assert.That(with.Header.Width, Is.EqualTo(with.Banner.Width), "top and bottom strips line up");
        Assert.That(with.Card.Y, Is.GreaterThanOrEqualTo(with.Header.Bottom),
            "the card must start below the header, not under it");
        Assert.That(with.Card.Bottom, Is.LessThanOrEqualTo(with.Banner.Y));
        Assert.That(with.Card.Height, Is.LessThan(without.Card.Height), "and it gives up the room");
        Assert.That(with.Card.Height, Is.GreaterThan(h / 3), "but not so much that it cannot hold a card");
        Assert.That(with.HeaderTitleSize, Is.GreaterThan(with.HeaderRigSize),
            "the title leads and the equipment is secondary");
    }

    [TestCase(854, 480)]
    [TestCase(1280, 720)]
    [TestCase(1920, 1080)]
    public void TheSecondPictureSitsInTheOneFreeCorner(int w, int h) {
        var l = BroadcastLayout.For(w, h, hasHeader: true);
        Assert.That(l.Pip.X, Is.EqualTo(l.Pad), "bottom left, against the margin");
        Assert.That(l.Pip.Bottom, Is.LessThanOrEqualTo(l.Banner.Y), "and clear of the banner");
        Assert.That(l.Pip.Right, Is.LessThan(l.Card.X), "and clear of the object card");
        Assert.That(l.Pip.Y, Is.GreaterThan(l.Header.Bottom), "and below the header");
        // Big enough to make out a horizon, small enough to leave the picture
        // the frame is actually about.
        Assert.That(l.Pip.Width, Is.LessThan(w / 3));
        Assert.That(l.PipLabelSize, Is.GreaterThan(7f));
    }

    [Test]
    public void TheSecondPictureIsFourThree() {
        // An all sky lens puts a round image in a square frame and a webcam is
        // 16:9. Neither is cropped, so the slot only has to be a sane shape.
        var l = BroadcastLayout.For(1280, 720, hasHeader: true);
        Assert.That((double)l.Pip.Width / l.Pip.Height, Is.EqualTo(4.0 / 3).Within(0.05));
    }

    [Test]
    public void TheCutoutSlotIsWiderThanItIsTall() {
        // A square cutout at card width takes half the panel, and with a
        // header above there is no longer half a panel to spare.
        var l = BroadcastLayout.For(1280, 720, hasHeader: true);
        Assert.That(l.CardThumb.Width, Is.GreaterThan(l.CardThumb.Height));
        Assert.That(l.CardThumb.Height, Is.LessThan(l.Card.Height / 2));
    }

    [Test]
    public void ACutoutFillsItsSlotRatherThanLeavingBarsInsideThePanel() {
        var slot = new BroadcastRect(10, 10, 200, 124);
        var square = BroadcastLayout.Cover(1000, 1000, slot);
        Assert.That(square.Width, Is.GreaterThanOrEqualTo(slot.Width));
        Assert.That(square.Height, Is.GreaterThanOrEqualTo(slot.Height));
        // Centred, so what the crop loses is the top and bottom in equal
        // measure, and the cutouts are centred on the object.
        Assert.That(square.X + square.Width / 2, Is.EqualTo(slot.X + slot.Width / 2).Within(1));
        Assert.That(square.Y + square.Height / 2, Is.EqualTo(slot.Y + slot.Height / 2).Within(1));

        Assert.That(BroadcastLayout.Cover(0, 0, slot).IsEmpty, Is.True);
    }

    [Test]
    public void ARidiculousCanvasStillProducesAUsableLayout() {
        var tiny = BroadcastLayout.For(1, 1);
        Assert.That(tiny.Card.Width, Is.GreaterThan(0));
        Assert.That(tiny.TitleSize, Is.GreaterThan(0));
        Assert.That(tiny.Banner.Height, Is.GreaterThan(0));
    }

    [Test]
    public void ThePictureIsFittedWhole_NeverCropped() {
        // A sensor is almost never 16:9. Cropping to fill would cut the top
        // and bottom off every frame, which on a tall galaxy is the galaxy.
        var canvas = new BroadcastRect(0, 0, 1280, 720);

        var fourThree = BroadcastLayout.Letterbox(4656, 3520, canvas);
        Assert.That(fourThree.Height, Is.EqualTo(720), "it is taller than 16:9, so height is the limit");
        Assert.That(fourThree.Width, Is.LessThan(1280));
        Assert.That(fourThree.X, Is.GreaterThan(0), "and it is centred");
        Assert.That(fourThree.X + fourThree.Width, Is.EqualTo(1280 - fourThree.X).Within(1));

        var wide = BroadcastLayout.Letterbox(4000, 1000, canvas);
        Assert.That(wide.Width, Is.EqualTo(1280));
        Assert.That(wide.Y, Is.GreaterThan(0));

        var exact = BroadcastLayout.Letterbox(1920, 1080, canvas);
        Assert.That(exact, Is.EqualTo(new BroadcastRect(0, 0, 1280, 720)));
    }

    [Test]
    public void ANonsensePictureSizeDoesNotThrow() {
        var canvas = new BroadcastRect(0, 0, 1280, 720);
        Assert.That(BroadcastLayout.Letterbox(0, 0, canvas).IsEmpty, Is.True);
        Assert.That(BroadcastLayout.Letterbox(-5, 10, canvas).IsEmpty, Is.True);
        Assert.That(BroadcastLayout.Letterbox(100, 100, new BroadcastRect(0, 0, 0, 0)).IsEmpty, Is.True);
    }
}

/// <summary>
/// Drawing a broadcast frame. The assertions read the pixels that come out
/// rather than the renderer's account of what it did: the card has to be
/// somewhere, over something, and the picture must not have been stretched.
/// </summary>
[TestFixture]
[NonParallelizable]
public class FrameComposerTests {

    private const int W = 854, H = 480;
    private BroadcastFonts _fonts = null!;
    private BroadcastLayout _layout = null!;

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string WebRoot => Path.Combine(RepoRoot(), "src", "NINA.Polaris", "wwwroot");

    [SetUp]
    public void SetUp() {
        _fonts = new BroadcastFonts(WebRoot);
        _layout = BroadcastLayout.For(W, H);
    }

    [TearDown]
    public void TearDown() => _fonts.Dispose();

    private static ObjectCard Card(string? thumb = null, string description = "A stellar nursery 1,344 light years away, and the closest region of massive star formation to Earth.") =>
        new() {
            TargetName = "M42",
            ThumbnailPath = thumb,
            Copy = ObjectCardText.Compose(new ObjectCardFacts {
                CommonName = "Orion Nebula", CatalogId = "M42",
                Aliases = new[] { "NGC 1976" }, Type = "Emission nebula",
                Constellation = "Orion", Magnitude = 4.0, SizeArcmin = 85
            }, description, null)
        };

    private static SKBitmap Picture(int w, int h, byte grey) {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var c = new SKCanvas(bmp);
        c.Clear(new SKColor(grey, grey, grey));
        return bmp;
    }

    private static (byte R, byte G, byte B) At(byte[] rgb, int x, int y) {
        var i = (y * W + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    [Test]
    public void TheBundledTypefaceIsTheOneItUses() {
        // Without it the overlay depends on the host having fonts installed,
        // and a minimal Debian image for a headless board often has none: the
        // card would come out blank on exactly the installs this is for.
        Assert.That(_fonts.Source, Does.Contain("IBMPlexSans-Regular.ttf"),
            "the TrueType files must ship next to the woff2 ones the browser uses");
    }

    [Test]
    public void AFrameIsTheRightNumberOfBytes() {
        using var composer = new FrameComposer(_layout, _fonts);
        var rgb = composer.Compose(null, null, null);
        Assert.That(rgb.Length, Is.EqualTo(W * H * 3), "ffmpeg is reading rgb24 of exactly this size");
    }

    [Test]
    public void TheCardIsDrawnWhereTheCardGoes() {
        using var composer = new FrameComposer(_layout, _fonts);
        var picture = Picture(1600, 900, 200);
        var rgb = composer.Compose(picture, Card(), "M42 · 120s · 14 frames");
        picture.Dispose();

        // Over the picture, on the right, the panel darkens what is behind it.
        var insideCard = At(rgb, _layout.Card.X + _layout.Card.Width / 2, _layout.Card.Y + _layout.CardInset / 2);
        var leftOfCard = At(rgb, _layout.Card.X / 2, _layout.Card.Y + _layout.CardInset / 2);
        Assert.That(leftOfCard.R, Is.GreaterThan(150), "the picture is bright grey");
        Assert.That(insideCard.R, Is.LessThan(90), "and the card panel is dark over it");

        // And the banner, at the bottom.
        var insideBanner = At(rgb, _layout.Banner.X + 5, _layout.Banner.Y + _layout.Banner.Height / 2);
        Assert.That(insideBanner.R, Is.LessThan(90));
    }

    [Test]
    public void ATallPictureGetsBarsRatherThanACrop() {
        using var composer = new FrameComposer(_layout, _fonts);
        using var picture = Picture(1000, 1000, 220);
        var rgb = composer.Compose(picture, null, null);

        var middle = At(rgb, W / 2, H / 2);
        Assert.That(middle.R, Is.GreaterThan(200), "the picture is in the middle");
        var farLeft = At(rgb, 2, H / 2);
        Assert.That(farLeft.R, Is.LessThan(30), "and a square frame leaves bars at the sides");
    }

    [Test]
    public void NoPictureYetIsStillAFrame() {
        // Between subs, before the first one, or while the camera is
        // reconfiguring. The broadcast must not go black or stop.
        using var composer = new FrameComposer(_layout, _fonts);
        var rgb = composer.Compose(null, Card(), "Waiting for the first frame");
        Assert.That(rgb.Length, Is.EqualTo(W * H * 3));
        var insideCard = At(rgb, _layout.Card.X + _layout.Card.Width / 2, _layout.Card.Y + _layout.CardInset / 2);
        Assert.That(insideCard.R, Is.LessThan(90), "the card is still there");
    }

    [Test]
    public void ALongDescriptionCannotPushTheCardOffTheFrame() {
        using var composer = new FrameComposer(_layout, _fonts);
        var wall = string.Join(" ", Enumerable.Repeat("supernova", 400));
        Assert.DoesNotThrow(() => composer.Compose(null, Card(description: wall), null));

        // The bottom of the frame below the banner must still be background,
        // which it would not be if the panel had grown without limit.
        var rgb = composer.Compose(null, Card(description: wall), null);
        var belowBanner = At(rgb, _layout.Card.X + 5, H - 2);
        Assert.That(belowBanner.R, Is.LessThan(30));
    }

    [Test]
    public void TheHeaderIsDrawnAndTheCardMovesUnderIt() {
        var layout = BroadcastLayout.For(W, H, hasHeader: true);
        using var composer = new FrameComposer(layout, _fonts);
        using var picture = Picture(1600, 900, 200);
        var rgb = composer.Compose(picture, Card(), "M42 | 14 frames",
            "Polaris Live Stream", "550 mm f/5.5 · ASI2600MC Pro · AM5");

        var i = ((layout.Header.Y + layout.Header.Height / 2) * W + layout.Header.X + 5) * 3;
        Assert.That(rgb[i], Is.LessThan(90), "the header panel is dark over the picture");

        // Above the header there is still picture, so the strip is inset
        // rather than glued to the edge where a player would overscan it.
        var above = ((layout.Header.Y / 2) * W + W / 2) * 3;
        Assert.That(rgb[above], Is.GreaterThan(150));
    }

    [Test]
    public void AHeaderWithNothingToSayIsNotDrawn() {
        var layout = BroadcastLayout.For(W, H, hasHeader: true);
        using var composer = new FrameComposer(layout, _fonts);
        using var picture = Picture(1600, 900, 200);
        var rgb = composer.Compose(picture, null, null, null, null);
        var i = ((layout.Header.Y + layout.Header.Height / 2) * W + layout.Header.X + 5) * 3;
        Assert.That(rgb[i], Is.GreaterThan(150), "no panel over the picture");
    }

    [Test]
    public void BeforeTheFirstImageTheFrameSaysSo() {
        // A broadcast starts when the button is pressed, which is usually
        // before the first sub has landed. A black rectangle with a card
        // floating on it reads as a fault.
        using var composer = new FrameComposer(_layout, _fonts);
        // Copied: Compose hands back the same buffer every time, so holding
        // the first result and then composing again would compare a frame
        // with itself.
        var bare = (byte[])composer.Compose(null, null, null).Clone();
        var withMessage = composer.Compose(null, null, null, null, null, "Waiting for the first image");

        // Something was drawn across the middle of the frame, which is where
        // the line goes.
        var row = H / 2;
        var litBare = 0;
        var litMessage = 0;
        for (var x = 0; x < W; x++) {
            if (At(bare, x, row).R > 40) litBare++;
            if (At(withMessage, x, row).R > 40) litMessage++;
        }
        Assert.That(litBare, Is.Zero);
        Assert.That(litMessage, Is.GreaterThan(0));
    }

    [Test]
    public void TheSecondPictureIsDrawnInTheBottomLeft() {
        using var composer = new FrameComposer(_layout, _fonts);
        using var picture = Picture(1600, 900, 200);
        using var allSky = Picture(640, 480, 120);

        var without = (byte[])composer.Compose(picture, null, null).Clone();
        var with = composer.Compose(picture, null, null, null, null, null, allSky, "Live");

        var p = _layout.Pip;
        var probe = (p.X + p.Width / 2, p.Y + p.Height / 2);
        Assert.That(At(without, probe.Item1, probe.Item2).R, Is.GreaterThan(150),
            "picture only, before the corner is drawn");
        // The corner holds the mid grey of the second picture, not the bright
        // grey of the main one.
        var inside = At(with, probe.Item1, probe.Item2);
        Assert.That(inside.R, Is.InRange(90, 150));
    }

    [Test]
    public void NoSecondPictureDrawsNoEmptyBox() {
        // A broadcast with no all sky camera must not carry a black rectangle
        // in the corner all night.
        using var composer = new FrameComposer(_layout, _fonts);
        using var picture = Picture(1600, 900, 200);
        var rgb = composer.Compose(picture, null, null, null, null, null, null, null);
        var p = _layout.Pip;
        Assert.That(At(rgb, p.X + p.Width / 2, p.Y + p.Height / 2).R, Is.GreaterThan(150));
    }

    [Test]
    public void AnAllSkyCircleIsFittedRatherThanCropped() {
        // A square fisheye in a 4:3 slot: cropping to fill would cut the
        // horizon off the left and right, which is the part people watch.
        using var composer = new FrameComposer(_layout, _fonts);
        using var square = Picture(600, 600, 120);
        var rgb = composer.Compose(null, null, null, null, null, null, square, null);

        var p = _layout.Pip;
        var mid = p.Y + p.Height / 2;
        // Fitted to the height, so the sides of the slot keep the panel
        // backing rather than the picture.
        Assert.That(At(rgb, p.X + p.Width / 2, mid).R, Is.InRange(90, 150));
        Assert.That(At(rgb, p.X + 3, mid).R, Is.LessThan(90), "panel, not picture, at the edge");
    }

    [Test]
    public void TheSourceIsNamedOverTheCorner() {
        using var composer = new FrameComposer(_layout, _fonts);
        using var allSky = Picture(640, 480, 120);
        var plain = (byte[])composer.Compose(null, null, null, null, null, null, allSky, null).Clone();
        var labelled = composer.Compose(null, null, null, null, null, null, allSky, "All sky");

        // The caption sits over the top left of the corner, on its own scrim.
        // Scan the band the caption occupies rather than one row: where the
        // glyphs land inside it depends on the font metrics.
        var p = _layout.Pip;
        var plainLit = 0;
        var labelledLit = 0;
        for (var y = p.Y; y < p.Y + p.Height / 3; y++) {
            for (var x = p.X; x < p.Right; x++) {
                if (At(plain, x, y).R > 200) plainLit++;
                if (At(labelled, x, y).R > 200) labelledLit++;
            }
        }
        Assert.That(labelledLit, Is.GreaterThan(plainLit + 20), "the caption is white text on a scrim");
    }

    [Test]
    public void AMissingCutoutIsNotAMissingCard() {
        using var composer = new FrameComposer(_layout, _fonts);
        Assert.DoesNotThrow(() => composer.Compose(null, Card(thumb: "/no/such/file.jpg"), "x"));
    }

    [Test]
    public void TextTooWideForTheCardIsCutRatherThanRunOff() {
        using var font = new SKFont(_fonts.Regular, 20);
        var lines = FrameComposer.Wrap("one two three four five six seven eight nine ten", font, 60, 2);
        Assert.That(lines.Length, Is.LessThanOrEqualTo(2));
        foreach (var line in lines) Assert.That(font.MeasureText(line), Is.LessThanOrEqualTo(62), line);
        Assert.That(lines[^1], Does.EndWith("..."), "the part that did not fit is shown to be missing");

        // A single unbreakable token wider than the panel, which a long
        // designation or a pasted URL can be.
        var one = FrameComposer.Wrap("NGC7000IC5070NORTHAMERICANEBULA", font, 60, 2);
        Assert.That(font.MeasureText(one[0]), Is.LessThanOrEqualTo(62));
    }

    [Test]
    public void ShortTextIsNotEllipsised() {
        using var font = new SKFont(_fonts.Regular, 20);
        var lines = FrameComposer.Wrap("Orion Nebula", font, 400, 2);
        Assert.That(lines, Is.EqualTo(new[] { "Orion Nebula" }));
    }
}
