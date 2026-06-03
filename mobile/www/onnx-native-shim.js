/*
 * onnx-native-shim.js  (M1 - the GraXpert unlock)
 *
 * Injected at document-start into EVERY frame of the native app. It has
 * two roles depending on which frame it runs in:
 *
 *  - PARENT frame (the app's own connect screen, app origin): native
 *    Capacitor plugins ARE available here. The shim installs a
 *    postMessage handler that forwards ONNX RPC requests to the real
 *    `PolarisOnnx` plugin and posts the results back.
 *
 *  - CHILD frame (the remote Polaris UI loaded in an <iframe>): native
 *    plugins are NOT available on a navigated external origin (Capacitor
 *    only bridges plugins on the app origin). So here the shim installs a
 *    drop-in `window.ort` whose InferenceSession/Tensor forward inference
 *    to the parent via postMessage. The unchanged onnx-pipelines.js calls
 *    the same small ORT surface; it now runs on the device's native ORT
 *    (XNNPACK/CPU), dodging the mobile WebGPU fp16 / Safari-OOM limits.
 *
 * The model never crosses postMessage as one giant blob: large models are
 * streamed in 4 MB chunks (beginModel/appendModel) and tensors are small
 * tiles, so every message stays small.
 */
(function () {
  'use strict';
  if (window.__polarisOnnxShim) { return; }
  window.__polarisOnnxShim = true;

  // Resolve the real native plugin (parent/app origin only).
  function realNative() {
    var cap = window.Capacitor;
    if (!cap) return null;
    if (cap.Plugins && cap.Plugins.PolarisOnnx) return cap.Plugins.PolarisOnnx;
    if (typeof cap.registerPlugin === 'function') {
      try {
        var p = cap.registerPlugin('PolarisOnnx');
        if (p) { if (cap.Plugins) { cap.Plugins.PolarisOnnx = p; } return p; }
      } catch (e) { /* fall through */ }
    }
    return null;
  }

  var inIframe = false;
  try { inIframe = (window.top !== window.self); } catch (e) { inIframe = true; }

  // ---------- PARENT role: RPC handler -> native plugin ----------
  if (!inIframe) {
    window.addEventListener('message', async function (ev) {
      var d = ev.data;
      if (!d || d.__polarisOnnxReq !== true) return;
      var reply = { __polarisOnnxRes: true, id: d.id };
      try {
        var Native = realNative();
        if (!Native) {
          var cap = window.Capacitor || {};
          var headers = (cap.PluginHeaders || []).map(function (h) { return h.name; });
          throw new Error('native plugin unavailable in app shell (PluginHeaders=['
            + headers.join(',') + '])');
        }
        var fn = Native[d.method];
        if (typeof fn !== 'function') throw new Error('unknown method ' + d.method);
        reply.result = await fn.call(Native, d.args || {});
        reply.ok = true;
      } catch (e) {
        reply.ok = false;
        reply.error = (e && e.message) || String(e);
      }
      try { ev.source.postMessage(reply, '*'); } catch (e) { /* ignore */ }
    });
    console.log('[polaris] ONNX RPC host ready (parent frame)');
    return; // the parent never runs ONNX itself
  }

  // ---------- CHILD role (iframe): window.ort -> postMessage RPC ----------

  var _rpcId = 0;
  var _pending = {};
  window.addEventListener('message', function (ev) {
    var d = ev.data;
    if (!d || d.__polarisOnnxRes !== true) return;
    var p = _pending[d.id];
    if (!p) return;
    delete _pending[d.id];
    if (d.ok) p.resolve(d.result);
    else p.reject(new Error(d.error || 'native error'));
  });
  function rpc(method, args) {
    return new Promise(function (resolve, reject) {
      var id = 'r' + (++_rpcId);
      _pending[id] = { resolve: resolve, reject: reject };
      try {
        window.parent.postMessage(
          { __polarisOnnxReq: true, id: id, method: method, args: args || {} }, '*');
      } catch (e) {
        delete _pending[id];
        reject(e);
      }
    });
  }
  // RPC proxy mirroring the native plugin's method surface.
  var Native = {
    deviceMemory: function () { return rpc('deviceMemory', {}); },
    beginModel: function (a) { return rpc('beginModel', a); },
    appendModel: function (a) { return rpc('appendModel', a); },
    createSessionFromFile: function (a) { return rpc('createSessionFromFile', a); },
    createSession: function (a) { return rpc('createSession', a); },
    run: function (a) { return rpc('run', a); },
    releaseSession: function (a) { return rpc('releaseSession', a); },
  };

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
  function typedFromBytes(type, u8) {
    var b = u8.buffer, o = u8.byteOffset, n = u8.byteLength;
    switch (type) {
      case 'float32': return new Float32Array(b, o, n / 4);
      case 'float16': return new Uint16Array(b, o, n / 2);
      case 'int32':   return new Int32Array(b, o, n / 4);
      case 'int64':   return new BigInt64Array(b, o, n / 8);
      case 'uint8':   return new Uint8Array(b, o, n);
      case 'bool':    return new Uint8Array(b, o, n);
      default:        return new Float32Array(b, o, n / 4);
    }
  }

  function Tensor(type, data, dims) {
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

  var FILE_THRESHOLD = 8 * 1024 * 1024;   // 8 MB
  var CHUNK = 4 * 1024 * 1024;            // 4 MB raw per appendModel call

  InferenceSession.create = async function (model, options) {
    var u8 = model instanceof Uint8Array ? model
           : (model instanceof ArrayBuffer ? new Uint8Array(model) : null);
    if (!u8) throw new Error('PolarisOnnx shim: model must be Uint8Array/ArrayBuffer');
    var eps = (options && options.executionProviders) || [];

    await assertEnoughMemory(u8.length);

    if (u8.length > FILE_THRESHOLD) {
      var id = 'm' + Date.now().toString(36) + '_' +
               Math.floor(Math.random() * 1e9).toString(36);
      await Native.beginModel({ id: id });
      for (var off = 0; off < u8.length; off += CHUNK) {
        var slice = u8.subarray(off, Math.min(off + CHUNK, u8.length));
        await Native.appendModel({ id: id, chunk: bytesToB64(slice) });
      }
      return sessionFromInfo(await Native.createSessionFromFile(
        { id: id, executionProviders: eps }));
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
    try { await Native.releaseSession({ handle: this._handle }); } catch (e) { /* ignore */ }
  };

  window.ort = {
    Tensor: Tensor,
    InferenceSession: InferenceSession,
    env: { wasm: {}, webgpu: {}, logLevel: 'warning' }
  };
  console.log('[polaris] native ONNX shim active (iframe -> parent RPC -> device CPU)');
})();
