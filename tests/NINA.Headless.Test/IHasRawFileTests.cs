using NUnit.Framework;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Test;

/// <summary>
/// Pins the <see cref="IHasRawFile"/> contract that the DSLR drivers
/// use to short-circuit the ImageWriterService's FITS / XISF path
/// and persist the camera-native RAW (CR2 / NEF / ARW) instead.
/// </summary>
[TestFixture]
public class IHasRawFileTests {

    [Test]
    public void BaseImageData_ImplementsIHasRawFile_AndDefaultsAreNull() {
        // Every BaseImageData satisfies the optional companion
        // interface — backends that have no raw simply leave the
        // properties null and the writer falls through to FITS.
        var props = new ImageProperties { Width = 1, Height = 1, BitDepth = 16 };
        var img = new BaseImageData(new ushort[1], props);
        Assert.That(img, Is.InstanceOf<IHasRawFile>());
        var raw = (IHasRawFile)img;
        Assert.That(raw.RawFileBytes, Is.Null);
        Assert.That(raw.RawFileExtension, Is.Null);
    }

    [Test]
    public void BaseImageData_RawFieldsSet_SignalsRawPersistence() {
        // DSLR driver path: attach the camera-native bytes + the
        // matching extension so the writer can save the file
        // verbatim.
        var props = new ImageProperties { Width = 1, Height = 1, BitDepth = 16 };
        var img = new BaseImageData(new ushort[1], props) {
            RawFileBytes = new byte[] { 0x49, 0x49, 0x2A, 0x00 },  // little-endian TIFF marker (CR2 starts with this)
            RawFileExtension = ".cr2"
        };
        var raw = (IHasRawFile)img;
        Assert.That(raw.RawFileBytes, Is.EqualTo(new byte[] { 0x49, 0x49, 0x2A, 0x00 }));
        Assert.That(raw.RawFileExtension, Is.EqualTo(".cr2"));
    }
}
