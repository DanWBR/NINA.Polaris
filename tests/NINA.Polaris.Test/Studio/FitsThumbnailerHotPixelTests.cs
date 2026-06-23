// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.

using NINA.Polaris.Services.Studio;
using NUnit.Framework;

namespace NINA.Polaris.Test.Studio;

[TestFixture]
public class FitsThumbnailerHotPixelTests {
    private const int W = 32, H = 32;

    private static ushort[] Background(ushort level = 100) {
        var p = new ushort[W * H];
        for (int i = 0; i < p.Length; i++) p[i] = level;
        return p;
    }

    [Test]
    public void SuppressHotPixels_RemovesIsolatedSpeck() {
        var p = Background();
        int hot = 12 * W + 18;
        p[hot] = 60000;   // single bright pixel over a flat field

        var clean = FitsThumbnailer.SuppressHotPixels(p, W, H);

        Assert.That(clean[hot], Is.LessThan(1000), "isolated hot pixel should be knocked down to its neighbours");
        Assert.That(p[hot], Is.EqualTo(60000), "source buffer must not be mutated");
    }

    [Test]
    public void SuppressHotPixels_KeepsRealStar() {
        // A small star: a bright core with bright immediate neighbours. None of
        // its pixels tower over its neighbours, so all must survive.
        var p = Background();
        int cx = 16, cy = 16;
        void Set(int dx, int dy, ushort v) => p[(cy + dy) * W + (cx + dx)] = v;
        Set(0, 0, 50000);
        Set(-1, 0, 30000); Set(1, 0, 30000); Set(0, -1, 30000); Set(0, 1, 30000);
        Set(-1, -1, 18000); Set(1, 1, 18000); Set(-1, 1, 18000); Set(1, -1, 18000);

        var clean = FitsThumbnailer.SuppressHotPixels(p, W, H);

        Assert.That(clean[cy * W + cx], Is.EqualTo(50000), "star core must be preserved");
        Assert.That(clean[cy * W + (cx + 1)], Is.EqualTo(30000), "star wing must be preserved");
    }

    [Test]
    public void SuppressHotPixels_LeavesCleanFieldUnchanged() {
        var p = Background(250);
        var clean = FitsThumbnailer.SuppressHotPixels(p, W, H);
        Assert.That(clean, Is.EqualTo(p));
    }
}
