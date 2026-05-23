# NINA.Image.Portable — Architecture

Pure code library for **image I/O, analysis, and math**. No host
dependencies (no ASP.NET, no INDI client). Other projects depend on
this; it depends on nothing in the solution.

This is the home of the FITS reader/writer, the XISF writer, the star
detector, the autostretch, and the math primitives the live stacker
and Studio integration jobs use.

## Layout

```
src/NINA.Image.Portable/
  Interfaces/                  # contracts other layers depend on
    IImageData.cs              # the canonical "an image in memory"
    IImageBuffer.cs            # raw pixel access (ushort[])
    IImageStatistics.cs        # mean/median/MAD/etc.
    ICamera.cs                 # abstract camera surface (INDI / Alpaca /
                               # Canon / Nikon / Sony all implement this)
    ITelescope.cs              # abstract mount surface
    IHasRawFile.cs             # optional: DSLR raw bytes passthrough
  ImageData/
    BaseImageData.cs           # the default IImageData impl
    ImageBuffer.cs             # ushort[] + width/height/bitdepth/bayer
    ImageMetaData.cs           # camera/telescope/observer/target/filter
    ImageProperties.cs         # width/height/bitdepth/bayer + accessors
    ImageStatistics.cs         # streaming-friendly stats computation
  FileFormat/
    FITS/
      FITSReader.cs            # BITPIX 8/16/32/-32, BZERO/BSCALE,
                               # standard + custom keywords
      FITSWriter.cs            # symmetric writer
    XISF/
      XISFWriter.cs            # PixInsight format, optional LZ4
    TIFF/
      TiffWriter.cs            # 16-bit TIFF via SkiaSharp
  ImageAnalysis/
    StarDetector.cs            # local-max + HFR + flux per detection
    StarMatcher.cs             # triangle invariant for cross-frame
                               # alignment (used by LiveStacking)
    AffineTransform.cs         # 3-point linear least-squares fit
    ImageResampler.cs          # bilinear warp under an affine
    BayerDebayer.cs            # RGGB/GRBG/BGGR/GBRG → RGB
    AutoStretch.cs             # MTF auto-stretch (display preview)
    BackgroundExtractor.cs     # polynomial gradient removal
    GaussianBlur.cs            # separable convolution kernel
    UnsharpMask.cs             # image + factor × (image − blurred)
    IntegrationMath.cs         # mean / median / sigma-clipped per-pixel
    JpegEncoder.cs             # ushort[] → JPEG bytes (for live preview)
    DetectedStar.cs            # record: x, y, hfr, peak, flux
```

## Design principles

- **Pure code**: no `System.IO.File` paths that hardcode `%LOCALAPPDATA%`,
  no logger sinks, no DI. Everything takes its inputs and produces
  outputs.
- **`ushort[]` is the canonical pixel storage**. 16-bit monochrome —
  bayer pattern preserved as metadata, not exploded into 3 planes
  until debayer is explicitly requested.
- **`IImageData` is the boundary type** between this library and the
  rest of the system. A capture from `IndiCamera.CaptureAsync`
  produces an `IImageData`; the live stacker, the writer, the image
  relay, the studio job services all consume `IImageData`.
- **Deterministic + testable**: every algorithm is unit-testable on
  synthetic inputs. The test suite includes golden-value tests for
  the math primitives (autostretch curve, integration, debayer).

## Image formats

### FITS

NASA standard astronomical format. ASCII header (80-char records,
multiple of 2880-byte blocks) + binary pixel data. `FITSReader`
handles all common BITPIX values, BZERO/BSCALE rescaling, BAYERPAT
detection, and the standard astronomical keywords. `FITSWriter` is
symmetric.

### XISF

PixInsight's format. Binary magic + XML header (padded to 4 KB
blocks) + binary attachment. Pixel data uint16 LE, optional LZ4
compression on the attachment. Metadata via `<FITSKeyword>` + native
`<Property>` elements.

### TIFF

16-bit grayscale TIFF via SkiaSharp for post-processed exports from
Studio. Not used for capture (FITS / XISF are the capture formats).

## Star detection + alignment

`StarDetector` is the workhorse. Given a `ushort[]` image + width +
height, it:

1. Computes a local-background estimate (sliding median in tiles)
2. Finds local-maxima above `background + N × MAD`
3. Computes HFR (half-flux radius) per candidate via radial profile fit
4. Computes peak ADU + total flux per candidate
5. Returns `List<DetectedStar>`

`StarMatcher` cross-matches detected stars across two frames using
triangle-invariant features (ratios of sides of triangles formed by
triplets of stars — invariant under translation, rotation, scale).
The output is a list of corresponding star pairs.

`AffineTransform.FitLeastSquares(pairs)` produces a 2×3 affine that
maps frame B onto frame A.

`ImageResampler.Warp(image, affine)` applies the affine via bilinear
sampling.

Together these form the LiveStacking alignment pipeline:
detect → match → fit → warp → integrate.

## Image stretching

`AutoStretch.MtfStretch(image, options)` applies the standard MTF
(midtone transfer function) used by PixInsight/Siril:

```
m(x) = (m - 1) × x / ((2m - 1) × x - m)
```

with `m` chosen automatically from the image's median + MAD so that
the stretched midtone lands at a configurable target (default 0.25).

The output is a `byte[]` for display in browser-side `<canvas>`.

## How NINA.Polaris consumes this

- **Capture path**: `IndiCamera.CaptureAsync` returns `IImageData`
  built by `FITSReader` from the BLOB. `ImageRelayService` then
  encodes a JPEG via `JpegEncoder` for the browser, and
  `ImageWriterService` writes the FITS to disk via `FITSWriter`.
- **Live stacking**: `LiveStackingService` runs `StarDetector`,
  `StarMatcher`, `AffineTransform.FitLeastSquares`,
  `ImageResampler.Warp`, then accumulates into a running mean.
- **Studio**: `MasterFrameService` uses `IntegrationMath`,
  `CalibrationService` uses pixel-wise arithmetic on
  `ushort[]` arrays, `BatchStackingService` reuses the LiveStacking
  alignment primitives for offline integration.

## See also

- [Root ARCHITECTURE.md](../../ARCHITECTURE.md)
- The capture flow in the root doc shows where `IImageData` enters
  and exits the system
