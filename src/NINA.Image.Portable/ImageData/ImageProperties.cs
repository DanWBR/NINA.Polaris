// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;

namespace NINA.Image.ImageData;

public record ImageProperties {
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitDepth { get; init; }
    public bool IsBayered { get; init; }
    public BayerPatternEnum BayerPattern { get; init; } = BayerPatternEnum.None;

    /// <summary>
    /// How many low bits of each 16-bit sample actually carry signal, when
    /// the pixel buffer holds RIGHT-ALIGNED raw ADC values (e.g. a 12-bit
    /// sensor delivering values 0..4095 in the low bits of a ushort). The
    /// native SDK backends set this for their RAW16 readout so consumers that
    /// need full-range 16-bit data (the SER recorder, which follows the
    /// FireCapture/ZWO convention of left-aligning to fill the container) can
    /// shift the samples up by <c>16 - SignificantBitDepth</c>.
    ///
    /// 0 = unset / already 16-bit-aligned. Leave it 0 for buffers that are
    /// already left-aligned (the RAW8 path widens with <c>px &lt;&lt; 8</c>) or
    /// where the alignment is unknown (INDI / Alpaca / ASCOM), so nothing
    /// shifts and the existing behaviour is preserved.
    /// </summary>
    public int SignificantBitDepth { get; init; }

    /// <summary>
    /// Number of colour planes in the pixel buffer. 1 = grayscale (the
    /// default, matches every existing call site that didn't set this
    /// explicitly); 3 = RGB stored plane-sequentially (R plane first,
    /// then G, then B). RGB FITS files (NAXIS=3 with NAXIS3=3, the
    /// PixInsight/Siril export convention) populate this so the
    /// thumbnailer can render in colour instead of dropping to the
    /// red channel.
    /// </summary>
    public int Channels { get; init; } = 1;

    /// <summary>
    /// World Coordinate System info, populated by <see cref="FITSReader"/>
    /// when the source FITS carries the WCS keyword block (CRVAL /
    /// CRPIX / CD matrix). Null for un-solved frames. Photometric
    /// Color Calibration (CCALB-3) uses this to project catalog
    /// (RA, Dec) onto image pixels without re-solving.
    /// </summary>
    public WcsInfo? Wcs { get; init; }

    public bool IsColor => Channels >= 3;

    public long PixelCount => (long)Width * Height;
}