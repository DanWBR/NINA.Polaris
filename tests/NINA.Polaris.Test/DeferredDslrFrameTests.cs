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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Devices;

namespace NINA.Polaris.Test;

/// <summary>A DSLR frame off indi_gphoto must reach the sequencer before its
/// RAW is decoded, and the decode must run once, on the first reader, without
/// being pulled forward by the re-tagging IndiCamera does after the BLOB.
/// Measured on a Raspberry Pi 4 with a Sony a7 IV: 7.8 s of the 16.5 s gap
/// between 60 s subs was this decode, run on the INDI reader thread before the
/// exposure counted as complete.</summary>
[TestFixture]
public class DeferredDslrFrameTests {
    private static readonly ImageProperties Props = new() {
        Width = 4, Height = 2, BitDepth = 16, IsBayered = true, BayerPattern = BayerPatternEnum.RGGB
    };

    private static DeferredDslrFrame Frame(Func<ushort[]> decode) =>
        new(Props, new ImageMetaData(), new byte[] { 1, 2, 3 }, ".arw", decode);

    [Test]
    public void TheDecodeWaitsForTheFirstReader() {
        int calls = 0;
        var frame = Frame(() => { calls++; return new ushort[8]; });

        Assert.That(frame.IsDecoded, Is.False);
        Assert.That(calls, Is.Zero, "building the frame must not decode it");
        Assert.That(frame.Properties.Width, Is.EqualTo(4), "the size is known before the decode");
        Assert.That(((IHasRawFile)frame).RawFileBytes, Is.EqualTo(new byte[] { 1, 2, 3 }),
            "the writer saves the RAW without touching the pixels");

        var px = frame.Data;
        Assert.That(frame.IsDecoded, Is.True);
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(px.Length, Is.EqualTo(8));
    }

    [Test]
    public void TheDecodeRunsOnce_ForEveryReader() {
        int calls = 0;
        var frame = Frame(() => { Interlocked.Increment(ref calls); Thread.Sleep(20); return new ushort[8]; });

        var reads = Enumerable.Range(0, 8).Select(_ => Task.Run(() => frame.Data)).ToArray();
        Task.WaitAll(reads);

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(reads.Select(r => r.Result).Distinct().Count(), Is.EqualTo(1), "every reader gets the one buffer");
        Assert.That(frame.Statistics.Width, Is.EqualTo(4));
    }

    [Test]
    public void AFailedDecode_GivesZerosOfTheAnnouncedSize_NotAnException() {
        var frame = Frame(() => throw new InvalidOperationException("libraw refused"));
        Assert.That(frame.Data, Has.Length.EqualTo(8).And.All.EqualTo(0));

        var shortFrame = Frame(() => new ushort[3]);
        Assert.That(shortFrame.Data, Has.Length.EqualTo(8),
            "a buffer that does not match Width x Height must not reach a consumer indexing by the properties");
    }

    [Test]
    public void RetaggingTheFrame_KeepsTheDecodeDeferred_AndTheCameraFile() {
        int calls = 0;
        var frame = Frame(() => { calls++; return new ushort[8]; });

        var rebuilt = IndiCamera.WithProperties(frame, frame.Properties with { SignificantBitDepth = 14 });

        Assert.That(rebuilt, Is.InstanceOf<DeferredDslrFrame>());
        Assert.That(calls, Is.Zero, "re-tagging the bit depth after the BLOB must not decode on the INDI thread");
        Assert.That(rebuilt.Properties.SignificantBitDepth, Is.EqualTo(14));
        Assert.That(((IHasRawFile)rebuilt).RawFileExtension, Is.EqualTo(".arw"));

        _ = rebuilt.Data;
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(frame.IsDecoded, Is.True, "the original and the re-tagged frame share one decode");
    }
}
