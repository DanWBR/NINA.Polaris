# Client-side compute (WASM offload)

By default Polaris's server does all the heavy image math,
StarDetector, alignment, accumulator, the whole live-stack pipeline.
That works fine on a Pi 4 or 5, but on a Pi 2 or 3 the server-side
math saturates all four cores and leaves no headroom for capture +
INDI + writes.

**Client-side compute** flips that: the math runs in your browser
via a WebAssembly module, the Pi just orchestrates equipment + relays
raw frames. Most modern laptops/desktops have ~10-50× the CPU of a
Pi sitting idle anyway.

## When to use it

| Server hardware       | Recommended mode |
|-----------------------|------------------|
| **Raspberry Pi 2 / 3**| 🌐 Client (essential, Pi can't keep up otherwise) |
| **Raspberry Pi 4**    | Auto (Pi 4 handles either; client gives more headroom) |
| **Raspberry Pi 5**    | Auto (either fine; auto picks client when browser supports it) |
| **Intel mini-PC**     | Auto |
| **Mobile phone in browser** | 🖥 Server (phones don't have spare CPU either) |

## How it works

```
┌──────────────────────────────────────────────┐
│  Pi (server)                                  │
│    [Camera] → capture                         │
│         → ImageWriter (FITS to disk)          │
│         → LiveStackingService (MetricsOnly)   │
│             ├─ runs StarDetector              │
│             │  (for trigger HFR + star count) │
│             └─ skips StarMatcher/Resampler/   │
│                accumulator                    │
│         → ImageRelayService                   │
│             → /ws/image-stream raw uint16     │
└──────────────────────────────────────────────┘
                   ↓ ↑
                WebSocket
                   ↓ ↑
┌──────────────────────────────────────────────┐
│  Browser (client)                             │
│    [WASM: NINA.Polaris.Wasm]                  │
│      Initialize → AddFrame(pixels) →          │
│        StarDetector + StarMatcher +           │
│        AffineTransform + ImageResampler +     │
│        running-mean accumulator               │
│      ↓                                        │
│    WebGL2 stretch + debayer → canvas          │
│                                               │
│    {type:'client-stack-progress'}             │
│      → server's trigger orchestrator          │
└──────────────────────────────────────────────┘
```

The server still runs **StarDetector** in MetricsOnly mode because
auto-AF + auto-recenter triggers ([live-stacking.md](live-stacking.md))
need HFR + star count to fire. Everything else, alignment, warping,
accumulation, the displayable preview, happens in your browser.

## Toggling it

LIVE tab → toolbar → **Compute** dropdown:

- **Auto** (default), server flips to MetricsOnly when a browser
  with the WASM module connected, back to Full when the last one
  disconnects. The right setting for most users.
- **🖥 Server**, force server-side accumulator regardless of
  clients. Use when multiple browsers should see the same canonical
  stack, or when the client is slow.
- **🌐 Client (WASM)**, force MetricsOnly. The next browser that
  arrives stacks from frame 1 on its side. Useful for testing the
  WASM path, or to keep the Pi free even when no browser is open
  yet.

Saved per rig, a Pi 2 setup defaults to "client" once you change it
there, while a Pi 5 + mini-PC setup can stay on "auto".

## The status chip

The live-stack chip in the activity bar shows the mode:

- **🌐 Live stack 12f**, browser is doing the math
- **🖥 Live stack 12f**, server is doing the math

If you see the icon flip mid-session, the handshake reacted to a
browser opening / closing or a Compute setting change.

## Saving a client-stacked result

When the browser owns the accumulator (server in MetricsOnly), the
stacked buffer never reaches the Pi disk through the regular capture
path. Click **💾 Save current stack** in the LIVE tab toolbar to
upload the current accumulator → server writes it as a FITS into
`{rig}/integrated/{target}/{filter}/master_*.fits`. From there it
shows up in STUDIO like any other master.

Server-side stacking (Full mode) already saves automatically; the
button only appears when MetricsOnly is active.

## Bundle size

The WASM module is ~12 MB on disk (~3-4 MB gzipped on the wire,
~2.5 MB Brotli). One-time download per browser session; cached
afterwards. First load: 2-3 s on a modern laptop, 6-10 s on a mid-
range phone.

## Browser requirements

- **WASM**, supported in every browser since 2017
- **WASM SIMD**, Chromium 91+, Firefox 90+, Safari 16.4+. Without
  SIMD the math runs 2-3× slower but still works.
- **WebGL2**, used for the canvas render (orthogonal to WASM,
  needed in both server and client modes). Modern desktop browsers
  all support it; some older mobile browsers fall back to JPEG.

If WASM fails to load (network glitch, browser blocked it), Polaris
silently falls back to server-side mode for that session, you'll
see 🖥 in the chip instead of 🌐 and the server takes over.

## When the math doesn't match

The client-side WASM and the server-side .NET both run the **same
binaries** (`NINA.Image.Portable` referenced by both projects) so
the output is byte-identical on the same inputs. If you ever see
diverging results between modes:

- Compare frame-by-frame metrics in the DevTools console (the
  client-stack-progress messages log HFR + star count) against the
  server's `/ws/status` `liveStack.lastFrameHfr` / `lastFrameStarCount`
- Confirm the WASM bundle version is in sync, `Interop.Ping()`
  returns a version string the page logs at boot
- A stale browser cache is the most common explanation; Ctrl+F5

## See also

- [live-stacking.md](live-stacking.md), the live-stack feature itself
- [installation.md](installation.md), Pi 2 vs Pi 4/5 hardware notes
- Plan file (root), CLST-1..8 implementation history
