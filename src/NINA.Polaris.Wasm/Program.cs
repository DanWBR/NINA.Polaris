// Entry point. browser-wasm requires OutputType=Exe + a Main, but in
// the browser the "main" never runs to completion, the WASM runtime
// stays alive waiting for JS to call into the [JSExport] surface
// defined in Interop.cs.
//
// We keep Main empty + use the explicit Main signature (instead of
// top-level statements) so the linker has a stable entry-point symbol.

namespace NINA.Polaris.Wasm;

public class Program {
    public static void Main() {
        // Intentionally empty.
    }
}
