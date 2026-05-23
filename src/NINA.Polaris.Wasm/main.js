// main.js — WASM bootstrap for NINA.Polaris.Wasm.
//
// wasm-tools emits this file alongside the .wasm bundle when
// WasmMainJSPath is set in the csproj. The page loads it via
// <script type="module"> (see livestack-client.js + index.html).
//
// We don't ship a UI here — this is the bare-minimum runtime boot.
// The page calls into [JSExport] methods via the returned exports
// object after the runtime is ready.

import { dotnet } from './_framework/dotnet.js';

const { setModuleImports, getAssemblyExports, getConfig } =
    await dotnet.create();

const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);

// Hang the exports off a global namespace so page JS can find them
// from anywhere without import gymnastics.
//   await globalThis.NINA.Polaris.Wasm.Interop.Ping()  →  "pong vN"
globalThis.NINA = globalThis.NINA || {};
globalThis.NINA.Polaris = globalThis.NINA.Polaris || {};
globalThis.NINA.Polaris.Wasm = exports.NINA.Polaris.Wasm;

// Flag the page can poll. Avoids racy "is it loaded yet" checks.
globalThis.NINA.Polaris.WasmReady = true;

// Fire a DOM event the page (Alpine app) can listen for via @addEventListener.
window.dispatchEvent(new CustomEvent('nina-wasm-ready', {
    detail: { version: exports.NINA.Polaris.Wasm.Interop.Ping() }
}));

console.log('[NINA.Polaris.Wasm] runtime ready —',
    exports.NINA.Polaris.Wasm.Interop.Ping());
