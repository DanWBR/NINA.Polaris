/*
 * onnx-native-shim.js  (M1 - the GraXpert unlock)
 *
 * Injected into the LIVE Polaris UI at document-start by the native
 * layer (see plugins/polaris-onnx). It installs a drop-in `globalThis.ort`
 * that forwards inference to the `PolarisOnnx` Capacitor plugin (ONNX
 * Runtime Mobile: CoreML on iOS, NNAPI/XNNPACK on Android).
 *
 * The Pi's wwwroot/js/onnx-pipelines.js is NOT modified: it keeps doing
 * all the tiling / normalization and calls the same small ORT surface
 * (`ort.Tensor`, `ort.InferenceSession.create`, `session.run`). Those
 * calls now run natively, dodging the mobile WebGPU / Safari-OOM limits.
 *
 * It only activates when the native plugin is present; in a normal
 * browser it is a no-op and the page's own ORT Web loads as usual.
 */
(function () {
  'use strict';
  var cap = window.Capacitor;
  var Native = cap && cap.Plugins && cap.Plugins.PolarisOnnx;
  if (!Native) { return; } // not in the native app -> leave ORT Web alone
  if (window.__polarisNativeOnnx) { return; } // idempotent

  // ---- base64 <-> bytes (the bridge marshals binary as base64) ----
  function bytesToB64(u8) {
    var CHUNK = 0x8000, parts = [];
    for (var i = 0; i < u8.length; i += CHUNK) {
      parts.push(String.fromCharCode.apply(null, u8.subarray(i, i + CHUNK)));
    }
    return btoa(parts.join(''));
  }
  function b64ToBytes(b64) {
    var bin = atob(b64), u8 = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
    return u8;
  }
  function tensorBytes(data) {
    if (data instanceof Uint8Array) return data;
    return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  }
  // Map an ORT element type string to the JS typed array used for output.
  function typedFromBytes(type, u8) {
    var b = u8.buffer, o = u8.byteOffset, n = u8.byteLength;
    switch (type) {
      case 'float32': return new Float32Array(b, o, n / 4);
      case 'float16': return new Uint16Array(b, o, n / 2); // raw half bits
      case 'int32':   return new Int32Array(b, o, n / 4);
      case 'int64':   return new BigInt64Array(b, o, n / 8);
      case 'uint8':   return new Uint8Array(b, o, n);
      case 'bool':    return new Uint8Array(b, o, n);
      default:        return new Float32Array(b, o, n / 4);
    }
  }

  function Tensor(type, data, dims) {
    // Mirrors ort.Tensor's shape used by onnx-pipelines.js.
    this.type = type;
    this.data = data;
    this.dims = dims || [];
  }

  function InferenceSession() {
    this._handle = null;
    this.inputNames = [];
    this.outputNames = [];
  }
  function sessionFromInfo(info) {
    var s = new InferenceSession();
    s._handle = info.handle;
    s.inputNames = info.inputNames || [];
    s.outputNames = info.outputNames || [];
    return s;
  }

  function fmtMB(b) { return Math.round(b / (1024 * 1024)) + ' MB'; }

  // Refuse up front when the model clearly won't fit, instead of letting
  // the device OOM-crash. ORT (XNNPACK) needs roughly one extra copy of
  // the weights while building the session, so budget ~1.6x the model
  // against available RAM. Best-effort: if deviceMemory isn't available
  // (iOS / older plugin) we skip the check.
  async function assertEnoughMemory(modelBytes) {
    var mem = null;
    try { mem = await Native.deviceMemory(); } catch (e) { return; }
    if (!mem || !mem.availBytes) return;
    var needed = modelBytes * 1.6;
    if (needed > mem.availBytes) {
      throw new Error(
        'Not enough free memory to run this AI model on the device. ' +
        'Model needs about ' + fmtMB(needed) + ' but only ' +
        fmtMB(mem.availBytes) + ' is free' +
        (mem.lowMemory ? ' (device is low on memory)' : '') +
        '. Close other apps, or use a smaller model (e.g. denoise 2.0.0).');
    }
  }

  // Big models (BGE ~200 MB, denoise ~450 MB) must NOT cross the bridge
  // as one base64 string: building it holds the model ~3-4x in the
  // WebView renderer (Uint8Array + joined string + btoa output), which
  // OOM-kills the renderer. Stream to a file in chunks instead; native
  // creates the session from the file (mmap). Small models keep the
  // one-shot base64 path.
  var FILE_THRESHOLD = 8 * 1024 * 1024;   // 8 MB
  var CHUNK = 4 * 1024 * 1024;            // 4 MB raw per appendModel call

  InferenceSession.create = async function (model, options) {
    var u8 = model instanceof Uint8Array ? model
           : (model instanceof ArrayBuffer ? new Uint8Array(model) : null);
    if (!u8) throw new Error('PolarisOnnx shim: model must be Uint8Array/ArrayBuffer');
    var eps = (options && options.executionProviders) || [];

    await assertEnoughMemory(u8.length);

    if (u8.length > FILE_THRESHOLD) {
      try {
        var id = 'm' + Date.now().toString(36) + '_' +
                 Math.floor(Math.random() * 1e9).toString(36);
        await Native.beginModel({ id: id });
        for (var off = 0; off < u8.length; off += CHUNK) {
          var slice = u8.subarray(off, Math.min(off + CHUNK, u8.length));
          await Native.appendModel({ id: id, chunk: bytesToB64(slice) });
        }
        return sessionFromInfo(await Native.createSessionFromFile(
          { id: id, executionProviders: eps }));
      } catch (e) {
        // assertEnoughMemory throws a friendly message we want to surface
        // verbatim; only fall back to base64 for "method not found" style
        // failures (older plugin / iOS not yet ported).
        if (/memory/i.test(e && e.message || '')) throw e;
        console.warn('[polaris] chunked model load unavailable, falling back:', e && e.message);
      }
    }

    return sessionFromInfo(await Native.createSession({
      model: bytesToB64(u8),
      executionProviders: eps
    }));
  };
  InferenceSession.prototype.run = async function (feeds) {
    var packed = {};
    for (var name in feeds) {
      if (!Object.prototype.hasOwnProperty.call(feeds, name)) continue;
      var t = feeds[name];
      packed[name] = { data: bytesToB64(tensorBytes(t.data)), type: t.type, dims: t.dims };
    }
    var res = await Native.run({ handle: this._handle, feeds: packed });
    var out = {};
    var outs = res.outputs || {};
    for (var oname in outs) {
      if (!Object.prototype.hasOwnProperty.call(outs, oname)) continue;
      var o = outs[oname];
      out[oname] = new Tensor(o.type, typedFromBytes(o.type, b64ToBytes(o.data)), o.dims);
    }
    return out;
  };
  InferenceSession.prototype.release = async function () {
    try { await Native.releaseSession({ handle: this._handle }); } catch (e) {}
  };

  // Install the drop-in ORT. `env` is a no-op shell so any
  // ort.env.wasm.* tweaks the pipelines make don't throw.
  window.ort = {
    Tensor: Tensor,
    InferenceSession: InferenceSession,
    env: { wasm: {}, webgpu: {}, logLevel: 'warning' }
  };
  window.__polarisNativeOnnx = true;

  // Some builds guard `loadOrtWeb()` with `if (window.ort) return;`; this
  // satisfies that and skips the heavy ORT Web download entirely.
  console.log('[polaris] native ONNX shim active (GraXpert runs on device GPU/NPU)');
})();
