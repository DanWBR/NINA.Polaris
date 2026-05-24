/*
 * sky-bridge.js — postMessage RPC between the Polaris main app and
 * the stellarium-web-engine sub-application.
 *
 * SWE-1 scope (this commit): WebGL detection + initial { type: "ready" }
 * handshake. No engine wired yet — SWE-2 plugs in StelWebEngine(),
 * SWE-3 wires data sources, SWE-4 handles observer/look-at/search RPC,
 * SWE-5 adds FOV-overlay + drag-to-frame messages.
 *
 * Message protocol (parent → iframe):
 *   { type: "set-observer", lat, lng }       (degrees)
 *   { type: "set-time", utc }                (epoch ms)
 *   { type: "look-at", raDeg, decDeg, fovDeg? }
 *   { type: "search", query }
 *   { type: "get-center" }                   (replies "center")
 *   { type: "set-fov-overlays", mount, target }
 *   { type: "set-drag-mode", mode }          ("free" | "fixed-target")
 *
 * Message protocol (iframe → parent):
 *   { type: "ready", version, webgl }
 *   { type: "search-result", query, result }
 *   { type: "center", raDeg, decDeg, fovDeg }
 *   { type: "map-click", raDeg, decDeg, objectName }
 *   { type: "webgl-unavailable" }
 *
 * The parent (Polaris app.js) installs a `message` listener on window
 * and forwards relevant calls; it also keeps the iframe handle so it
 * can postMessage targeted at this sub-app.
 *
 * This file is loaded by /sky/index.html and runs in the iframe's
 * own document. It only touches APIs available on the engine; nothing
 * here imports or depends on the main app's code.
 */
(function () {
    'use strict';

    var BRIDGE_VERSION = '0.1.0-swe1';

    // -----------------------------------------------------------------
    // WebGL detection.
    //
    // The engine ships in SWE-2; until then we just probe the canvas
    // ahead of time and warn the parent early if the browser has no
    // WebGL2. Probing both webgl2 (preferred — engine targets it) and
    // webgl (so we can later print a clearer "WebGL1 but engine needs 2"
    // message instead of a generic failure).
    // -----------------------------------------------------------------
    function detectWebGL() {
        var canvas = document.getElementById('stel-canvas');
        if (!canvas) return { webgl: false, webgl2: false };
        var gl2 = null, gl1 = null;
        try { gl2 = canvas.getContext('webgl2'); } catch (e) { /* swallow */ }
        try { gl1 = canvas.getContext('webgl') || canvas.getContext('experimental-webgl'); }
        catch (e) { /* swallow */ }
        // Release the contexts so SWE-2's StelWebEngine() can grab the
        // canvas without contention. WebGL contexts can't be reattached
        // after the first getContext call, so we don't free anything —
        // we just lose the references. (The engine will call getContext
        // itself when it initialises and either get the same context or
        // a no-op extension.)
        return { webgl: !!gl1 || !!gl2, webgl2: !!gl2 };
    }

    function setStatus(text) {
        var el = document.getElementById('sky-status');
        if (!el) return;
        if (text === null) {
            el.style.display = 'none';
        } else {
            el.style.display = 'flex';
            var msg = el.querySelector('.msg');
            if (msg) msg.textContent = text;
        }
    }

    function postToParent(msg) {
        // window.parent is the Polaris main app. Same-origin (iframe
        // sandbox allows it), so we don't need to lock down targetOrigin
        // for security here — but use '*' so any embedding works (admin
        // tools, future relay deployments).
        try {
            window.parent.postMessage(msg, '*');
        } catch (e) {
            console.warn('[Sky] postMessage to parent failed', e);
        }
    }

    // -----------------------------------------------------------------
    // Incoming message router. SWE-1 only logs unknown messages so we
    // can see in DevTools that the parent is reaching us; real handlers
    // ship in SWE-4 / SWE-5.
    // -----------------------------------------------------------------
    window.addEventListener('message', function (ev) {
        var msg = ev.data;
        if (!msg || typeof msg !== 'object' || !msg.type) return;
        // Ignore messages we accidentally bounced to ourselves.
        if (msg.__from === 'sky-bridge') return;
        console.log('[Sky] received from parent:', msg.type, msg);
    });

    // -----------------------------------------------------------------
    // Init: detect WebGL, announce ready (or unavailable) to parent,
    // and update the on-screen status. Wrap in a load handler so the
    // canvas is definitely in the DOM.
    // -----------------------------------------------------------------
    window.addEventListener('load', function () {
        var caps = detectWebGL();
        if (!caps.webgl2) {
            setStatus(
                caps.webgl
                    ? 'This browser has WebGL but no WebGL2 — the sky engine needs WebGL2. Try a recent Chrome / Firefox / Safari.'
                    : 'WebGL is not available — open Polaris from a desktop browser.');
            postToParent({ type: 'webgl-unavailable', webgl1: caps.webgl, __from: 'sky-bridge' });
            return;
        }

        // SWE-1: engine not loaded yet, just say we're alive so the
        // parent can verify the bridge round-trip in DevTools. SWE-2
        // will move this announcement into the engine's onReady
        // callback and include the real version string.
        setStatus('Bridge ready · engine loads in SWE-2');
        postToParent({
            type: 'ready',
            version: BRIDGE_VERSION,
            webgl: true,
            webgl2: true,
            engineLoaded: false,
            __from: 'sky-bridge'
        });
        console.log('[Sky] bridge ready v' + BRIDGE_VERSION + ' webgl2=true');
    });
})();
