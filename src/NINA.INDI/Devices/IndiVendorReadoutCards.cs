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

using NINA.Image.ImageData;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

/// <summary>
/// Sensor readout modes that change the pixels and have no standard FITS
/// keyword, read off the driver at capture time and written into the header.
///
/// <para>Why: a ToupTek ATR2600C has low noise, conversion gain and high full
/// well switches. A light taken with one setting does not calibrate against a
/// dark taken with another, and nothing in the saved file said which was in
/// force, so the mismatch only showed up as a bad calibration weeks later
/// (issue #31). Ekos records the first and the third under LOWNOISE and
/// FULLWELL; conversion gain has no established keyword, so CONVMODE follows
/// the reporter's suggestion.</para>
///
/// <para>A table rather than a ToupTek special case: the same shape covers the
/// read modes other vendors expose, and adding one is a line here instead of a
/// branch in the capture path. Everything is best effort, a camera that
/// publishes none of these contributes no cards.</para>
/// </summary>
public static class IndiVendorReadoutCards {

    /// <summary>One switch vector worth recording: which property to read,
    /// which FITS keyword it becomes, and what each element means.</summary>
    /// <param name="Property">INDI switch vector name.</param>
    /// <param name="Keyword">FITS keyword, eight characters or fewer.</param>
    /// <param name="ValueByElement">Element name to the value written when
    /// that element is the one switched on.</param>
    public readonly record struct Mapping(
        string Property, string Keyword, string Comment,
        IReadOnlyDictionary<string, string> ValueByElement);

    private static readonly IReadOnlyDictionary<string, string> EnabledDisabled =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["INDI_ENABLED"] = "ON",
            ["INDI_DISABLED"] = "OFF",
        };

    /// <summary>The known modes. ToupTek-platform cameras today; the ZWO and
    /// SVBony drivers expose their equivalents under different names and can be
    /// added here when someone has one to confirm the element names on.</summary>
    public static readonly IReadOnlyList<Mapping> Known = new[] {
        new Mapping("TC_LOW_NOISE", "LOWNOISE", "Low noise readout mode", EnabledDisabled),
        new Mapping("TC_HIGHFULLWELL", "FULLWELL", "High full well mode", EnabledDisabled),
        new Mapping("TC_CONVERSION_GAIN", "CONVMODE", "Conversion gain mode",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["GAIN_LOW"] = "LOW",
                ["GAIN_HIGH"] = "HIGH",
                ["GAIN_HDR"] = "HDR",
            }),
    };

    /// <summary>
    /// Build the cards for whatever the device currently publishes.
    ///
    /// <paramref name="readSwitch"/> returns the switch vector for a property
    /// name, or null when the device does not have it. Separated from the
    /// camera so the mapping can be tested without a driver.
    /// </summary>
    public static List<VendorFitsCard> Collect(
            Func<string, IndiSwitchProperty?> readSwitch,
            IReadOnlyList<Mapping>? mappings = null) {
        var cards = new List<VendorFitsCard>();
        foreach (var m in mappings ?? Known) {
            IndiSwitchProperty? sw;
            try { sw = readSwitch(m.Property); } catch { continue; }
            if (sw == null || sw.Values.Count == 0) continue;

            // The element that is on. A one-of-many vector has exactly one; a
            // driver that somehow reports none contributes nothing rather than
            // a guess.
            string? onElement = null;
            foreach (var kv in sw.Values) {
                if (kv.Value) { onElement = kv.Key; break; }
            }
            if (onElement == null) continue;

            if (!m.ValueByElement.TryGetValue(onElement, out var value)) {
                // An element the table does not know: record the driver's own
                // name rather than drop the setting. Better an unfamiliar
                // string in the header than silence about a mode that changed
                // the pixels.
                value = onElement;
            }
            cards.Add(new VendorFitsCard(m.Keyword, value, m.Comment));
        }
        return cards;
    }
}
