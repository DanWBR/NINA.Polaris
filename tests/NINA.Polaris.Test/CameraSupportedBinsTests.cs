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
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

/// <summary>
/// Which binnings a camera admits to, and what happens to the ones it does not.
///
/// Field, 2026-08-12 (OPi 5 Pro, ASI585MC): a PREVIEW at BIN3 over INDI came
/// back 3840x2160, full sensor. The same camera on the native ZWO SDK binned
/// correctly at 2 AND at 3, so the value was fine and the INDI path was not.
/// Underneath both: nothing ever asked the camera which bins it takes. The
/// panel offered 1/2/3/4 to everything, ASISetROIFormat's return code was
/// discarded, and indi_asi_ccd publishes CCD_BINNING with min=max=step=0, so
/// there was nothing to validate against on either side.
/// </summary>
[TestFixture]
public class CameraSupportedBinsTests {

    private static CameraCapabilities Caps(params int[] bins) =>
        CameraCapabilities.Astro with { SupportedBins = bins };

    /// <summary>An unprobeable backend must keep working exactly as before.
    /// Empty is "unknown", and treating it as "supports nothing" would take
    /// binning away from every camera Polaris cannot interrogate.</summary>
    [Test]
    public void AnEmptyList_MeansUnknown_AndAllowsAnything() {
        var caps = CameraCapabilities.Astro;

        Assert.That(caps.SupportedBins, Is.Empty);
        foreach (var bin in new[] { 1, 2, 3, 4, 8 })
            Assert.That(caps.AllowsBin(bin), Is.True, $"bin {bin} must pass when the list is unknown");
    }

    [Test]
    public void ADeclaredList_IsAuthoritative() {
        var caps = Caps(1, 2, 4);

        Assert.That(caps.AllowsBin(1), Is.True);
        Assert.That(caps.AllowsBin(2), Is.True);
        Assert.That(caps.AllowsBin(4), Is.True);
        Assert.That(caps.AllowsBin(3), Is.False, "3 is missing from this camera's list");
        Assert.That(caps.AllowsBin(8), Is.False);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ANonsenseBin_IsNeverAllowed(int bin) {
        Assert.That(CameraCapabilities.Astro.AllowsBin(bin), Is.False,
            "even an unknown list must not admit a bin below 1");
        Assert.That(Caps(1, 2).AllowsBin(bin), Is.False);
    }

    /// <summary>The default profiles must not accidentally start declaring a
    /// list, or every camera using them would suddenly validate against it.</summary>
    [Test]
    public void TheStockProfilesDeclareNothing() {
        Assert.That(CameraCapabilities.Astro.SupportedBins, Is.Empty);
        Assert.That(CameraCapabilities.Dslr.SupportedBins, Is.Empty);
    }

    /// <summary>`with { SupportedBins = ... }` has to survive the record's
    /// init-only wiring; a positional record with a custom property is easy to
    /// get wrong in a way that silently drops the value.</summary>
    [Test]
    public void TheListSurvivesAWithExpression() {
        var caps = CameraCapabilities.Astro with { SupportedBins = new[] { 1, 2 } };
        Assert.That(caps.SupportedBins, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(caps.SupportsBinning, Is.True, "the rest of the profile must be untouched");
    }

    [Test]
    public void ANullListIsTreatedAsEmpty() {
        var caps = CameraCapabilities.Astro with { SupportedBins = null! };
        Assert.That(caps.SupportedBins, Is.Not.Null);
        Assert.That(caps.AllowsBin(3), Is.True);
    }
}

/// <summary>
/// Decoding ASI_CAMERA_INFO.SupportedBins, which is a fixed 16-int array the
/// SDK terminates with a zero. Everything past the terminator is whatever was
/// in that memory, so a reader that does not stop will happily offer bin 32.
/// </summary>
[TestFixture]
public class AsiSupportedBinsParsingTests {

    private static IReadOnlyList<int> Parse(params int[] raw) =>
        NINA.Camera.ZwoSdk.AsiSdkCamera.ParseSupportedBins(raw);

    /// <summary>What an ASI585 actually reports.</summary>
    [Test]
    public void ATypicalAsiArray_StopsAtTheTerminator() {
        var bins = Parse(1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.That(bins, Is.EqualTo(new[] { 1, 2, 3, 4 }));
    }

    /// <summary>THE ONE THAT MATTERS. Past the terminator the array holds
    /// uninitialised memory; a reader that keeps going publishes garbage as
    /// supported hardware modes.</summary>
    [Test]
    public void GarbageAfterTheTerminator_IsIgnored() {
        var bins = Parse(1, 2, 0, 32767, -1, 99, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.That(bins, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void TheResultIsSortedAndDeduplicated() {
        Assert.That(Parse(4, 1, 2, 1, 0), Is.EqualTo(new[] { 1, 2, 4 }));
    }

    [Test]
    public void AFullArrayWithNoTerminator_IsStillBounded() {
        var bins = Parse(Enumerable.Range(1, 16).ToArray());
        Assert.That(bins.Count, Is.EqualTo(16), "16 is the array size; nothing may read past it");
        Assert.That(bins.First(), Is.EqualTo(1));
        Assert.That(bins.Last(), Is.EqualTo(16));
    }

    [TestCase]
    public void AnEmptyOrNullArray_YieldsUnknownRatherThanThrowing() {
        Assert.That(NINA.Camera.ZwoSdk.AsiSdkCamera.ParseSupportedBins(null), Is.Empty);
        Assert.That(Parse(), Is.Empty);
        Assert.That(Parse(0, 0, 0), Is.Empty, "a leading terminator means the SDK told us nothing");
    }
}
