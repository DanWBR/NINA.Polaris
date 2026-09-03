using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The depth-inference rule shared by the SER recorder (when INDI / Alpaca
/// deliver raw ADC counts without saying how deep they are) and the
/// SerRescale salvage tool. Reference case: a real ASI2600MC Moon clip
/// recorded through INDI, header 16-bit, brightest sample 3415.
/// </summary>
[TestFixture]
public class SerBitDepthTests {
    [TestCase(3415, 12)]    // the field clip: 12-bit ADC counts in a 16-bit container
    [TestCase(4095, 12)]    // exactly full 12-bit
    [TestCase(4096, 14)]    // one past 12-bit rounds UP to the next common depth
    [TestCase(9000, 14)]
    [TestCase(16383, 14)]
    [TestCase(16384, 16)]
    [TestCase(65535, 16)]
    [TestCase(200, 8)]      // dim 8-bit-range value
    [TestCase(1000, 10)]
    [TestCase(0, 16)]       // black → "already full range", no shift
    public void RoundUpDepth_RoundsToCommonAdcDepths(int max, int expected) =>
        Assert.That(SerBitDepth.RoundUpDepth(max), Is.EqualTo(expected));

    [TestCase(12, 4)]
    [TestCase(14, 2)]
    [TestCase(10, 6)]
    [TestCase(8, 8)]
    [TestCase(16, 0)]
    [TestCase(0, 0)]        // unknown → no shift
    [TestCase(7, 0)]        // sub-8-bit is not a raw camera depth → no shift
    public void ShiftFor_FillsThe16BitContainer(int bits, int shift) =>
        Assert.That(SerBitDepth.ShiftFor(bits), Is.EqualTo(shift));

    [Test]
    public void AutoDetect_TwelveBitMoonFrame_IsTwelveBit() {
        var px = new ushort[64 * 64];
        for (int i = 0; i < px.Length; i++) px[i] = (ushort)(i % 50);
        px[1234] = 3415;
        Assert.That(SerBitDepth.AutoDetect(px), Is.EqualTo(12));
    }

    [Test]
    public void AutoDetect_TooDarkToJudge_ReturnsUnknown() {
        // Brightest sample below the floor: a guess here could read a 12-bit
        // sensor as 8/10-bit and clip every later bright frame, so refuse.
        var px = new ushort[32 * 32];
        px[5] = (ushort)(SerBitDepth.MinDetectableMax - 1);
        Assert.That(SerBitDepth.AutoDetect(px), Is.EqualTo(0));
    }

    [Test]
    public void AutoDetect_AtTheFloor_IsJudged() {
        var px = new ushort[32 * 32];
        px[5] = (ushort)SerBitDepth.MinDetectableMax;   // 1024 → 11 bits → rounds to 12
        Assert.That(SerBitDepth.AutoDetect(px), Is.EqualTo(12));
    }

    [Test]
    public void AutoDetect_AlreadyFullRange_IsSixteenBit_NoShift() {
        var px = new ushort[32 * 32];
        px[7] = 65000;
        int bits = SerBitDepth.AutoDetect(px);
        Assert.That(bits, Is.EqualTo(16));
        Assert.That(SerBitDepth.ShiftFor(bits), Is.EqualTo(0));
    }

    [Test]
    public void AutoDetect_BlackFrame_ReturnsUnknown() =>
        Assert.That(SerBitDepth.AutoDetect(new ushort[16 * 16]), Is.EqualTo(0));

    // ---- ResolveShift: the one per-clip decision the recorder makes ----

    private static ushort[] Frame(int max) { var f = new ushort[64]; f[3] = (ushort)max; return f; }

    [Test]
    public void ResolveShift_Off_NeverShifts_EvenWhenDriverReportsAndDataIsDim() {
        var r = SerBitDepth.ResolveShift(policyDepth: 16, reportedBits: 12, Frame(3415));
        Assert.That((r.Shift, r.Source), Is.EqualTo((0, SerBitDepth.ShiftSource.Off)));
    }

    [Test]
    public void ResolveShift_Explicit12_ShiftsBy4_RegardlessOfDriverOrData() {
        var r = SerBitDepth.ResolveShift(policyDepth: 12, reportedBits: 16, Frame(60000));
        Assert.That((r.Bits, r.Shift, r.Source), Is.EqualTo((12, 4, SerBitDepth.ShiftSource.Explicit)));
    }

    [Test]
    public void ResolveShift_Auto_DriverReportedDepthWins() {
        var r = SerBitDepth.ResolveShift(policyDepth: null, reportedBits: 14, Frame(300));
        Assert.That((r.Bits, r.Shift, r.Source), Is.EqualTo((14, 2, SerBitDepth.ShiftSource.Reported)));
    }

    [Test]
    public void ResolveShift_Auto_Unreported_InfersFromFirstFrame() {
        var r = SerBitDepth.ResolveShift(policyDepth: null, reportedBits: 0, Frame(3415));
        Assert.That((r.Bits, r.Shift, r.Source), Is.EqualTo((12, 4, SerBitDepth.ShiftSource.Inferred)));
    }

    [Test]
    public void ResolveShift_Auto_Unreported_DarkFrame_LeavesSamplesAlone() {
        var r = SerBitDepth.ResolveShift(policyDepth: null, reportedBits: 0, Frame(900));
        Assert.That((r.Shift, r.Source), Is.EqualTo((0, SerBitDepth.ShiftSource.Undetermined)));
    }

    [Test]
    public void ResolveShift_ExplicitOutOfRange_IsTreatedAsOff() {
        Assert.That(SerBitDepth.ResolveShift(7, 0, Frame(3415)).Source, Is.EqualTo(SerBitDepth.ShiftSource.Off));
        Assert.That(SerBitDepth.ResolveShift(32, 0, Frame(3415)).Source, Is.EqualTo(SerBitDepth.ShiftSource.Off));
    }
}
