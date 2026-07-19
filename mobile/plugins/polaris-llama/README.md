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

The `llama-server` binary and its `.so` deps are large, so they are **not** checked
in; they get staged into `android/src/main/jniLibs/arm64-v8a/` before
`npx cap sync android`.

> **Heads-up:** llama.cpp's GitHub releases do **not** publish a prebuilt
> `bin-android-arm64` asset, so `scripts/fetch-llama.{sh,ps1}` (which targets that
> URL) 404s against upstream today. The Android CI runs it **best-effort**: if it
> can't stage the binary, the APK still builds, just without the on-device backend
> (a `::warning::` is logged). To actually ship it you must **build llama-server
> with the Android NDK** and place the outputs here, or point the script at a
> mirror that hosts the binary.

Expected layout once staged (executable renamed so Android keeps + can exec it from
`nativeLibraryDir`, since W^X blocks exec from `filesDir`; `pb.directory(...)`
points the process at that dir so the loader resolves the sibling libs):

```
android/src/main/jniLibs/arm64-v8a/
  libllamaserver.so   <- the llama-server executable, renamed
  libllama.so  libggml*.so  ...
```

NDK build sketch (unverified; iterate locally before trusting CI):

```bash
git clone --depth 1 -b b<NNNN> https://github.com/ggml-org/llama.cpp
cmake -S llama.cpp -B build-android \
  -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-24 \
  -DLLAMA_CURL=OFF -DBUILD_SHARED_LIBS=ON
cmake --build build-android --target llama-server -j
# then copy build-android/bin/llama-server (-> libllamaserver.so) + *.so into jniLibs
```

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

iOS forbids spawning a subprocess, so `llama-server` cannot run as a child
process. Instead the model host runs **in-process**: a small C bridge
(`ios/Plugin/PolarisLlamaBridge.h` -> `polaris_llama_start/stop/is_running`)
starts llama.cpp's OpenAI-compatible server on `127.0.0.1` on a background
thread. Because that is a real loopback HTTP server, the Canopus client's
provider (`provider-local.js`, HTTP to localhost) drives it **unchanged, exactly
like Android** -- no JS branch, the host UI's mobile flow (download/start) works
as-is.

State:

- **Done (pure Foundation, compiles):** `downloadModel` (URLSession + progress),
  `deleteModel`, `status`, and the Swift `start/stop` wiring.
- **Pending (native artifact):** the llama.cpp **xcframework** that implements the
  three bridge symbols. Until it is vendored, `start/stop` do not link.

Build + vendor the framework:

1. Build llama.cpp for iOS (arm64 device + arm64 simulator) with `server.cpp`
   and libllama, exposing the `polaris_llama_*` C entry points from the bridge
   header (a thin wrapper that runs the server loop on a thread, with the same
   flags as Android: weights resident, jinja template, half the cores, ctx 8192,
   KV prefix reuse).
2. Package the slices into `llama.xcframework`, drop it in
   `ios/Frameworks/`, and uncomment `vendored_frameworks` in `PolarisLlama.podspec`.
3. **ATS**: the WebView page (remote host, https) fetches `http://127.0.0.1`, so
   add an App Transport Security exception for local networking in the app's
   `Info.plist` (`NSAllowsLocalNetworking`), alongside the existing LAN-cert
   exceptions.
4. `npx cap sync ios`, build, test on a real device.
