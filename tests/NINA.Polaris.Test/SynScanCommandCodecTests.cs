using NUnit.Framework;
using NINA.Mount.SynScanWifi;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the LX200 / SynScan sexagesimal encoding the Wi-Fi driver
/// puts on the wire. The mount silently ignores malformed
/// coordinates (or replies "0", slew failed) so a regression here
/// surfaces only at run time on the user's first slew attempt.
/// </summary>
[TestFixture]
public class SynScanCommandCodecTests {

    // --- RA encoding -----------------------------------------------

    [Test]
    public void FormatRA_Zero_Encodes000000() {
        Assert.That(SynScanCommandCodec.FormatRA(0), Is.EqualTo("00:00:00"));
    }

    [Test]
    public void FormatRA_M31_Encodes004244() {
        // M31 = RA 0h 42m 44s. Round-trip pin: this is the exact
        // string the driver pushes onto the wire for the most common
        // first-light target.
        Assert.That(SynScanCommandCodec.FormatRA(0 + 42 / 60.0 + 44 / 3600.0),
            Is.EqualTo("00:42:44"));
    }

    [Test]
    public void FormatRA_WrapsAround24Hours() {
        // 24h = 0h. Negative hours wrap up. Both happen when the
        // caller hands us a not-yet-normalised value.
        Assert.That(SynScanCommandCodec.FormatRA(24.0), Is.EqualTo("00:00:00"));
        Assert.That(SynScanCommandCodec.FormatRA(-1.0), Is.EqualTo("23:00:00"));
    }

    // --- Dec encoding ----------------------------------------------

    [Test]
    public void FormatDec_PositiveSouthernEnd_EncodesWithSign() {
        // LX200 spec requires a sign even on positive values.
        Assert.That(SynScanCommandCodec.FormatDec(41.27),
            Is.EqualTo("+41*16:12"));  // M31 declination
    }

    [Test]
    public void FormatDec_Negative_FormatsWithMinus() {
        // M42 ~ -5° 23' 28"
        Assert.That(SynScanCommandCodec.FormatDec(-5.391),
            Is.EqualTo("-05*23:28"));
    }

    [Test]
    public void FormatDec_ClampsBeyondPoles() {
        Assert.That(SynScanCommandCodec.FormatDec( 95), Is.EqualTo("+90*00:00"));
        Assert.That(SynScanCommandCodec.FormatDec(-95), Is.EqualTo("-90*00:00"));
    }

    // --- Round trips -----------------------------------------------

    [Test]
    public void RoundTrip_RA_PreservesValueToSecond() {
        // The codec quantises to 1-second precision, so round-trip
        // tolerance is ~0.5 seconds = ~0.00014 hours.
        var input = 13 + 25 / 60.0 + 48 / 3600.0;   // Spica
        var encoded = SynScanCommandCodec.FormatRA(input);
        var decoded = SynScanCommandCodec.ParseRA(encoded);
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!.Value, Is.EqualTo(input).Within(1.0 / 3600));
    }

    [Test]
    public void RoundTrip_Dec_PreservesValueToSecond() {
        var input = -11 - 9 / 60.0 - 41 / 3600.0;   // Spica
        var encoded = SynScanCommandCodec.FormatDec(input);
        var decoded = SynScanCommandCodec.ParseDec(encoded);
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!.Value, Is.EqualTo(input).Within(1.0 / 3600));
    }

    // --- Parsing robustness ----------------------------------------

    [Test]
    public void ParseRA_TrailingHash_Tolerated() {
        Assert.That(SynScanCommandCodec.ParseRA("13:25:48#"),
            Is.EqualTo(13 + 25 / 60.0 + 48 / 3600.0).Within(1e-9));
    }

    [Test]
    public void ParseDec_AcceptsDegreeSymbol() {
        // Some SynScan firmware variants emit ° instead of * in the
        // reply. Codec tolerates both.
        Assert.That(SynScanCommandCodec.ParseDec("+41°16:12#"),
            Is.EqualTo(41 + 16 / 60.0 + 12 / 3600.0).Within(1e-9));
    }

    [Test]
    public void ParseRA_Garbage_ReturnsNull() {
        Assert.That(SynScanCommandCodec.ParseRA(""), Is.Null);
        Assert.That(SynScanCommandCodec.ParseRA("not a coord"), Is.Null);
        Assert.That(SynScanCommandCodec.ParseRA("13:25"), Is.Null);
    }

    [Test]
    public void ParseDec_NoSign_TreatedAsPositive() {
        Assert.That(SynScanCommandCodec.ParseDec("41*16:12"),
            Is.EqualTo(41 + 16 / 60.0 + 12 / 3600.0).Within(1e-9));
    }
}
