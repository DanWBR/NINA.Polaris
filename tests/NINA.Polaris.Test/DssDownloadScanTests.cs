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
using System.IO;
using NUnit.Framework;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Test;

/// <summary>
/// What the offline DSS card reports as installed. Issue #61: a Pi with
/// 4,012 of the 12,288 order-5 tiles showed order 5 as "full", because one
/// tile in Norder5 was enough to count the order.
/// </summary>
[TestFixture]
public class DssDownloadScanTests {
    private string _root = null!;
    private string _dl = null!;
    private string _bundle = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), "dss-scan-" + Guid.NewGuid().ToString("N"));
        _dl = Path.Combine(_root, "dl");
        _bundle = Path.Combine(_root, "bundle");
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void Tiles(string root, int order, long from, long to) {
        for (long npix = from; npix < to; npix++) {
            long dir = npix / 10000 * 10000;
            var path = Path.Combine(root, $"Norder{order}", $"Dir{dir}", $"Npix{npix}.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1 });
        }
    }

    private static long At(int order) => 12L << (2 * order);

    [Test]
    public void APartlyDownloadedOrderIsNotInstalled() {
        for (int o = 0; o <= 3; o++) Tiles(_bundle, o, 0, At(o));
        Tiles(_dl, 4, 0, At(4));
        Tiles(_dl, 5, 0, 4012);

        var scan = DssDownloadService.ScanOrders(_dl, _bundle);

        Assert.That(scan.InstalledOrder, Is.EqualTo(4));
        Assert.That(scan.PartialOrder, Is.EqualTo(5));
        Assert.That(scan.PartialTiles, Is.EqualTo(4012));
    }

    [Test]
    public void TilesSplitBetweenBundleAndDownloadsCountOnce() {
        // Order 3 half in the bundle, the rest (and an overlap) downloaded.
        for (int o = 0; o <= 2; o++) Tiles(_bundle, o, 0, At(o));
        Tiles(_bundle, 3, 0, 500);
        Tiles(_dl, 3, 400, At(3));

        var scan = DssDownloadService.ScanOrders(_dl, _bundle);

        Assert.That(scan.InstalledOrder, Is.EqualTo(3));
        Assert.That(scan.PartialOrder, Is.EqualTo(-1));
    }

    [Test]
    public void EmptyAndPartFilesDoNotCount() {
        for (int o = 0; o <= 3; o++) Tiles(_bundle, o, 0, At(o));
        Tiles(_dl, 4, 0, At(4) - 1);
        var last = Path.Combine(_dl, "Norder4", "Dir0", $"Npix{At(4) - 1}.jpg");
        File.WriteAllBytes(last, Array.Empty<byte>());
        File.WriteAllBytes(last + ".part", new byte[] { 1 });

        var scan = DssDownloadService.ScanOrders(_dl, _bundle);

        Assert.That(scan.InstalledOrder, Is.EqualTo(3));
        Assert.That(scan.PartialOrder, Is.EqualTo(4));
        Assert.That(scan.PartialTiles, Is.EqualTo(At(4) - 1));
    }
}
