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

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// The one place that decides how many bits a raw 16-bit-container sample
/// really carries, and how far to left-align it so a SER fills its container
/// (the convention FireCapture / ASIVideoStack / AutoStakkert expect).
///
/// Native SDK drivers (ASI / SVBony / Player One) report the ADC depth
/// exactly. INDI and Alpaca do not: indi_asi_ccd advertises CCD_BITSPERPIXEL
/// = 16 for a RAW16 stream whose samples are the raw 12-bit ADC counts
/// (0..4095), and an Alpaca "maxadu" is just as often 65535. So when the
/// depth is unknown the recorder infers it from the data itself, with the
/// same rounding the SerRescale salvage tool uses, and refuses to guess on a
/// frame too dark to tell (which would over-shift and clip the highlights of
/// every later frame).
/// </summary>
public static class SerBitDepth {
    /// <summary>Common astro-camera ADC depths. A detected bit length is rounded
    /// UP to one of these so a clip whose brightest pixel never reached the ADC
    /// ceiling is still recognised as 12-bit rather than 11-bit.</summary>
    public static readonly int[] CommonDepths = { 8, 10, 12, 14, 16 };

    /// <summary>Brightest sample below which a frame is too dark to infer the
    /// depth from: a 12-bit target at 1/4 scale is still ≥ 1024, while a
    /// guess from anything dimmer risks reading a 12-bit sensor as 8/10-bit.</summary>
    public const int MinDetectableMax = 1024;

    /// <summary>Bit length of <paramref name="max"/> rounded up to a common
    /// ADC depth. 0 (a black frame) and anything above 14 bits map to 16, i.e.
    /// "already fills the container, no shift".</summary>
    public static int RoundUpDepth(int max) {
        if (max <= 0) return 16;
        int bitLen = 0;
        for (int v = max; v > 0; v >>= 1) bitLen++;
        foreach (var d in CommonDepths) if (d >= bitLen) return d;
        return 16;
    }

    /// <summary>Infers the significant depth of a raw frame from its brightest
    /// sample. Returns 0 when the frame is too dark to judge (max below
    /// <see cref="MinDetectableMax"/>), which callers treat as "unknown, do not
    /// shift".</summary>
    public static int AutoDetect(ReadOnlySpan<ushort> pixels) {
        int max = 0;
        foreach (var p in pixels) {
            if (p > max) {
                max = p;
                if (max >= 0xF000) break;   // clearly fills the range already
            }
        }
        return max < MinDetectableMax ? 0 : RoundUpDepth(max);
    }

    /// <summary>Left shift that fills a 16-bit container from a
    /// <paramref name="significantBits"/>-deep sample. 0 for unknown (0),
    /// sub-8-bit, or already-16-bit input.</summary>
    public static int ShiftFor(int significantBits) =>
        significantBits is >= 8 and < 16 ? 16 - significantBits : 0;

    /// <summary>How the recorder decided to align a clip, for the log.</summary>
    public enum ShiftSource { Off, Explicit, Reported, Inferred, Undetermined }

    /// <summary>The one decision the recorder makes per clip, taken on its
    /// first frame.
    /// <paramref name="policyDepth"/> is the operator's choice from the Video
    /// tab: null = Auto, 16 = Off (write the samples as they come), 8..15 =
    /// treat the stream as that many significant bits regardless of what the
    /// driver or the data say. Under Auto a depth reported by a native driver
    /// wins; otherwise the depth is inferred from the first frame, and a frame
    /// too dark to judge leaves the samples unshifted.
    /// The shift is really a lossless gain for a camera whose ADC is deeper
    /// than the exposure used (an ASI2600 at 16 bits peaking near 3400 gets
    /// x16); it saturates only if the scene later exceeds the chosen ceiling.</summary>
    public static (int Bits, int Shift, ShiftSource Source) ResolveShift(
            int? policyDepth, int reportedBits, ReadOnlySpan<ushort> firstFrame) {
        if (policyDepth is int p) {
            if (p >= 16 || p < 8) return (16, 0, ShiftSource.Off);
            return (p, ShiftFor(p), ShiftSource.Explicit);
        }
        if (reportedBits != 0) return (reportedBits, ShiftFor(reportedBits), ShiftSource.Reported);
        int inferred = AutoDetect(firstFrame);
        return inferred == 0
            ? (0, 0, ShiftSource.Undetermined)
            : (inferred, ShiftFor(inferred), ShiftSource.Inferred);
    }
}
