# Sony α-series on Windows + Linux (Skeleton, Open Work)

> **Status:** the Sony driver in this build is a skeleton. The
> Camera card recognises the driver, lists it as *(not installed)*,
> and shows this page as the install banner, but the actual
> capture path is a stub. Contributions welcome.

## Why Sony is the most attractive vendor driver to finish

Unlike Canon EDSDK and the Nikon stack (Windows-only), Sony's
**Camera Remote SDK** (SCRSDK) v2.x ships native binaries for
both **Windows** and **Linux**, including ARM64. That makes it
the only vendor SDK we can ship to Raspberry Pi users running
Polaris headless, Canon and Nikon DSLR users on Linux need to
go through the INDI gphoto path instead.

## What's already in place

- `src/NINA.Camera.SonySdk/` project skeleton with the right
  cross-project references. **No `<SupportedOSPlatform>` attribute**
  (intentionally) so the assembly compiles on Linux too, the
  actual runtime SDK probe handles the per-host decision.
- `SonySdkCamera : ICamera` with the right shape: ISO options
  (50..102400), `Capabilities.Dslr`, `Gain` aliased to ISO so the
  status broadcast renders the right field.
- `SonySdkDiscovery.Enumerate()` and `SonySdkRegistry.IsAvailable`
  stubs returning empty/false so the Equipment UI surfaces the
  install banner without crashing.
- `EquipmentManager.SelectCamera` and the camera-drivers endpoint
  include the `sony-sdk` entry; once the binding lands here the
  driver becomes selectable end-to-end with no other wiring
  changes.

## What's needed to make it real

Sony's tethering stack splits in two, which one fits depends
on the cameras you want to support:

### Option A, Camera Remote API v1.90 (Wi-Fi REST, older bodies)

Cameras released ~2013-2017 expose a **JSON-RPC over HTTP**
interface over Wi-Fi (Sony "Smart Remote Control" mode). No
native binaries, no Zadig USB-driver dance, no platform
restrictions, works the same on Windows, Linux, Raspberry Pi
and even tablets. Easiest path to a working Sony driver.

Bodies covered (this list is not exhaustive, any body with
the "Smart Remote Control" PlayMemories app works):

- α6000 / α6300 / α6500
- α7 / α7R / α7S (original)
- α7 II / α7R II / α7S II
- NEX-5R / NEX-5T / NEX-6
- DSC-QX10 / QX100 lens cameras
- HX series with Wi-Fi

**Recommended reference:** the MS-PL-licensed
[nantcom/SonyCameraSDK](https://github.com/nantcom/SonyCameraSDK)
project is a portable C# client for this exact API. Adapt its
HTTP-call patterns into `SonySdkCamera`, discovery via SSDP on
port 1900, then JSON-RPC POSTs against
`http://{cam-ip}:8080/sony/camera`. Methods we care about:
`startRecMode` / `actTakePicture` / `getEvent` / `setIsoSpeedRate`
/ `setShutterSpeed` / `setExposureMode`.

This path doesn't need any redistribution-restricted DLLs, the
camera has the API built in. The Polaris-side wrapper can live
entirely under our MPL 2.0 licence.

### Option B, Camera Remote SDK v2.x (USB tether, modern bodies)

Cameras released 2018-onward dropped the Wi-Fi REST API in
favour of Sony's **Camera Remote SDK** (SCRSDK), a C-style
native library over USB tether. Coverage:

- α7 III / α7 IV
- α7R III / α7R IV / α7R V
- α7S III
- α9 II / α1
- α7C / α7C II / α7C R
- ZV-E1 / ZV-E10 II
- FX3 / FX30
- α6700

Path:

1. Register on
   <https://developer.sony.com/imaging-products/camera-remote-sdk/>
   (free, accept the SDK licence).
2. Download SCRSDK v2.x for the platforms you care about
   (Windows x64, Linux x64, Linux ARM64 for Raspberry Pi).
3. Implement the native bindings under
   `src/NINA.Camera.SonySdk/Native/`. The SDK exposes a C-style
   API (`SCRSDK::Init`, `EnumCameraObjects`, `Connect`,
   `SetDeviceProperty`, `SendCommand(S1Shooting)`, etc.)
   that's easier to P/Invoke than Canon EDSDK and Nikon Imaging SDK.
4. Wire `SonySdkDiscovery.Enumerate()` against `EnumCameraObjects`.
5. Wire `SonySdkCamera.ConnectAsync` / `CaptureAsync` against
   the SDK's connect + shutter + transfer flow.

SCRSDK DLLs are not redistributable, same arrangement as Canon
and Nikon (users register, accept the EULA, drop the libraries
into `plugins/sony-sdk/`).

### Picking which one to implement first

Option A (Camera Remote API) is the easier win: pure HTTP, no
native deps, no platform restrictions, no EULA dance, covers
the cameras a hobbyist astrophotographer is most likely to have
on hand. Option B is needed for current bodies. Both can live
in the same `NINA.Camera.SonySdk` project with internal branching
by camera generation.

## Capture-path expectations

Match the Canon driver's shape so the rest of Polaris doesn't
need to change:

- Capture in RAW + JPEG mode on the camera (Sony calls it RAW +
  JPEG, ARW + JPEG, or RAW + Small JPEG).
- Pull both assets on each shutter trigger via the SDK's content
  transfer callback.
- Attach the ARW bytes to the returned `IImageData` via
  `IHasRawFile.RawFileBytes` + `.arw` extension.
- Decode the JPEG to a Rec.601 luminance `ushort[]` for the live
  preview (SkiaSharp, same pattern as `CanonEdsdkCamera`).
- Map the requested exposure to the closest shutter-speed enum
  on the body, or fall back to Bulb (Sony exposes Bulb as a
  shutter-speed enum value).

## Once the binding works

In Polaris **Equipment** → Camera card, pick the **Sony (Camera
Remote SDK)** entry → **Detect** → pick the body → **Connect**.
Captures land in
`{rig}/lights/{target}/{filter}/{session}/IMG_*.arw` exactly
like Canon ones do as CR2.

## Tips for tethered Sony sessions

- USB-PD power: Sony α bodies accept USB Power Delivery, a
  PD-capable battery pack or wall adapter (≥ 9V output) powers
  the body during long sessions. The internal NP-FZ100 stays
  charged at the same time.
- "Connect" mode: set USB Connection on the camera body to **PC
  Remote**, not Mass Storage or MTP. Without this the SDK won't
  see the camera.
- Tethering apps that fight Polaris: close Sony Imaging Edge
  Desktop / Remote before connecting, same single-session-
  at-a-time constraint as Canon and Nikon.
- Body firmware: newer α bodies often need firmware updates to
  match newer SCRSDK versions. The SCRSDK release notes list the
  minimum firmware per body.

## EULA reminder

Sony Camera Remote SDK binaries are not redistributable. Same
arrangement as Canon and Nikon: users register, accept the EULA,
download the SDK, drop the libraries into `plugins/sony-sdk/`.
The Polaris-side wrappers in this repo are MPL 2.0 and ship the
P/Invoke surface only, no Sony code.
