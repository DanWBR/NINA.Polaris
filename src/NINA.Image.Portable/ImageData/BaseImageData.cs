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

using NINA.Image.Interfaces;

namespace NINA.Image.ImageData;

public class BaseImageData : IImageData, IHasRawFile {
    public ImageProperties Properties { get; }
    public ImageMetaData MetaData { get; }
    public ushort[] Data { get; }

    private IImageStatistics? _statistics;
    public IImageStatistics Statistics => _statistics ??= ImageStatistics.Create(this);

    /// <summary>Optional vendor-native RAW bytes attached by DSLR /
    /// mirrorless drivers. Null for everything else. See
    /// <see cref="IHasRawFile"/> for the persistence contract.</summary>
    public byte[]? RawFileBytes { get; set; }
    public string? RawFileExtension { get; set; }

    public BaseImageData(ushort[] data, ImageProperties properties, ImageMetaData? metaData = null) {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Properties = properties;
        MetaData = metaData ?? new ImageMetaData();
    }
}