using System.Runtime.InteropServices.JavaScript;

namespace NINA.Polaris.Wasm;

/// <summary>
/// JS-callable surface for the browser-side live-stack module.
///
/// CLST-2 scaffolding — only a single <see cref="Ping"/> method right
/// now, used as a smoke test from the page boot script. CLST-3 fills
/// in the real surface (Initialize / AddFrame / GetStackedResult /
/// Reset) backed by NINA.Image.Portable's StarDetector / StarMatcher /
/// AffineTransform / ImageResampler.
///
/// The page loads the bundle, instantiates the runtime, and reaches
/// these methods through the `NINA.Polaris.Wasm.Interop.*` namespace
/// path that the WASM JS interop exposes on <c>globalThis</c>.
/// </summary>
public static partial class Interop {

    /// <summary>Returns "pong v{N}" — version-stamped so the page can
    /// log "WASM live-stack ready, vN" and so we can detect a stale
    /// cached bundle vs a fresh deploy without inspecting bytes.</summary>
    [JSExport]
    public static string Ping() {
        // Bumped manually when the [JSExport] surface changes shape.
        // CLST-3 will switch this to assembly version + git SHA.
        return "pong v0.1";
    }
}
