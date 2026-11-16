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
using System.Threading;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.INDI.Devices;

/// <summary>
/// A DSLR frame whose pixels are decoded from the camera's RAW the first time
/// somebody reads <see cref="Data"/>, not when the BLOB arrives.
///
/// The RAW decode is the expensive part of a DSLR capture: libraw's demosaic of
/// a 33 MP ARW takes 7.8 s on a Raspberry Pi 4, and it used to run on the INDI
/// reader thread before the exposure was reported complete. Nothing between
/// the BLOB and the next exposure needs the pixels: the writer saves the RAW
/// bytes verbatim, so with the decode deferred the sequencer can save the frame
/// and start the next exposure while the preview is still being decoded. The
/// consumers that do need pixels (relay, star detection, live stack, plate
/// solver, flat wizard) pay the same decode as before, once, on their own
/// thread. <see cref="IndiCamera"/> builds these; the sequencers only see
/// <see cref="IImageData"/>.
/// </summary>
public sealed class DeferredDslrFrame : IImageData, IHasRawFile {
    private readonly Lazy<ushort[]> _pixels;
    private IImageStatistics? _statistics;

    public ImageProperties Properties { get; }
    public ImageMetaData MetaData { get; }
    public byte[]? RawFileBytes { get; }
    public string? RawFileExtension { get; }

    /// <summary>Decoded pixels. The first read runs the decoder, later reads
    /// and concurrent readers share the one result. The decoder must return a
    /// buffer of exactly Width times Height samples and never throw; a shorter
    /// buffer is replaced by zeros so a consumer indexing by the properties
    /// cannot read past the end.</summary>
    public ushort[] Data => _pixels.Value;

    public IImageStatistics Statistics => _statistics ??= ImageStatistics.Create(this);

    /// <summary>True once <see cref="Data"/> has been read. Tests and
    /// diagnostics use it to tell a deferred frame from a decoded one.</summary>
    public bool IsDecoded => _pixels.IsValueCreated;

    /// <param name="decode">Produces the Bayer mosaic for this frame, called
    /// at most once, on the first reader's thread. May return null or throw;
    /// both are reported by the decoder itself and yield a zero frame here.</param>
    public DeferredDslrFrame(ImageProperties properties, ImageMetaData metaData,
            byte[] rawFileBytes, string rawFileExtension, Func<ushort[]?> decode)
        : this(properties, metaData, rawFileBytes, rawFileExtension,
               new Lazy<ushort[]>(Guard(decode, properties), LazyThreadSafetyMode.ExecutionAndPublication)) { }

    private DeferredDslrFrame(ImageProperties properties, ImageMetaData metaData,
            byte[] rawFileBytes, string rawFileExtension, Lazy<ushort[]> pixels) {
        Properties = properties;
        MetaData = metaData ?? new ImageMetaData();
        RawFileBytes = rawFileBytes ?? throw new ArgumentNullException(nameof(rawFileBytes));
        RawFileExtension = rawFileExtension ?? throw new ArgumentNullException(nameof(rawFileExtension));
        _pixels = pixels;
    }

    /// <summary>The same frame with new properties, sharing the pending (or
    /// finished) decode, so re-tagging the Bayer pattern or bit depth after the
    /// BLOB does not force the decode onto the INDI reader thread.</summary>
    public DeferredDslrFrame WithProperties(ImageProperties properties) =>
        new(properties, MetaData, RawFileBytes!, RawFileExtension!, _pixels);

    private static Func<ushort[]> Guard(Func<ushort[]?> decode, ImageProperties props) => () => {
        long expected = (long)props.Width * props.Height;
        ushort[]? px = null;
        try { px = decode(); } catch { /* the decoder reports its own failure */ }
        if (px != null && px.LongLength == expected) return px;
        return new ushort[expected];
    };
}
