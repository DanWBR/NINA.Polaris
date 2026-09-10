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
using System.Text.Json;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// wwwroot/data/dslr-cameras.json, checked against its own stated rules.
///
/// The catalogue is not decoration: indi_gphoto publishes CCD_INFO as zeros and
/// refuses to expose a frame until something fills it in, so these rows are what
/// lets a DSLR take its first photo at all. A row with a zero or a mistyped
/// resolution is therefore a camera that cannot shoot, and it would reach a user
/// as "nothing happens" with no error anywhere.
///
/// Rows are added by hand, by PR, so the checks here are the ones a human
/// reviewer cannot do by eye across a hundred lines: that every row is complete,
/// that pixelSizeUm really is sensorWidthMm / maxX, and that the numbers stay
/// inside the bounds POST /ccd-info/apply will accept.
/// </summary>
[TestFixture]
public class DslrCatalogueTests {

    private record Cam(string Brand, string Model, double PixelSizeUm,
                       int MaxX, int MaxY, double SensorWidthMm, double SensorHeightMm);

    private static string RepoFile([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..",
                     "src", "NINA.Polaris", "wwwroot", "data", "dslr-cameras.json");

    private static List<Cam> Load() {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile()));
        var list = new List<Cam>();
        foreach (var e in doc.RootElement.GetProperty("cameras").EnumerateArray()) {
            double D(string n) => e.TryGetProperty(n, out var v) ? v.GetDouble() : 0;
            int I(string n) => e.TryGetProperty(n, out var v) ? v.GetInt32() : 0;
            list.Add(new Cam(e.GetProperty("brand").GetString()!,
                             e.GetProperty("model").GetString()!,
                             D("pixelSizeUm"), I("maxX"), I("maxY"),
                             D("sensorWidthMm"), D("sensorHeightMm")));
        }
        return list;
    }

    [Test]
    public void TheCatalogueIsNotEmpty() {
        Assert.That(Load(), Has.Count.GreaterThan(50));
    }

    /// <summary>Every row carries the full set. A missing maxX or maxY is the one
    /// that matters: it is what the driver needs, and the client can only fall
    /// back to a derived approximation.</summary>
    [Test]
    public void EveryRowIsComplete() {
        foreach (var c in Load()) {
            var who = c.Brand + " " + c.Model;
            Assert.Multiple(() => {
                Assert.That(c.Brand, Is.Not.Empty, who);
                Assert.That(c.Model, Is.Not.Empty, who);
                Assert.That(c.PixelSizeUm, Is.GreaterThan(0), who + ": pixelSizeUm");
                Assert.That(c.MaxX, Is.GreaterThan(0), who + ": maxX");
                Assert.That(c.MaxY, Is.GreaterThan(0), who + ": maxY");
                Assert.That(c.SensorWidthMm, Is.GreaterThan(0), who + ": sensorWidthMm");
                Assert.That(c.SensorHeightMm, Is.GreaterThan(0), who + ": sensorHeightMm");
            });
        }
    }

    /// <summary>The file's own rule: pixelSizeUm = sensorWidthMm / maxX, to
    /// 0.01 um. Half a rounding step is the tightest tolerance that can hold,
    /// and it catches the realistic mistake, which is a resolution copied from
    /// the wrong row of a spec sheet.</summary>
    [Test]
    public void PixelSizeAgreesWithTheSensorWidthAndResolution() {
        foreach (var c in Load()) {
            var derived = c.SensorWidthMm / c.MaxX * 1000.0;
            Assert.That(c.PixelSizeUm, Is.EqualTo(derived).Within(0.005),
                $"{c.Brand} {c.Model}: {c.SensorWidthMm} mm / {c.MaxX} px = " +
                $"{derived:F3} um, but the row says {c.PixelSizeUm:F2}");
        }
    }

    /// <summary>Square pixels are assumed throughout, so the aspect ratio the
    /// resolution implies has to match the one the sensor dimensions imply.
    /// 1.5 percent covers the nominal rounding in the published mm figures.</summary>
    [Test]
    public void TheAspectRatioIsConsistent() {
        foreach (var c in Load()) {
            var byPixels = (double)c.MaxX / c.MaxY;
            var byMm = c.SensorWidthMm / c.SensorHeightMm;
            Assert.That(byPixels, Is.EqualTo(byMm).Within(0.015 * byMm),
                $"{c.Brand} {c.Model}: {c.MaxX}x{c.MaxY} is {byPixels:F3}:1 but " +
                $"{c.SensorWidthMm}x{c.SensorHeightMm} mm is {byMm:F3}:1");
        }
    }

    /// <summary>Nothing in the catalogue may fall outside what the apply route
    /// accepts, or picking that body would silently do nothing.</summary>
    [Test]
    public void EveryRowSurvivesTheApplyRouteBounds() {
        foreach (var c in Load()) {
            var body = JsonDocument.Parse(
                $"{{\"maxX\":{c.MaxX},\"maxY\":{c.MaxY},\"pixelSizeUm\":" +
                c.PixelSizeUm.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "}").RootElement;
            var (x, y, px, _) = NINA.Polaris.Endpoints.CameraEndpoints.ReadCcdInfoRequest(body);
            Assert.Multiple(() => {
                Assert.That(x, Is.EqualTo(c.MaxX), c.Brand + " " + c.Model);
                Assert.That(y, Is.EqualTo(c.MaxY), c.Brand + " " + c.Model);
                Assert.That(px, Is.EqualTo(c.PixelSizeUm).Within(1e-9),
                    c.Brand + " " + c.Model);
            });
        }
    }

    /// <summary>A duplicate brand+model would give the picker two rows with the
    /// same label, and whichever one Array.find reached first would win.</summary>
    [Test]
    public void NoDuplicateBrandAndModel() {
        var dupes = Load().GroupBy(c => c.Brand + " / " + c.Model)
                          .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.That(dupes, Is.Empty);
    }

    /// <summary>The three brands the gphoto path is actually used with should all
    /// be represented, and with enough depth to be worth calling a catalogue.
    /// This is a floor, not a target.</summary>
    [TestCase("Canon", 20)]
    [TestCase("Nikon", 15)]
    [TestCase("Sony", 15)]
    public void TheMajorBrandsAreCovered(string brand, int atLeast) {
        Assert.That(Load().Count(c => c.Brand == brand), Is.GreaterThanOrEqualTo(atLeast));
    }
}
