# polaris-llama

On-device local LLM backend for **Canopus** (the Polaris assistant). It bundles
llama.cpp's `llama-server`, runs the GGUF model on a loopback port, and lets the
**existing** in-browser Canopus agent talk to it over plain HTTP. No new agent,
no new transport: the phone just becomes the inference host.

Why this shape (proven in `canopus-eval/MOBILE.md`, Xiaomi Pad 7, SD 7+ Gen 3):

- **llama.cpp, not ORT GenAI.** Same 4-bit weights: ORT GenAI ~5 tok/s, llama.cpp
  **13.7 tok/s** while running a model 2.4x larger. Its ARM (NEON/i8mm) kernels win.
- **`--no-mmap` is mandatory.** Android reclaims the page cache behind an mmap'd
  model, so every token re-reads flash: 0.04 tok/s vs 13.7. A 343x swing on one flag.
- **Prefix cache pays the catalog once.** The ~2500-token tool catalog costs ~60s
  cold; `llama-server`'s KV reuse then re-processes ~19 tokens, so warm turns are
  **~3.3s**. The catalog is identical between turns.
- **Foreground service.** MIUI's SmartPower killed the eval harness after ~2 min.
  The model host must run in a foreground service with a persistent notification.

The model is the **4B Q4_0** (`qwen3-4b`-class). The 1.7B int4 does not tool-call
(confirmed on-device *and* on desktop, same weights) and is not a smaller option.

## Populate the native binary (build step, not committed)

The `llama-server` binary and its `.so` deps are large release artifacts, so they
are **not** checked in. Stage them into `android/src/main/jniLibs/arm64-v8a/`
before `npx cap sync android` with the helper script:

```bash
mobile/plugins/polaris-llama/scripts/fetch-llama.sh              # or fetch-llama.ps1
mobile/plugins/polaris-llama/scripts/fetch-llama.sh b<NNNN>      # pin a llama.cpp tag
```

It downloads the `llama-b<NNNN>-bin-android-arm64.zip` release (the eval validated
**b10058**), copies every `.so` into `jniLibs/arm64-v8a/`, and copies the
`llama-server` executable there renamed `libllamaserver.so`. Naming everything
`lib*.so` is what lets Android place them in `nativeLibraryDir` and execute the
server there (W^X blocks exec from `filesDir`); `pb.directory(...)` points the
process at that dir so the loader resolves the sibling libs.

> Wire the script as a prebuild step in the Android CI job (mirroring the
> ncnn/data-pack packaging) so releases stage the binary automatically.

## Register the plugin

Add it next to `polaris-onnx` in the generated host project:

`android/app/src/main/assets/capacitor.plugins.json`
```json
{ "pkg": "polaris-llama", "classpath": "dev.danwbr.polaris.llama.PolarisLlamaPlugin" }
```
and as a dependency of the app module (Capacitor picks local plugins up from
`package.json`; run `npx cap sync android`).

## Model delivery

The GGUF (~2.4-3.9 GB) is **downloaded on first activation**, not bundled. Point
`downloadModel({ url })` at a release asset or at the connected Polaris host. The
plugin streams to `filesDir/canopus/model.gguf` with resume + progress events, so
the APK stays store-sized.

## JS usage (wired from the Canopus host UI)

```ts
import { PolarisLlama } from 'polaris-llama';

await PolarisLlama.addListener('downloadProgress', p => showBar(p.percent));
await PolarisLlama.downloadModel({ url: MODEL_URL, expectedBytes: MODEL_BYTES });
const { url } = await PolarisLlama.start();      // e.g. http://127.0.0.1:8823/v1
// hand `url` to the Canopus "on this device" provider (provider-local.js) and go.
```

The Canopus client fetches `http://127.0.0.1:8823` from the (remote) Polaris page:
mixed content is already allowed (`capacitor.config.ts`), and `llama-server` sends
permissive CORS, so the loopback fetch works.

## Device test

```powershell
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
cd mobile
npx cap sync android
npx cap run android            # a real arm64 phone/tablet, USB debugging on
adb logcat -s CANOPUS-LLAMA    # watch the server come up + per-turn timing
```

Expect: model downloads once, server ready in a few seconds (weights resident),
first assistant turn ~60s (catalog ingest), warm turns ~3.3s.

## iOS

Deferred. iOS forbids spawning a subprocess, so `llama-server` cannot run; iOS
needs llama.cpp **embedded in-process** (an xcframework) with a small bridge that
exposes the same `start/stop/status/downloadModel` surface. The web/stub in
`web.ts` throws `unavailable` until then.
