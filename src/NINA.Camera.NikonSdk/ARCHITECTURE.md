# NINA.Camera.NikonSdk, Architecture

Windows-only `ICamera` wrapper for the **Nikon Imaging SDK** (Z-series
mirrorless) and **Nikon MAID SDK** (classic DSLRs).

For the cross-vendor design rationale (why a separate project, how the
native DLLs are sourced, how it plugs into Polaris), read
[NINA.Camera.CanonEdsdk/ARCHITECTURE.md](../NINA.Camera.CanonEdsdk/ARCHITECTURE.md)
, the structure is identical.

## Layout

```
src/NINA.Camera.NikonSdk/
  NINA.Camera.NikonSdk.csproj    # net10.0-windows
  NikonSdkDiscovery.cs           # static EnumerateCameras()
  NikonSdkCamera.cs              # ICamera impl
  NikonSdkRegistry.cs            # DI helpers
```

## Notes specific to Nikon

- **Two SDKs**: Nikon Imaging SDK (Z series) has the cleaner API and
  is the default path. MAID SDK is older and harder to wrap; only
  used as a fallback when the user's DSLR isn't supported by the
  Imaging SDK.
- **Registration required**: Nikon's developer portal requires you to
  register before downloading. The Polaris-side install procedure
  walks the user through it (see `docs/dslr-windows-nikon.md` when
  present).
- **NEF raw**: written verbatim to disk via `IImageData.RawFileBytes`,
  same convention as Canon CR2.

## See also

- [NINA.Camera.CanonEdsdk/ARCHITECTURE.md](../NINA.Camera.CanonEdsdk/ARCHITECTURE.md)
 , full pattern explanation
- [Root ARCHITECTURE.md](../../ARCHITECTURE.md)
