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

using System.Text.Json;
using NINA.Polaris.Endpoints;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The body of POST /api/camera/ccd-info/apply.
///
/// Why it takes a body at all: the RIGS camera picker saves the rig on a 600 ms
/// debounce, so a push issued straight after a pick reads the PREVIOUS camera's
/// geometry out of the stored profile. Carrying the numbers in the request
/// removes that ordering dependency, and zeros mean "use the rig", which is what
/// the manual button sends.
///
/// Everything that reaches here lands in an INDI CCD_INFO number vector, which
/// the driver validates element by element: one out-of-range member gets the
/// whole vector refused, so a half-usable body must be rejected as a body rather
/// than applied in part.
/// </summary>
[TestFixture]
public class CcdInfoRequestTests {

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private const string Full =
        """{"maxX":6000,"maxY":4000,"pixelSizeUm":5.98,"bitDepth":14}""";

    [Test]
    public void AFullBodyComesThroughIntact() {
        var (x, y, px, bits) = CameraEndpoints.ReadCcdInfoRequest(Json(Full));
        Assert.Multiple(() => {
            Assert.That(x, Is.EqualTo(6000));
            Assert.That(y, Is.EqualTo(4000));
            Assert.That(px, Is.EqualTo(5.98).Within(1e-9));
            Assert.That(bits, Is.EqualTo(14));
        });
    }

    /// <summary>The manual button posts <c>{}</c>, which must read as "no body"
    /// so the route falls back to the stored rig.</summary>
    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("7")]
    public void AnEmptyOrNonObjectBodyIsNoBody(string body) {
        Assert.That(CameraEndpoints.ReadCcdInfoRequest(Json(body)),
            Is.EqualTo((0, 0, 0d, 0)));
    }

    /// <summary>A body missing any one of the three required numbers is rejected
    /// whole. Applying the half that is present is the failure mode worth
    /// pinning: writing a pixel size while Max X/Y stay zero is exactly what was
    /// leaving indi_gphoto unable to capture.</summary>
    [TestCase("""{"maxY":4000,"pixelSizeUm":5.98}""", TestName = "no maxX")]
    [TestCase("""{"maxX":6000,"pixelSizeUm":5.98}""", TestName = "no maxY")]
    [TestCase("""{"maxX":6000,"maxY":4000}""", TestName = "no pixel size")]
    public void APartialBodyIsRejectedWhole(string body) {
        Assert.That(CameraEndpoints.ReadCcdInfoRequest(Json(body)),
            Is.EqualTo((0, 0, 0d, 0)));
    }

    /// <summary>Strings are not numbers, even when they spell one: JSON from a
    /// form can arrive quoted, and <c>"6000"</c> must not pass as geometry.</summary>
    [Test]
    public void QuotedNumbersDoNotCount() {
        Assert.That(CameraEndpoints.ReadCcdInfoRequest(
            Json("""{"maxX":"6000","maxY":"4000","pixelSizeUm":"5.98"}""")),
            Is.EqualTo((0, 0, 0d, 0)));
    }

    [TestCase(0, 4000, 5.98, TestName = "zero width")]
    [TestCase(-6000, 4000, 5.98, TestName = "negative width")]
    [TestCase(6000, 0, 5.98, TestName = "zero height")]
    [TestCase(40000, 4000, 5.98, TestName = "width past the bound")]
    [TestCase(6000, 40000, 5.98, TestName = "height past the bound")]
    [TestCase(6000, 4000, 0.0, TestName = "zero pixel size")]
    [TestCase(6000, 4000, -5.98, TestName = "negative pixel size")]
    [TestCase(6000, 4000, 500.0, TestName = "pixel size past the bound")]
    public void OutOfRangeGeometryIsRejectedWhole(int x, int y, double px) {
        var body = $"{{\"maxX\":{x},\"maxY\":{y},\"pixelSizeUm\":{px.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        Assert.That(CameraEndpoints.ReadCcdInfoRequest(Json(body)),
            Is.EqualTo((0, 0, 0d, 0)));
    }

    /// <summary>The largest sensor in the catalogue (9504 px wide) and the
    /// smallest pixel pitch (3.2 um) are inside the bounds, which is the point of
    /// choosing them where they are.</summary>
    [Test]
    public void TheCatalogueExtremesFitInsideTheBounds() {
        var (x, y, px, _) = CameraEndpoints.ReadCcdInfoRequest(
            Json("""{"maxX":9504,"maxY":6336,"pixelSizeUm":3.2}"""));
        Assert.Multiple(() => {
            Assert.That(x, Is.EqualTo(9504));
            Assert.That(y, Is.EqualTo(6336));
            Assert.That(px, Is.EqualTo(3.2).Within(1e-9));
        });
    }

    /// <summary>Bit depth is the one optional member: an absent or silly value
    /// becomes 0, which TrySetCcdInfoAsync reads as "leave CCD_BITSPERPIXEL
    /// alone". The geometry still applies, because a wrong bit depth is a
    /// cosmetic problem and a zero Max X/Y is not.</summary>
    [TestCase("""{"maxX":6000,"maxY":4000,"pixelSizeUm":5.98}""", 0)]
    [TestCase("""{"maxX":6000,"maxY":4000,"pixelSizeUm":5.98,"bitDepth":0}""", 0)]
    [TestCase("""{"maxX":6000,"maxY":4000,"pixelSizeUm":5.98,"bitDepth":4}""", 0)]
    [TestCase("""{"maxX":6000,"maxY":4000,"pixelSizeUm":5.98,"bitDepth":64}""", 0)]
    [TestCase("""{"maxX":6000,"maxY":4000,"pixelSizeUm":5.98,"bitDepth":8}""", 8)]
    [TestCase("""{"maxX":6000,"maxY":4000,"pixelSizeUm":5.98,"bitDepth":16}""", 16)]
    public void BitDepthIsOptionalAndNeverBlocksTheGeometry(string body, int expected) {
        var (x, y, px, bits) = CameraEndpoints.ReadCcdInfoRequest(Json(body));
        Assert.Multiple(() => {
            Assert.That(bits, Is.EqualTo(expected));
            Assert.That(x, Is.EqualTo(6000), "the geometry still applies");
            Assert.That(y, Is.EqualTo(4000));
            Assert.That(px, Is.EqualTo(5.98).Within(1e-9));
        });
    }

    /// <summary>Fractional pixel counts round rather than truncate; a client that
    /// computed Max X from a rounded pixel pitch can hand over 6003.4.</summary>
    [Test]
    public void FractionalPixelCountsRound() {
        var (x, y, _, _) = CameraEndpoints.ReadCcdInfoRequest(
            Json("""{"maxX":6003.4,"maxY":4002.6,"pixelSizeUm":5.98}"""));
        Assert.Multiple(() => {
            Assert.That(x, Is.EqualTo(6003));
            Assert.That(y, Is.EqualTo(4003));
        });
    }
}
