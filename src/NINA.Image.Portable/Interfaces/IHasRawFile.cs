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

namespace NINA.Image.Interfaces;

/// <summary>
/// Optional companion to <see cref="IImageData"/> for capture backends
/// that deliver a vendor-native RAW file alongside the demosaiced /
/// luminance buffer the rest of Polaris consumes.
///
/// DSLR / mirrorless drivers (Canon EDSDK, Nikon SDK, Sony Camera
/// Remote SDK) produce two assets per capture: the original CR2 /
/// NEF / ARW raw file (full sensor data) plus a smaller embedded
/// JPEG suitable for live preview. The <see cref="IImageData"/>
/// itself carries the JPEG-derived luminance plane; this companion
/// carries the raw bytes so the persistence layer can write the
/// vendor-native file to disk instead of generating a FITS.
///
/// Implementations whose backend has no raw asset (INDI astronomy
/// cameras, Alpaca cameras, the legacy capture path) simply do not
/// implement this interface. Consumers check via <c>img is IHasRawFile</c>
/// and fall back to the FITS / XISF writer when it's absent.
/// </summary>
public interface IHasRawFile {
    /// <summary>The unmodified bytes the camera SDK handed back,
    /// typically CR2, NEF, or ARW. Null means no raw was captured
    /// for this frame (e.g. driver configured to JPEG-only mode).</summary>
    byte[]? RawFileBytes { get; }

    /// <summary>File extension to use when persisting <see cref="RawFileBytes"/>,
    /// including the leading dot. Examples: <c>.cr2</c>, <c>.nef</c>,
    /// <c>.arw</c>. Ignored when <see cref="RawFileBytes"/> is null.</summary>
    string? RawFileExtension { get; }
}