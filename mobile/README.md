# N.I.N.A. Polaris -- mobile app (Capacitor)

A thin native shell (Android + iOS) around the existing Polaris web UI.
It does **not** reimplement the UI: it discovers the Polaris host on the
LAN (or takes a Relay URL), loads the live web UI in a native WebView,
and adds the few things only a device can do:

- **M1 - native GraXpert AI** via ONNX Runtime Mobile (CoreML on iOS,
  NNAPI/XNNPACK on Android) through the local `polaris-onnx` plugin.
  This bypasses the mobile-browser WebGPU/Safari-OOM limits, so the
  GraXpert `.onnx` models actually run fast on a phone/tablet.
- M0 - connect screen (mDNS discovery + manual host/Relay), keep-awake.
- (later) M2 push, M3 sensor "Aim" helper.

This folder is **completely separate** from `NINA.sln` and the .deb
build. Deleting `mobile/` changes nothing in the server/web app.

## Architecture

```
mobile/
  www/                       launcher + injected shims (the only local web assets)
    index.html               connect / discovery screen + "Devices" button
    connect.js               mDNS browse, host entry, load the Pi, switch device
    onnx-native-shim.js       injected into the Polaris UI; routes ORT -> native
  plugins/polaris-onnx/      local Capacitor plugin: native ONNX Runtime
  capacitor.config.ts        appId, allowNavigation (so the remote Pi UI keeps the bridge)
```

The shell loads `www/index.html`. After the user picks a host it
navigates the same WebView to e.g. `https://polaris-app.local:5000`.
`server.allowNavigation` keeps the Capacitor bridge (and our plugins)
available on that remote page. `onnx-native-shim.js` is injected so the
unchanged `onnx-pipelines.js` served by the Pi transparently calls the
native runtime instead of ONNX Runtime Web.

## How the GraXpert "unlock" works (M1)

The Pi's `wwwroot/js/onnx-pipelines.js` already does all the heavy
pre/post-processing (tiling, MAD/log normalization, blend masks) and
calls `ort.InferenceSession.create()` + `session.run()`. We do NOT
touch that file. Instead `onnx-native-shim.js` (injected by this app)
installs a drop-in `globalThis.ort` whose `InferenceSession`/`Tensor`
forward inference to the `PolarisOnnx` Capacitor plugin, which runs the
model with CoreML / NNAPI. Model bytes still come from the Pi's existing
`/api/onnx/*` endpoints.

## Prerequisites

- Node 18+ and npm (have: Node 24 / npm 11).
- Android: Android Studio + SDK (NDK not required for ORT prebuilts).
- iOS: **a macOS machine with Xcode** (iOS cannot be built on Windows).
  CocoaPods for the ORT pod.

## First-time setup

```bash
cd mobile
npm install
# build the local plugin
npm --prefix plugins/polaris-onnx install
npm --prefix plugins/polaris-onnx run build
# add native platforms
npx cap add android
npx cap add ios          # on macOS only
npx cap sync
```

## Run / build

> Full Android step-by-step (toolchain, debug + **signed** APK, keystore,
> troubleshooting) is in [`BUILDING-ANDROID.md`](BUILDING-ANDROID.md).


```bash
# Android (device/emulator)
npx cap run android
# or open the project to build a signed APK:
npx cap open android

# iOS (macOS only)
npx cap open ios         # build + run / Archive for TestFlight in Xcode
```

## Distribution (off-store, per project decision)

- Android: `Build > Generate Signed Bundle/APK` in Android Studio, share
  the APK. Or use Play Internal Testing.
- iOS: Archive in Xcode -> upload to **TestFlight**. Note: TestFlight
  builds expire ~90 days; re-upload to refresh. Requires a paid Apple
  Developer account.

## Status

Scaffold (M0 + M1 skeleton). The connect shell is functional; the
native ONNX plugin has working Android/iOS inference code and the JS
shim. End-to-end ONNX parity + per-model CoreML/NNAPI quirks must be
validated on physical devices (see the plan's Verification section).
