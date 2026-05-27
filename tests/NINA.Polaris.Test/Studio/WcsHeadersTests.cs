using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// CCALB-0a: pins the WCS read/write round-trip and the pixel↔(RA,
/// Dec) inversion. PCC depends on these being correct to within a
/// fraction of a pixel; a sign flip on any matrix element would
/// mirror catalog stars on the wrong side of the image and silently
/// produce a nonsense color calibration.
/// </summary>
[TestFixture]
public class WcsHeadersTests {

    // ─── round-trip helpers ──────────────────────────────────────────

    [Test]
    public void FromSolveResult_BuildsConsistentCdMatrix() {
        // A typical solve result: M31 at RA=10.68° Dec=41.27°, scale
        // 2.0"/px, rotation 0°. The CD matrix should produce -2"/px
        // on CD11 (RA grows east, X grows west on a typical northern
        // frame), 2"/px on CD22, and zero cross-terms.
        var wcs = WcsHeaders.FromSolveResult(
            raDeg: 10.68, decDeg: 41.27,
            scaleArcsecPerPixel: 2.0, rotationDeg: 0,
            imageWidth: 4000, imageHeight: 3000);

        double scaleDeg = 2.0 / 3600.0;
        Assert.That(wcs.CD11, Is.EqualTo(-scaleDeg).Within(1e-12));
        Assert.That(wcs.CD22, Is.EqualTo(scaleDeg).Within(1e-12));
        Assert.That(wcs.CD12, Is.EqualTo(0).Within(1e-12));
        Assert.That(wcs.CD21, Is.EqualTo(0).Within(1e-12));
        Assert.That(wcs.RefPixelX, Is.EqualTo(2000.5).Within(0.01));
        Assert.That(wcs.RefPixelY, Is.EqualTo(1500.5).Within(0.01));
    }

    // ─── pixel ↔ RA/Dec inverse ──────────────────────────────────────

    [Test]
    public void PixelToRaDec_AtReferencePixel_ReturnsReference() {
        var wcs = WcsHeaders.FromSolveResult(
            raDeg: 200.0, decDeg: 30.0,
            scaleArcsecPerPixel: 1.5, rotationDeg: 0,
            imageWidth: 1000, imageHeight: 1000);

        var (ra, dec) = wcs.PixelToRaDec(wcs.RefPixelX, wcs.RefPixelY);
        Assert.That(ra, Is.EqualTo(200.0).Within(1e-9));
        Assert.That(dec, Is.EqualTo(30.0).Within(1e-9));
    }

    [Test]
    public void RaDecToPixel_AtReferenceRaDec_ReturnsRefPixel() {
        var wcs = WcsHeaders.FromSolveResult(
            raDeg: 200.0, decDeg: 30.0,
            scaleArcsecPerPixel: 1.5, rotationDeg: 15,
            imageWidth: 1000, imageHeight: 1000);

        var (x, y) = wcs.RaDecToPixel(200.0, 30.0);
        Assert.That(x, Is.EqualTo(wcs.RefPixelX).Within(0.01));
        Assert.That(y, Is.EqualTo(wcs.RefPixelY).Within(0.01));
    }

    [Test]
    public void PixelToRaDec_Then_RaDecToPixel_RoundTrips() {
        // Pick a non-trivial WCS with rotation; round-trip a handful
        // of pixels through both directions and confirm they land
        // within 0.1 px of the original. This is the contract PCC
        // relies on: catalog → pixel mapping is consistent with
        // pixel → catalog measurement.
        var wcs = WcsHeaders.FromSolveResult(
            raDeg: 83.82, decDeg: -5.39,    // M42 / Orion Nebula
            scaleArcsecPerPixel: 1.2, rotationDeg: 23.5,
            imageWidth: 4000, imageHeight: 3000);

        var samples = new[] {
            (100.0, 100.0),
            (3900.0, 100.0),
            (2000.0, 1500.0),
            (3900.0, 2900.0),
            (100.0, 2900.0),
        };
        foreach (var (px, py) in samples) {
            var (ra, dec) = wcs.PixelToRaDec(px, py);
            var (px2, py2) = wcs.RaDecToPixel(ra, dec);
            Assert.That(px2, Is.EqualTo(px).Within(0.1),
                $"X round-trip failed for pixel ({px}, {py}) → ({ra:F6}, {dec:F6}) → ({px2:F3}, {py2:F3})");
            Assert.That(py2, Is.EqualTo(py).Within(0.1),
                $"Y round-trip failed for pixel ({px}, {py}) → ({ra:F6}, {dec:F6}) → ({px2:F3}, {py2:F3})");
        }
    }

    // ─── FITS header round-trip ──────────────────────────────────────

    [Test]
    public void WcsHeaders_WrittenByFitsWriter_AreReadBackByFitsReader() {
        // Build a small RGB ImageData with a known WCS, serialise via
        // FITSWriter, deserialise via FITSReader, and confirm the WCS
        // survives intact. This is the contract the AstapSolver re-
        // stamp pathway depends on (ASTAP writes WCS, we read it
        // back) and the BatchStackingService WCS propagation.
        var tmp = Path.Combine(Path.GetTempPath(),
            "polaris-wcs-test-" + Guid.NewGuid().ToString("N") + ".fits");
        try {
            var wcsIn = WcsHeaders.FromSolveResult(
                raDeg: 56.75, decDeg: 24.12,    // Pleiades
                scaleArcsecPerPixel: 1.8, rotationDeg: -12,
                imageWidth: 100, imageHeight: 80);
            var pix = new ushort[100 * 80];
            Array.Fill(pix, (ushort)1000);

            FITSWriter.Write(new BaseImageData(pix,
                new ImageProperties {
                    Width = 100, Height = 80, BitDepth = 16,
                    Channels = 1, Wcs = wcsIn,
                },
                new ImageMetaData {
                    Target = new ImageMetaData.TargetInfo { Name = "Pleiades" },
                }), tmp);

            using var fs = File.OpenRead(tmp);
            var img = FITSReader.Read(fs);
            var wcsOut = img.Properties.Wcs;
            Assert.That(wcsOut, Is.Not.Null, "FITSReader did not extract WCS.");
            Assert.That(wcsOut!.RaDeg,     Is.EqualTo(wcsIn.RaDeg).Within(1e-6));
            Assert.That(wcsOut.DecDeg,     Is.EqualTo(wcsIn.DecDeg).Within(1e-6));
            Assert.That(wcsOut.RefPixelX,  Is.EqualTo(wcsIn.RefPixelX).Within(1e-6));
            Assert.That(wcsOut.RefPixelY,  Is.EqualTo(wcsIn.RefPixelY).Within(1e-6));
            Assert.That(wcsOut.CD11,       Is.EqualTo(wcsIn.CD11).Within(1e-12));
            Assert.That(wcsOut.CD22,       Is.EqualTo(wcsIn.CD22).Within(1e-12));
        } finally {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Test]
    public void FitsReader_WithoutWcsBlock_ReturnsNullWcs() {
        // Frames written without a Wcs property must read back with
        // Wcs == null, not a default-constructed WcsInfo (which
        // would silently feed PCC nonsense coordinates).
        var tmp = Path.Combine(Path.GetTempPath(),
            "polaris-wcs-null-test-" + Guid.NewGuid().ToString("N") + ".fits");
        try {
            var pix = new ushort[64];
            FITSWriter.Write(new BaseImageData(pix,
                new ImageProperties { Width = 8, Height = 8, BitDepth = 16, Channels = 1 },
                new ImageMetaData { Target = new ImageMetaData.TargetInfo { Name = "X" } }),
                tmp);
            using var fs = File.OpenRead(tmp);
            var img = FITSReader.Read(fs);
            Assert.That(img.Properties.Wcs, Is.Null,
                "Un-solved FITS should have no Wcs to avoid feeding " +
                "catalog matchers garbage coordinates.");
        } finally {
            try { File.Delete(tmp); } catch { }
        }
    }
}
