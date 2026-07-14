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
using NINA.Image.FileFormat.FITS;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the 16-bit integer FITS decode. FITS BITPIX=16 is a SIGNED sample;
/// cameras are unsigned and use one of two encodings:
///   (a) the standard unsigned convention BZERO=32768 (physical = signed + 32768);
///   (b) some drivers emit raw *unsigned* samples with BZERO=0 (or omit it).
///
/// The regression these guard against: field report of an SV405CC where
/// saturated star cores rendered BLACK. The 16-bit branch of
/// <see cref="FITSReader.DecodeIntegerPixels"/> used to interpret every sample
/// as signed and cast <c>(ushort)(val*bscale+bzero)</c> — with BZERO=0 that
/// sends any value &gt; 32767 negative, and the double→ushort cast of a negative
/// number is 0, i.e. the brightest pixels flip to black. The branch also lacked
/// the <c>Math.Clamp</c> the 8- and 32-bit branches already had.
/// </summary>
[TestFixture]
public class FITSReaderIntegerTests {

    // Physical values we expect back, spanning black → mid → saturated.
    private static readonly ushort[] Physical = {
        0,     245,   1000,  32767,
        32768, 40000, 64000, 65535
    };

    [Test]
    public void Read_Int16_UnsignedConvention_Bzero32768_RoundTripsExactly() {
        // Standard convention: stored signed sample = physical - 32768.
        var samples = new ushort[Physical.Length];
        for (int i = 0; i < Physical.Length; i++)
            samples[i] = unchecked((ushort)(short)(Physical[i] - 32768));

        var fits = BuildInt16Fits(4, 2, samples, bzero: 32768);
        var img = FITSReader.Read(new MemoryStream(fits));

        Assert.That(img.Properties.Width, Is.EqualTo(4));
        Assert.That(img.Properties.Height, Is.EqualTo(2));
        Assert.That(img.Data, Is.EqualTo(Physical), "unsigned (BZERO=32768) must round-trip exactly");
        Assert.That(img.Data[^1], Is.EqualTo(65535), "saturated pixel must stay white");
    }

    [Test]
    public void Read_Int16_RawUnsigned_Bzero0_SaturatedStaysWhite() {
        // Non-standard driver output: raw unsigned samples, BZERO=0. This is
        // the case that used to render bright pixels black.
        var samples = new ushort[Physical.Length];
        for (int i = 0; i < Physical.Length; i++) samples[i] = Physical[i];

        var fits = BuildInt16Fits(4, 2, samples, bzero: 0);
        var img = FITSReader.Read(new MemoryStream(fits));

        Assert.That(img.Data, Is.EqualTo(Physical), "raw-unsigned (BZERO=0) must decode 1:1, not wrap to black");
        Assert.That(img.Data[^1], Is.EqualTo(65535), "saturated pixel must stay white, not 0");
        Assert.That(img.Data[5], Is.EqualTo(40000), "high-but-not-saturated pixel must not go black");
    }

    [Test]
    public void Read_Int16_RawUnsigned_MissingBzeroCard_TreatedAsUnsigned() {
        // BZERO absent entirely (GetIntHeader defaults to 0) behaves like the
        // BZERO=0 case — the brightest pixels must still be white.
        var samples = new ushort[] { 0, 65535, 50000, 100 };
        var fits = BuildInt16Fits(2, 2, samples, bzero: null);
        var img = FITSReader.Read(new MemoryStream(fits));

        Assert.That(img.Data[0], Is.EqualTo(0));
        Assert.That(img.Data[1], Is.EqualTo(65535), "saturated pixel must stay white with no BZERO card");
        Assert.That(img.Data[2], Is.EqualTo(50000));
    }

    // --- Helpers ----------------------------------------------------

    /// <summary>
    /// Build a minimal, valid 16-bit-integer FITS byte stream. The
    /// <paramref name="samples"/> are the raw 16-bit values written
    /// big-endian into the data block exactly as-is; the header advertises
    /// <paramref name="bzero"/> (omitted when null) and BSCALE=1.
    /// </summary>
    private static byte[] BuildInt16Fits(int width, int height, ushort[] samples, int? bzero) {
        Assert.That(samples.Length, Is.EqualTo(width * height),
            "sample buffer size must equal width*height");

        var headerCards = new List<string> {
            FormatCard("SIMPLE", "T"),
            FormatCard("BITPIX", "16"),
            FormatCard("NAXIS",  "2"),
            FormatCard("NAXIS1", width.ToString()),
            FormatCard("NAXIS2", height.ToString()),
            FormatCard("BSCALE", "1"),
        };
        if (bzero.HasValue) headerCards.Add(FormatCard("BZERO", bzero.Value.ToString()));
        headerCards.Add("END" + new string(' ', 77));

        var headerBlock = new byte[2880];
        for (int i = 0; i < headerCards.Count; i++) {
            var bytes = System.Text.Encoding.ASCII.GetBytes(headerCards[i]);
            Array.Copy(bytes, 0, headerBlock, i * 80, Math.Min(80, bytes.Length));
        }
        for (int i = headerCards.Count * 80; i < 2880; i++) headerBlock[i] = (byte)' ';

        int dataBytes = samples.Length * 2;
        int paddedDataBytes = ((dataBytes + 2879) / 2880) * 2880;
        var dataBlock = new byte[paddedDataBytes];
        for (int i = 0; i < samples.Length; i++) {
            dataBlock[i * 2 + 0] = (byte)(samples[i] >> 8);   // big-endian high byte
            dataBlock[i * 2 + 1] = (byte)(samples[i] & 0xFF); // low byte
        }

        var combined = new byte[headerBlock.Length + dataBlock.Length];
        Buffer.BlockCopy(headerBlock, 0, combined, 0, headerBlock.Length);
        Buffer.BlockCopy(dataBlock,   0, combined, headerBlock.Length, dataBlock.Length);
        return combined;
    }

    private static string FormatCard(string keyword, string value) {
        var kw = keyword.PadRight(8);
        var val = value.PadLeft(20);
        return (kw + "= " + val).PadRight(80);
    }
}
