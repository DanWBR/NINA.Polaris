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

using NINA.Image.Editor;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// SASPRO-D. Masks decide WHERE an edit lands. The failure that matters is a
/// mask that is subtly wrong rather than absent: a pixel the operator meant to
/// protect coming back changed, or a soft edge showing a seam.
/// </summary>
[TestFixture]
public class EditMaskTests {

    /// <summary>Mono ramp, one pixel per luminance step.</summary>
    private static byte[] Ramp(int n) {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 255 / Math.Max(1, n - 1));
        return b;
    }

    // ── no mask ─────────────────────────────────────────────────────────

    [Test]
    public void NoMaskCoversEverything() {
        var m = EditMask.Build(Ramp(16), 16, 1, 1, new MaskParams());

        Assert.That(m, Is.All.EqualTo((byte)255));
    }

    [Test]
    public void APaintedMaskWithNoBitmapIsANoOp() {
        var p = new MaskParams(MaskKind.Painted, Painted: null);

        Assert.That(p.IsNoOp, Is.True,
            "an empty painted mask must not black out the whole edit");
    }

    // ── luminance ramp ──────────────────────────────────────────────────

    [Test]
    public void TheLuminanceRampCoversTheBrightEndAndProtectsTheDark() {
        var buf = new byte[] { 0, 128, 255 };
        var m = EditMask.Build(buf, 3, 1, 1,
            new MaskParams(MaskKind.Luminance, Low: 0.25, High: 0.75));

        Assert.That(m[0], Is.EqualTo(0), "below the ramp: untouched");
        Assert.That(m[2], Is.EqualTo(255), "above the ramp: fully edited");
        Assert.That(m[1], Is.InRange(100, 155), "mid-ramp sits near half");
    }

    [Test]
    public void InvertSwapsCoveredAndProtected() {
        var buf = new byte[] { 0, 255 };
        var plain = EditMask.Build(buf, 2, 1, 1,
            new MaskParams(MaskKind.Luminance, Low: 0.25, High: 0.75));
        var inv = EditMask.Build(buf, 2, 1, 1,
            new MaskParams(MaskKind.Luminance, Low: 0.25, High: 0.75, Invert: true));

        Assert.That(inv[0], Is.EqualTo(255 - plain[0]));
        Assert.That(inv[1], Is.EqualTo(255 - plain[1]));
    }

    /// <summary>A ramp with no width is a hard threshold, not an inverted or
    /// all-zero mask: dragging the two handles together is something an
    /// operator does by accident.</summary>
    [Test]
    public void ACollapsedRampBecomesAHardThreshold() {
        var buf = new byte[] { 100, 200 };
        var m = EditMask.Build(buf, 2, 1, 1,
            new MaskParams(MaskKind.Luminance, Low: 0.6, High: 0.6));

        Assert.That(m[0], Is.EqualTo(0));
        Assert.That(m[1], Is.EqualTo(255));
    }

    /// <summary>Handles dragged past each other must degrade to the same hard
    /// edge, not turn the mask inside out.</summary>
    [Test]
    public void HandlesInTheWrongOrderAreOrdered() {
        var buf = new byte[] { 0, 255 };
        var forward = EditMask.Build(buf, 2, 1, 1,
            new MaskParams(MaskKind.Luminance, Low: 0.3, High: 0.7));
        var swapped = EditMask.Build(buf, 2, 1, 1,
            new MaskParams(MaskKind.Luminance, Low: 0.7, High: 0.3));

        Assert.That(swapped, Is.EqualTo(forward));
    }

    // ── range band ──────────────────────────────────────────────────────

    [Test]
    public void TheRangeBandCoversTheMiddleAndFallsOffBothWays() {
        // Luminance 0, .251, .502, .749, 1. The band is .4 to .6, so 64 and
        // 191 sit .149 outside it: the feather has to be wider than that for
        // them to be in it at all, which is the whole point of the case.
        var buf = new byte[] { 0, 64, 128, 191, 255 };
        var m = EditMask.Build(buf, 5, 1, 1,
            new MaskParams(MaskKind.Range, Low: 0.4, High: 0.6, Feather: 0.25));

        Assert.That(m[2], Is.EqualTo(255), "inside the band");
        Assert.That(m[0], Is.EqualTo(0), "far below");
        Assert.That(m[4], Is.EqualTo(0), "far above");
        Assert.That(m[1], Is.GreaterThan(0).And.LessThan(255), "in the lower feather");
        Assert.That(m[3], Is.GreaterThan(0).And.LessThan(255), "in the upper feather");
    }

    [Test]
    public void ZeroFeatherGivesAHardBand() {
        var buf = new byte[] { 64, 128, 191 };
        var m = EditMask.Build(buf, 3, 1, 1,
            new MaskParams(MaskKind.Range, Low: 0.45, High: 0.55, Feather: 0));

        Assert.That(m, Is.EqualTo(new byte[] { 0, 255, 0 }));
    }

    // ── opacity ─────────────────────────────────────────────────────────

    [Test]
    public void OpacityScalesTheWholeMask() {
        var buf = new byte[] { 255, 255 };
        var m = EditMask.Build(buf, 2, 1, 1,
            new MaskParams(MaskKind.Luminance, Low: 0, High: 0.1, Opacity: 0.5));

        Assert.That(m[0], Is.InRange(126, 129));
    }

    // ── blending ────────────────────────────────────────────────────────

    [Test]
    public void FullCoverageKeepsTheEditAndZeroRestoresTheOriginal() {
        var original = new byte[] { 10, 10, 10, 10 };
        var edited = new byte[] { 200, 200, 200, 200 };
        var mask = new byte[] { 255, 0, 255, 0 };

        EditMask.Blend(edited, original, mask, channels: 1);

        Assert.That(edited, Is.EqualTo(new byte[] { 200, 10, 200, 10 }));
    }

    [Test]
    public void PartialCoverageLandsBetweenTheTwo() {
        var original = new byte[] { 0 };
        var edited = new byte[] { 200 };

        EditMask.Blend(edited, original, new byte[] { 128 }, channels: 1);

        Assert.That(edited[0], Is.InRange(99, 101));
    }

    /// <summary>Truncating instead of rounding darkens every partially covered
    /// pixel by up to one level, which reads as a band along a soft edge.
    /// Blending an unchanged image must return it byte for byte at ANY
    /// coverage.</summary>
    [TestCase((byte)1)]
    [TestCase((byte)64)]
    [TestCase((byte)128)]
    [TestCase((byte)200)]
    [TestCase((byte)254)]
    public void BlendingAnUnchangedImageIsLossless(byte coverage) {
        var values = new byte[] { 0, 1, 17, 63, 128, 200, 254, 255 };
        var edited = (byte[])values.Clone();
        var mask = new byte[values.Length];
        Array.Fill(mask, coverage);

        EditMask.Blend(edited, values, mask, channels: 1);

        Assert.That(edited, Is.EqualTo(values),
            "blending a pixel with itself must be exact, or a soft mask edge "
            + "shows a step where none exists in either input");
    }

    [Test]
    public void BlendHandlesThreeChannels() {
        var original = new byte[] { 0, 0, 0, 10, 20, 30 };
        var edited = new byte[] { 255, 255, 255, 200, 200, 200 };

        EditMask.Blend(edited, original, new byte[] { 255, 0 }, channels: 3);

        Assert.That(edited, Is.EqualTo(new byte[] { 255, 255, 255, 10, 20, 30 }));
    }

    // ── painted masks ───────────────────────────────────────────────────

    [Test]
    public void RleSurvivesARoundTrip() {
        var data = new byte[300];
        for (int i = 0; i < 100; i++) data[i] = 255;
        for (int i = 200; i < 300; i++) data[i] = 64;

        var back = EditMask.DecodeRle(EditMask.EncodeRle(data), data.Length);

        Assert.That(back, Is.EqualTo(data));
    }

    /// <summary>Runs longer than a 16-bit count have to split across pairs.</summary>
    [Test]
    public void RleSplitsRunsLongerThanTheCountField() {
        var data = new byte[200000];
        Array.Fill(data, (byte)7);

        var back = EditMask.DecodeRle(EditMask.EncodeRle(data), data.Length);

        Assert.That(back, Is.EqualTo(data));
    }

    [Test]
    public void RleIsMuchSmallerThanTheBitmapForBrushStrokes() {
        // A blob in the middle: what a painted mask actually looks like.
        var data = new byte[512 * 512];
        for (int y = 180; y < 330; y++)
            for (int x = 180; x < 330; x++) data[y * 512 + x] = 255;

        var encoded = EditMask.EncodeRle(data);

        Assert.That(encoded.Length, Is.LessThan(data.Length / 4),
            "the point of encoding it at all is that it can ride in the sidecar");
    }

    [Test]
    public void ACorruptPaintedMaskDecodesToNothingRatherThanThrowing() {
        Assert.That(EditMask.DecodeRle("not base64 at all!!", 10),
            Is.EqualTo(new byte[10]));
    }

    [Test]
    public void AShortStreamLeavesTheRestUncovered() {
        var partial = EditMask.EncodeRle(new byte[] { 255, 255 });

        var back = EditMask.DecodeRle(partial, 6);

        Assert.That(back, Is.EqualTo(new byte[] { 255, 255, 0, 0, 0, 0 }));
    }

    /// <summary>The painted bitmap is stored small and scaled up on every
    /// apply, so the scale must land on the image rather than drifting.</summary>
    [Test]
    public void APaintedMaskScalesToTheImage() {
        // 2x2: left column covered, right column not.
        var small = new byte[] { 255, 0, 255, 0 };
        var p = new MaskParams(MaskKind.Painted,
            Painted: EditMask.EncodeRle(small), PaintedWidth: 2, PaintedHeight: 2);

        var m = EditMask.Build(new byte[64], 8, 8, 1, p);

        Assert.That(m[0], Is.GreaterThan(200), "top-left stays covered");
        Assert.That(m[7], Is.LessThan(55), "top-right stays clear");
        Assert.That(m[8 * 7], Is.GreaterThan(200), "bottom-left too");
    }

    [Test]
    public void APaintedMaskCanBeInverted() {
        var small = new byte[] { 255, 255, 255, 255 };
        var p = new MaskParams(MaskKind.Painted,
            Painted: EditMask.EncodeRle(small), PaintedWidth: 2, PaintedHeight: 2,
            Invert: true);

        Assert.That(EditMask.Build(new byte[16], 4, 4, 1, p), Is.All.EqualTo((byte)0));
    }

    // ── through the pipeline ────────────────────────────────────────────

    /// <summary>The whole point, end to end: an edit that would change every
    /// pixel must leave the protected ones exactly as they were.</summary>
    [Test]
    public void AMaskedEditLeavesTheProtectedPixelsByteIdentical() {
        // Two mono pixels: one black, one white.
        var buf = new byte[] { 0, 255 };
        var before = (byte[])buf.Clone();

        // Exposure up, masked to the bright end only.
        var p = new EditParams(
            Light: new LightParams(Exposure: 0.6),
            Mask: new MaskParams(MaskKind.Luminance, Low: 0.5, High: 0.6));

        var outBuf = EditPipeline.Apply(buf, 2, 1, 1, p);

        Assert.That(outBuf[0], Is.EqualTo(before[0]),
            "the dark pixel is outside the mask and must be untouched");
    }

    [Test]
    public void WithoutAMaskTheEditReachesEveryPixel() {
        var masked = EditPipeline.Apply(new byte[] { 60, 60 }, 2, 1, 1,
            new EditParams(Light: new LightParams(Exposure: 0.6),
                           Mask: new MaskParams(MaskKind.Luminance, Low: 0.9, High: 1.0)));
        var plain = EditPipeline.Apply(new byte[] { 60, 60 }, 2, 1, 1,
            new EditParams(Light: new LightParams(Exposure: 0.6)));

        Assert.That(plain[0], Is.GreaterThan(60), "precondition: the edit does something");
        Assert.That(masked[0], Is.EqualTo(60),
            "and the mask is what stops it reaching this pixel");
    }
}
