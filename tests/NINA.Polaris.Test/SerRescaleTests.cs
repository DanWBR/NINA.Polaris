// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.

using System;
using System.IO;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class SerRescaleTests {
    private string _dir = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-ser-rescale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Write a 16-bit SER whose samples are RIGHT-aligned in
    /// <paramref name="significantBits"/> bits (the pre-fix bug), returning the
    /// per-frame source pixels for later comparison.</summary>
    private string WriteRightAlignedSer(int w, int h, int frames, int significantBits,
                                        out ushort[][] framesOut, SerColorMode color = SerColorMode.BayerRGGB) {
        var path = Path.Combine(_dir, "src.ser");
        framesOut = new ushort[frames][];
        int max = (1 << significantBits) - 1;
        using var writer = new SerFileWriter(path, w, h, 16, color, "Polaris", "TestCam", "TestScope");
        var rnd = new Random(1234);
        for (int f = 0; f < frames; f++) {
            var px = new ushort[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = (ushort)rnd.Next(0, max + 1);
            // Guarantee the brightest sample reaches the ADC ceiling so
            // auto-detect can recognise the depth.
            px[0] = (ushort)max;
            framesOut[f] = px;
            writer.WriteFrame(px, DateTime.UtcNow.AddSeconds(f));
        }
        return path;
    }

    [Test]
    public void Rescale_Explicit12Bit_LeftAlignsBy4_AndPreservesGeometry() {
        var src = WriteRightAlignedSer(8, 6, 4, significantBits: 12, out var original);

        var res = SerRescale.Rescale(src, bitsOverride: 12, outPath: null);

        Assert.That(res.Done, Is.True, res.Message);
        Assert.That(res.Shift, Is.EqualTo(4));
        Assert.That(res.OutputPath, Is.Not.Null);
        Assert.That(File.Exists(res.OutputPath!), Is.True);

        using var reader = new SerFileReader(res.OutputPath!);
        Assert.That(reader.Width, Is.EqualTo(8));
        Assert.That(reader.Height, Is.EqualTo(6));
        Assert.That(reader.FrameCount, Is.EqualTo(4));
        Assert.That(reader.BitDepth, Is.EqualTo(16));
        Assert.That(reader.ColorMode, Is.EqualTo(SerColorMode.BayerRGGB));

        for (int f = 0; f < 4; f++) {
            var got = reader.ReadFrameAsUshort(f);
            for (int i = 0; i < got.Length; i++) {
                ushort expected = (ushort)(original[f][i] << 4);
                Assert.That(got[i], Is.EqualTo(expected),
                    $"frame {f} pixel {i}: {original[f][i]} << 4 should be {expected}");
            }
        }
    }

    [Test]
    public void Rescale_AutoDetect_FindsTwelveBitDepth() {
        var src = WriteRightAlignedSer(8, 8, 5, significantBits: 12, out _);

        var res = SerRescale.Rescale(src, bitsOverride: null, outPath: null);

        Assert.That(res.Done, Is.True, res.Message);
        Assert.That(res.SignificantBits, Is.EqualTo(12));
        Assert.That(res.Shift, Is.EqualTo(4));
    }

    [Test]
    public void Rescale_AlreadyFullRange_IsNoOp() {
        // Samples fill the 16-bit range → nothing to do, no file written.
        var src = WriteRightAlignedSer(8, 8, 3, significantBits: 16, out _);

        var res = SerRescale.Rescale(src, bitsOverride: null, outPath: null);

        Assert.That(res.Done, Is.False);
        Assert.That(res.Shift, Is.EqualTo(0));
        Assert.That(res.OutputPath, Is.Null);
        Assert.That(File.Exists(Path.Combine(_dir, "src-fixed16.ser")), Is.False);
    }

    [Test]
    public void Rescale_RefusesToOverwriteSource() {
        var src = WriteRightAlignedSer(4, 4, 2, significantBits: 12, out _);
        Assert.Throws<ArgumentException>(() => SerRescale.Rescale(src, 12, src));
    }
}
