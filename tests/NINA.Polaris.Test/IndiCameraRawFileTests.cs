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

using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Devices;

namespace NINA.Polaris.Test;

/// <summary>A gphoto DSLR frame must reach the writer with the camera's own
/// file still attached, or it is saved as FITS of the preview decode instead
/// of the CR3 (a Canon R8 session: lights as LibRaw renderings, darks and bias
/// as the 8-bit embedded JPEG).</summary>
[TestFixture]
public class IndiCameraRawFileTests {
    private static BaseImageData DslrFrame() {
        var props = new ImageProperties { Width = 4, Height = 2, BitDepth = 16 };
        return new BaseImageData(new ushort[8], props, new ImageMetaData()) {
            RawFileBytes = new byte[] { 1, 2, 3 },
            RawFileExtension = ".cr3"
        };
    }

    [Test]
    public void ARebuiltFrame_KeepsTheCameraFile() {
        var src = DslrFrame();

        var rebuilt = IndiCamera.WithProperties(src, src.Properties with { SignificantBitDepth = 14 });

        Assert.That(rebuilt, Is.InstanceOf<IHasRawFile>());
        var raw = (IHasRawFile)rebuilt;
        Assert.Multiple(() => {
            Assert.That(raw.RawFileBytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(raw.RawFileExtension, Is.EqualTo(".cr3"));
            Assert.That(rebuilt.Properties.SignificantBitDepth, Is.EqualTo(14));
        });
    }

    [Test]
    public void ARebuiltFrame_KeepsTheCameraFile_WhenTheBayerPatternIsAdded() {
        var src = DslrFrame();
        var rebuilt = IndiCamera.WithProperties(src,
            src.Properties with { BayerPattern = BayerPatternEnum.RGGB, IsBayered = true });
        Assert.That(((IHasRawFile)rebuilt).RawFileExtension, Is.EqualTo(".cr3"));
    }

    [Test]
    public void AFrameWithoutACameraFile_StaysWithout() {
        var src = new BaseImageData(new ushort[8], new ImageProperties { Width = 4, Height = 2, BitDepth = 16 },
            new ImageMetaData());
        var rebuilt = IndiCamera.WithProperties(src, src.Properties with { SignificantBitDepth = 12 });
        Assert.That(((IHasRawFile)rebuilt).RawFileBytes, Is.Null);
    }
}
