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

namespace NINA.Image.FileFormat.FITS;

/// <summary>One header card as the viewer's header panel shows it.</summary>
public sealed record FitsHeaderCardView(string Keyword, string Value, string Comment);

/// <summary>A named group of cards in the viewer's header panel.</summary>
public sealed record FitsHeaderGroup(string Name, IReadOnlyList<FitsHeaderCardView> Cards);

/// <summary>
/// Sorts FITS header cards into the groups the viewer's header panel shows
/// (Observation, Instrument, Image, Site &amp; Weather, Processing, Other).
/// The grouping follows the categories of the FITS spec and the way
/// PixInsight and Siril lay headers out. The host's /api/files/fits-headers
/// and the browser-wasm viewer both build their panel from here.
/// </summary>
public static class FitsHeaderGroups {

    private static readonly string[] ImageKeys = {
        "SIMPLE","BITPIX","NAXIS","NAXIS1","NAXIS2","NAXIS3",
        "BZERO","BSCALE","BAYERPAT","XBAYROFF","YBAYROFF",
        "DATATYPE","CTYPE1","CTYPE2","CRVAL1","CRVAL2"
    };
    private static readonly string[] ObservationKeys = {
        "OBJECT","OBJCTRA","OBJCTDEC","OBJCTROT","RA","DEC",
        "DATE-OBS","DATE-AVG","MJD-OBS","EXPTIME","EXPOSURE",
        "FILTER","IMAGETYP","NCOMBINE","EXPTOTAL","FRAMENR"
    };
    private static readonly string[] InstrumentKeys = {
        "INSTRUME","TELESCOP","OTA","FOCALLEN","FOCRATIO","APERTURE",
        "XPIXSZ","YPIXSZ","XBINNING","YBINNING","GAIN","ISOSPEED","EGAIN",
        "OFFSET","READOUTM","CCD-TEMP","SET-TEMP","FWHEEL",
        "ROTATOR","ROTATANG","FOCNAME","FOCPOS","FOCTEMP","PIERSIDE"
    };
    private static readonly string[] SiteKeys = {
        "SITELAT","SITELONG","SITEELEV","SITENAME","OBSERVER",
        "OBSERVAT","CLOUDCVR","DEWPOINT","HUMIDITY","PRESSURE",
        "SKYBRGHT","MPSAS","AMBTEMP","WINDSPD","WINDDIR","WINDGUST"
    };
    private static readonly string[] ProcessingKeys = {
        "CREATOR","SWCREATE","CALSTAT","INTMETH","REJECT","BGREMOVE",
        "NRMETHOD","NRRADIUS","SHARPEN","SHARPAMT","SHARPRAD","SHARPTHR"
    };

    public static IReadOnlyList<FitsHeaderGroup> Build(IEnumerable<FITSHeaderCard> cards) {
        static bool In(string key, string[] set)
            => set.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

        var image = new List<FitsHeaderCardView>();
        var observation = new List<FitsHeaderCardView>();
        var instrument = new List<FitsHeaderCardView>();
        var site = new List<FitsHeaderCardView>();
        var processing = new List<FitsHeaderCardView>();
        var other = new List<FitsHeaderCardView>();

        foreach (var c in cards) {
            if (c.Keyword is "END" or "") continue;
            var dto = new FitsHeaderCardView(c.Keyword, c.Value?.Trim() ?? "", c.Comment ?? "");
            if (In(c.Keyword, ImageKeys)) image.Add(dto);
            else if (In(c.Keyword, ObservationKeys)) observation.Add(dto);
            else if (In(c.Keyword, InstrumentKeys)) instrument.Add(dto);
            else if (In(c.Keyword, SiteKeys)) site.Add(dto);
            else if (In(c.Keyword, ProcessingKeys)) processing.Add(dto);
            else other.Add(dto);
        }

        static IReadOnlyList<FitsHeaderCardView> Sort(List<FitsHeaderCardView> xs)
            => xs.OrderBy(c => c.Keyword, StringComparer.OrdinalIgnoreCase).ToList();

        return new[] {
            new FitsHeaderGroup("Observation", Sort(observation)),
            new FitsHeaderGroup("Instrument", Sort(instrument)),
            new FitsHeaderGroup("Image", Sort(image)),
            new FitsHeaderGroup("Site & Weather", Sort(site)),
            new FitsHeaderGroup("Processing", Sort(processing)),
            new FitsHeaderGroup("Other", Sort(other)),
        };
    }
}
