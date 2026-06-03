package dev.danwbr.polaris.onnx;

import android.app.ActivityManager;
import android.content.Context;
import android.net.Uri;
import android.net.http.SslError;
import android.util.Base64;
import android.webkit.SslErrorHandler;
import android.webkit.WebView;

import androidx.webkit.WebViewCompat;
import androidx.webkit.WebViewFeature;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Native ONNX Runtime bridge (M1). Runs the GraXpert .onnx models with
 * NNAPI / XNNPACK / CPU so the existing JS pipelines get device
 * acceleration without the mobile-browser limits.
 *
 * NOTE: the import above uses real ORT types at build time
 * (ai.onnxruntime.*). The actual symbol names are:
 *   OrtEnvironment, OrtSession, OnnxTensor, OnnxTensorLike,
 *   OrtSession.SessionOptions, OrtSession.Result, TensorInfo, etc.
 * They resolve once `onnxruntime-android` is on the classpath (see
 * build.gradle). This file is written for that dependency; it does not
 * compile standalone in this repo (mobile/ is outside NINA.sln).
 */
@CapacitorPlugin(name = "PolarisOnnx")
public class PolarisOnnxPlugin extends Plugin {

    private ai.onnxruntime.OrtEnvironment env;
    private final Map<String, ai.onnxruntime.OrtSession> sessions = new ConcurrentHashMap<>();
    private final AtomicLong counter = new AtomicLong(1);

    @Override
    public void load() {
        env = ai.onnxruntime.OrtEnvironment.getEnvironment();
        injectShimAtDocumentStart();
        acceptLanSslErrors();
    }

    /**
     * The Pi serves the Polaris UI over HTTPS with a self-signed cert
     * (needed for WebGPU's secure-context + general LAN privacy). A stock
     * WebView rejects that cert, so navigating to https://...:5000 fails
     * silently. Here we install a WebViewClient that proceeds through SSL
     * errors ONLY for LAN hosts (*.local, localhost, RFC-1918 private
     * IPs). Public hosts (the Relay domain, which has a real cert) keep
     * strict validation -- a bad cert there is still refused.
     *
     * We subclass Capacitor's BridgeWebViewClient so all of its other
     * behaviour (navigation allow-list, request interception) is kept.
     */
    private void acceptLanSslErrors() {
        try {
            final WebView wv = getBridge().getWebView();
            // Defer to the end of the UI message queue so this runs AFTER
            // Capacitor has finished installing its own BridgeWebViewClient
            // during bridge init -- otherwise ours could be overwritten and
            // the self-signed cert would still be blocked.
            wv.post(new Runnable() {
                @Override
                public void run() {
                    try {
                        wv.setWebViewClient(new com.getcapacitor.BridgeWebViewClient(getBridge()) {
                            @Override
                            public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
                                String host = null;
                                try {
                                    String url = error != null ? error.getUrl() : null;
                                    if (url != null) host = Uri.parse(url).getHost();
                                } catch (Throwable ignore) { }
                                if (isLanHost(host)) {
                                    handler.proceed();   // trust the user's own Pi on the LAN
                                } else {
                                    handler.cancel();    // strict for public hosts (Relay)
                                }
                            }
                        });
                    } catch (Throwable ignore) { }
                }
            });
        } catch (Throwable t) {
            // Non-fatal: without this, only LAN HTTPS to a self-signed Pi
            // is affected; the Relay / http paths still work.
        }
    }

    /** True for *.local, localhost and RFC-1918 private IPv4 ranges. */
    private boolean isLanHost(String host) {
        if (host == null) return false;
        host = host.toLowerCase();
        if (host.equals("localhost") || host.endsWith(".local")) return true;
        if (host.startsWith("10.") || host.startsWith("192.168.") || host.startsWith("127.")) return true;
        if (host.startsWith("172.")) {
            try {
                String[] p = host.split("\\.");
                if (p.length >= 2) {
                    int second = Integer.parseInt(p[1]);
                    if (second >= 16 && second <= 31) return true; // 172.16/12
                }
            } catch (Throwable ignore) { }
        }
        return false;
    }

    /**
     * Inject onnx-native-shim.js into EVERY page (including the remote
     * Polaris UI) before its own scripts run, so the unchanged
     * onnx-pipelines.js picks up our native `ort`. Uses the AndroidX
     * WebView document-start API when available.
     */
    private void injectShimAtDocumentStart() {
        try {
            String shim = readAsset("public/onnx-native-shim.js");
            if (shim == null) return;
            WebView wv = getBridge().getWebView();
            if (WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT)) {
                Set<String> origins = new HashSet<>();
                origins.add("*");
                WebViewCompat.addDocumentStartJavaScript(wv, shim, origins);
            }
        } catch (Throwable t) {
            // Non-fatal: without injection the page falls back to ORT Web.
        }
    }

    private String readAsset(String path) {
        try (InputStream is = getContext().getAssets().open(path);
             BufferedReader r = new BufferedReader(new InputStreamReader(is, StandardCharsets.UTF_8))) {
            StringBuilder sb = new StringBuilder();
            String line;
            while ((line = r.readLine()) != null) sb.append(line).append('\n');
            return sb.toString();
        } catch (Throwable t) {
            return null;
        }
    }

    @PluginMethod
    public void info(PluginCall call) {
        JSObject ret = new JSObject();
        ret.put("version", env.getVersion());
        List<String> p = new ArrayList<>();
        p.add("nnapi"); p.add("xnnpack"); p.add("cpu");
        ret.put("providers", new com.getcapacitor.JSArray(p));
        call.resolve(ret);
    }

    /** Available + total system RAM, so the JS side can refuse to even
     *  try a model that clearly won't fit (instead of OOM-crashing). */
    @PluginMethod
    public void deviceMemory(PluginCall call) {
        try {
            ActivityManager am = (ActivityManager)
                getContext().getSystemService(Context.ACTIVITY_SERVICE);
            ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
            am.getMemoryInfo(mi);
            JSObject ret = new JSObject();
            ret.put("availBytes", mi.availMem);
            ret.put("totalBytes", mi.totalMem);
            ret.put("thresholdBytes", mi.threshold);
            ret.put("lowMemory", mi.lowMemory);
            // Per-app Dalvik heap ceilings (MB). The native ORT runs off
            // the native heap, not Dalvik, but these still hint at how
            // tightly the OEM provisions this device.
            ret.put("appHeapMb", am.getMemoryClass());
            ret.put("appLargeHeapMb", am.getLargeMemoryClass());
            call.resolve(ret);
        } catch (Throwable t) {
            call.reject("deviceMemory failed: " + t.getMessage());
        }
    }

    private File modelDir() {
        File dir = new File(getContext().getCacheDir(), "polaris-onnx-models");
        if (!dir.exists()) dir.mkdirs();
        return dir;
    }

    private void clearModelDir() {
        File[] old = modelDir().listFiles();
        if (old != null) for (File f : old) { try { f.delete(); } catch (Throwable ignore) {} }
    }

    // Shared session builder: create from a model FILE so ORT memory-maps
    // the weights (low RAM), with BASIC graph optimization + XNNPACK
    // (single weight copy; NNAPI would duplicate them onto the
    // accelerator and OOM the big models).
    private JSObject buildSessionFromFile(File modelFile, boolean useGpu) throws Exception {
        ai.onnxruntime.OrtSession.SessionOptions opts =
            new ai.onnxruntime.OrtSession.SessionOptions();
        opts.setOptimizationLevel(
            ai.onnxruntime.OrtSession.SessionOptions.OptLevel.BASIC_OPT);
        String provider = "cpu";
        if (useGpu) {
            // User opted into GPU/NPU: NNAPI routes conv/matmul to the
            // accelerator (some ops still fall back to CPU). It duplicates
            // the weights, so it's only used on request + behind the JS
            // memory guard. Fall back to XNNPACK if NNAPI isn't usable.
            try { opts.addNnapi(); provider = "nnapi"; }
            catch (Throwable ignore) {
                try { opts.addXnnpack(Collections.emptyMap()); provider = "xnnpack"; }
                catch (Throwable ignore2) { /* plain CPU */ }
            }
        } else {
            try { opts.addXnnpack(Collections.emptyMap()); provider = "xnnpack"; }
            catch (Throwable ignore) { /* plain CPU */ }
        }

        ai.onnxruntime.OrtSession session =
            env.createSession(modelFile.getAbsolutePath(), opts);
        String handle = "s" + counter.getAndIncrement();
        sessions.put(handle, session);

        JSObject ret = new JSObject();
        ret.put("handle", handle);
        ret.put("provider", provider);
        ret.put("inputNames", new com.getcapacitor.JSArray(new ArrayList<>(session.getInputNames())));
        ret.put("outputNames", new com.getcapacitor.JSArray(new ArrayList<>(session.getOutputNames())));
        return ret;
    }

    // ---- small-model path: whole model as one base64 blob ----
    @PluginMethod
    public void createSession(PluginCall call) {
        try {
            String b64 = call.getString("model");
            if (b64 == null) { call.reject("model (base64) required"); return; }
            byte[] model = Base64.decode(b64, Base64.DEFAULT);
            clearModelDir();
            File modelFile = new File(modelDir(), "session-" + counter.get() + ".onnx");
            try (FileOutputStream out = new FileOutputStream(modelFile)) { out.write(model); }
            model = null;
            call.resolve(buildSessionFromFile(modelFile, Boolean.TRUE.equals(call.getBoolean("useGpu", false))));
        } catch (Throwable t) {
            call.reject("createSession failed: " + t.getMessage());
        }
    }

    // ---- large-model path: stream the model to a file in chunks so the
    // WebView renderer never builds a giant base64 string (that ~3-4x
    // peak OOM-killed the renderer even for the 200 MB BGE model). ----

    @PluginMethod
    public void beginModel(PluginCall call) {
        try {
            String id = call.getString("id");
            if (id == null) { call.reject("id required"); return; }
            clearModelDir();
            File f = new File(modelDir(), id + ".onnx");
            if (f.exists()) f.delete();
            call.resolve();
        } catch (Throwable t) {
            call.reject("beginModel failed: " + t.getMessage());
        }
    }

    @PluginMethod
    public void appendModel(PluginCall call) {
        try {
            String id = call.getString("id");
            String chunk = call.getString("chunk");
            if (id == null || chunk == null) { call.reject("id + chunk required"); return; }
            byte[] bytes = Base64.decode(chunk, Base64.DEFAULT);
            File f = new File(modelDir(), id + ".onnx");
            try (FileOutputStream out = new FileOutputStream(f, true)) { out.write(bytes); }
            call.resolve();
        } catch (Throwable t) {
            call.reject("appendModel failed: " + t.getMessage());
        }
    }

    @PluginMethod
    public void createSessionFromFile(PluginCall call) {
        try {
            String id = call.getString("id");
            if (id == null) { call.reject("id required"); return; }
            File f = new File(modelDir(), id + ".onnx");
            if (!f.exists()) { call.reject("model file not found (call beginModel/appendModel first)"); return; }
            call.resolve(buildSessionFromFile(f, Boolean.TRUE.equals(call.getBoolean("useGpu", false))));
        } catch (Throwable t) {
            call.reject("createSessionFromFile failed: " + t.getMessage());
        }
    }

    @PluginMethod
    public void run(PluginCall call) {
        ai.onnxruntime.OrtSession session = sessions.get(call.getString("handle"));
        if (session == null) { call.reject("unknown session handle"); return; }
        long t0 = System.nanoTime();
        Map<String, ai.onnxruntime.OnnxTensor> feeds = new LinkedHashMap<>();
        try {
            JSObject feedObj = call.getObject("feeds");
            java.util.Iterator<String> keys = feedObj.keys();
            while (keys.hasNext()) {
                String name = keys.next();
                JSObject t = feedObj.getJSObject(name);
                feeds.put(name, toOnnxTensor(t));
            }
            try (ai.onnxruntime.OrtSession.Result result = session.run(feeds)) {
                JSObject outputs = new JSObject();
                for (Map.Entry<String, ai.onnxruntime.OnnxValue> e : result) {
                    outputs.put(e.getKey(), fromOnnxValue(e.getValue()));
                }
                JSObject ret = new JSObject();
                ret.put("outputs", outputs);
                ret.put("ms", (System.nanoTime() - t0) / 1_000_000.0);
                call.resolve(ret);
            }
        } catch (Throwable err) {
            call.reject("run failed: " + err.getMessage());
        } finally {
            for (ai.onnxruntime.OnnxTensor t : feeds.values()) {
                try { t.close(); } catch (Throwable ignore) {}
            }
        }
    }

    @PluginMethod
    public void releaseSession(PluginCall call) {
        ai.onnxruntime.OrtSession s = sessions.remove(call.getString("handle"));
        if (s != null) { try { s.close(); } catch (Throwable ignore) {} }
        call.resolve();
    }

    // ---- tensor marshalling (base64 little-endian <-> OnnxTensor) ----

    private ai.onnxruntime.OnnxTensor toOnnxTensor(JSObject t) throws Exception {
        byte[] bytes = Base64.decode(t.getString("data"), Base64.DEFAULT);
        String type = t.getString("type", "float32");
        org.json.JSONArray dimsArr = t.getJSONArray("dims");
        long[] dims = new long[dimsArr.length()];
        for (int i = 0; i < dims.length; i++) dims[i] = dimsArr.getLong(i);

        ByteBuffer bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        switch (type) {
            case "float32": {
                FloatBuffer fb = bb.asFloatBuffer();
                return ai.onnxruntime.OnnxTensor.createTensor(env, fb, dims);
            }
            case "int32": {
                return ai.onnxruntime.OnnxTensor.createTensor(env, bb.asIntBuffer(), dims);
            }
            case "int64": {
                return ai.onnxruntime.OnnxTensor.createTensor(env, bb.asLongBuffer(), dims);
            }
            case "uint8":
            case "bool": {
                return ai.onnxruntime.OnnxTensor.createTensor(env, bb, dims,
                    type.equals("bool")
                        ? ai.onnxruntime.OnnxJavaType.BOOL
                        : ai.onnxruntime.OnnxJavaType.UINT8);
            }
            case "float16": {
                // ORT Java takes raw fp16 bytes via the ByteBuffer overload
                // tagged FLOAT16 (same overload used for uint8/bool above).
                return ai.onnxruntime.OnnxTensor.createTensor(env, bb, dims,
                    ai.onnxruntime.OnnxJavaType.FLOAT16);
            }
            default:
                throw new IllegalArgumentException("unsupported tensor type: " + type);
        }
    }

    private JSObject fromOnnxValue(ai.onnxruntime.OnnxValue v) throws Exception {
        ai.onnxruntime.OnnxTensor t = (ai.onnxruntime.OnnxTensor) v;
        ByteBuffer raw = t.getByteBuffer(); // little-endian raw bytes
        byte[] out = new byte[raw.remaining()];
        raw.get(out);
        String type = ortTypeName(t.getInfo().type);
        long[] shape = t.getInfo().getShape();
        com.getcapacitor.JSArray dims = new com.getcapacitor.JSArray();
        for (long d : shape) dims.put(d);

        JSObject o = new JSObject();
        o.put("data", Base64.encodeToString(out, Base64.NO_WRAP));
        o.put("type", type);
        o.put("dims", dims);
        return o;
    }

    private String ortTypeName(ai.onnxruntime.OnnxJavaType jt) {
        switch (jt) {
            case FLOAT:   return "float32";
            case FLOAT16: return "float16";
            case INT32:   return "int32";
            case INT64:   return "int64";
            case UINT8:   return "uint8";
            case BOOL:    return "bool";
            default:      return "float32";
        }
    }
}
