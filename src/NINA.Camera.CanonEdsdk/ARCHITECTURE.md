# NINA.Camera.CanonEdsdk — Architecture

Windows-only wrapper around the Canon **EDSDK** (EOS Digital SDK).
Provides an `ICamera` implementation for any EOS DSLR / mirrorless
that Canon supports in EDSDK 13.x / 14.x.

Sister projects `NINA.Camera.NikonSdk` and `NINA.Camera.SonySdk`
follow the same shape — read this once and the others read identically.

## Layout

```
src/NINA.Camera.CanonEdsdk/
  NINA.Camera.CanonEdsdk.csproj    # net10.0-windows, P/Invoke wrapper
  Native/                          # P/Invoke surface
    EdsdkNative.cs                 # DllImport entry points + structs
    EdsdkConstants.cs              # kEdsPropID_*, kEdsCameraCommand_*
  CanonEdsdkDiscovery.cs           # static EnumerateCameras()
  CanonEdsdkCamera.cs              # ICamera impl
  CanonEdsdkRegistry.cs            # DI registration helpers
```

## Why a separate project

EDSDK is **not redistributable**. Canon's EULA prohibits us from
shipping `EDSDK.dll` + `EDSDKLib.dll` with Polaris. The user
downloads the SDK from Canon's developer site and drops the native
DLLs into `plugins/canon-edsdk/` next to the Polaris binary at
runtime.

Keeping the wrapper in its own project means:

- The native dependency is **soft** — `NINA.Polaris` references this
  project, but the constructor of `CanonEdsdkCamera` is the first
  thing that actually touches a `DllImport`. If the DLLs aren't
  present, the user just doesn't see Canon in the driver dropdown.
- The wrapper itself is MPL 2.0 (matching the rest of Polaris).
  Canon's license applies only to the DLLs the user downloads.

## P/Invoke surface

`Native/EdsdkNative.cs` declares the `DllImport`s. EDSDK exports
~80 functions; we wrap the ~25 we actually use:

- Lifecycle: `EdsInitializeSDK`, `EdsTerminateSDK`
- Discovery: `EdsGetCameraList`, `EdsGetChildCount`, `EdsGetChildAtIndex`,
  `EdsGetDeviceInfo`
- Connect: `EdsOpenSession`, `EdsCloseSession`
- Properties: `EdsSetPropertyData`, `EdsGetPropertyData`
- Commands: `EdsSendCommand` (TakePicture, BulbStart/End, ...)
- Events: callbacks for object events (image-ready), property events,
  state events
- Transfer: `EdsCreateMemoryStream`, `EdsDownload`, `EdsDownloadComplete`

`EdsdkConstants.cs` holds the magic numbers — property IDs (ISO,
shutter speed, ...), command IDs, save-to flags, error codes.

## `CanonEdsdkCamera` (the ICamera impl)

Implements `NINA.Image.Portable.Interfaces.ICamera`:

- `ConnectAsync` → `EdsOpenSession` + register callbacks
- `CaptureAsync(seconds)`:
  1. Map `seconds` → nearest shutter-speed enum, OR use `BulbStart` /
     `BulbEnd` for > 30s
  2. Set ISO via `EdsSetPropertyData(kEdsPropID_ISOSpeed, ...)`
  3. `EdsSetPropertyData(kEdsPropID_SaveTo, kEdsSaveTo_Host)` +
     `EdsSetCapacity` (the trick that forces the camera to stream the
     image to the host instead of writing to the SD card)
  4. `EdsSendCommand(kEdsCameraCommand_TakePicture)`
  5. Wait for `kEdsObjectEvent_DirItemRequestTransfer` callback
  6. `EdsCreateMemoryStream` + `EdsDownload` + `EdsDownloadComplete`
     → byte buffer with CR2 + embedded JPEG
  7. Decode the JPEG via SkiaSharp into `ushort[]` grayscale (matching
     the rest of Polaris's monochrome pipeline)
  8. Build a `BaseImageData` with `RawFileBytes = cr2Bytes` so
     `ImageWriterService` writes the `.cr2` to disk verbatim
- `SetIsoAsync`, `SetTemperatureAsync` (no-op), `SetCoolerAsync` (no-op)
- `Capabilities` — DSLRs don't have cooler or binning, so
  `SupportsCooler = false`, `SupportsBinning = false`,
  `SupportsIso = true`, `SupportsBulb = true`

## How NINA.Polaris integrates

1. At startup, `EquipmentManager` checks if the platform is Windows +
   if `EDSDK.dll` is reachable. If yes, registers
   `CanonEdsdkDiscovery` as an available driver source.
2. When the user picks "Canon" in the Camera card driver dropdown +
   clicks "Detect", `CameraDriversEndpoints` calls
   `CanonEdsdkDiscovery.EnumerateCameras()` and returns
   `[{ id, model, serialNumber }]`.
3. User picks one + Connect → `EquipmentManager.SelectCamera("canon-
   edsdk", deviceId)` instantiates `CanonEdsdkCamera` and exposes it
   via `EquipmentManager.Camera` (the `ICamera?` typed property the
   rest of the app talks to).
4. From there, every capture path (manual snap, sequence, live stack)
   works identically to an INDI camera — the polymorphism is
   transparent above `ICamera`.

## Sister projects

- **`NINA.Camera.NikonSdk`** — Nikon Imaging SDK (Z-series mirrorless)
  + optional Nikon MAID SDK (classic DSLRs). Same pattern.
- **`NINA.Camera.SonySdk`** — Sony Camera Remote SDK 2.x. Notable
  difference: Sony ships Linux binaries too, so this project's
  target framework is `net10.0` (not `-windows`) and works on Linux
  hosts that have the Sony SDK installed.

All three share the contract of `ICamera` + the convention of writing
the vendor RAW file verbatim to disk.

## See also

- [Root ARCHITECTURE.md](../../ARCHITECTURE.md)
- `src/NINA.Image.Portable/Interfaces/ICamera.cs` — the contract
- [docs/dslr-windows-canon.md](../../docs/dslr-windows-canon.md) (if
  present) — end-user install procedure for the EDSDK DLLs
