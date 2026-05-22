# NINA.Camera.SonySdk — Architecture

`ICamera` wrapper for the **Sony Camera Remote SDK 2.x**.

For the cross-vendor design rationale (why a separate project, how the
native libraries are sourced, how it plugs into Polaris), read
[NINA.Camera.CanonEdsdk/ARCHITECTURE.md](../NINA.Camera.CanonEdsdk/ARCHITECTURE.md).

## Layout

```
src/NINA.Camera.SonySdk/
  NINA.Camera.SonySdk.csproj    # net10.0 — NOT -windows
  SonySdkDiscovery.cs           # static EnumerateCameras()
  SonySdkCamera.cs              # ICamera impl
  SonySdkRegistry.cs            # DI helpers
```

## Notes specific to Sony

- **Cross-platform**: unlike Canon EDSDK and Nikon SDK (Windows only),
  Sony ships native libraries for Linux too. The project targets
  plain `net10.0` (no `-windows` suffix) and runs on RPi / x64 Linux
  hosts that have the Sony SDK installed.
- **Modern API**: the v2 SDK is the cleanest of the three vendors.
  `Init` → `EnumCameraObjects` → `ConnectAsync` →
  `SetDeviceProperty(ISO)` → `SendCommand(S1Shooting)`.
- **ARW raw**: written verbatim to disk via `IImageData.RawFileBytes`.
- **Registration required**: Sony's developer portal requires signup
  before download. See `docs/dslr-sony.md` when present.

## See also

- [NINA.Camera.CanonEdsdk/ARCHITECTURE.md](../NINA.Camera.CanonEdsdk/ARCHITECTURE.md)
  — full pattern explanation
- [Root ARCHITECTURE.md](../../ARCHITECTURE.md)
