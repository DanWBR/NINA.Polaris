package dev.danwbr.polaris.llama;

import android.app.ActivityManager;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.PowerManager;
import android.provider.Settings;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Canopus on-device backend (Android). Downloads the GGUF, runs the bundled
 * llama.cpp `llama-server` on loopback inside a foreground service, and reports
 * a plain OpenAI base URL. The existing in-browser Canopus agent + provider then
 * drive it over HTTP with no other change.
 *
 * The server binary ships as a native lib (jniLibs/arm64-v8a/libllamaserver.so)
 * so it lands in nativeLibraryDir, the only writable-by-nobody, exec-allowed
 * place on modern Android (W^X blocks exec from filesDir). Its own .so deps ship
 * the same way and the loader resolves them from that dir.
 *
 * Invocation mirrors the validated eval (canopus-eval/MOBILE.md): --no-mmap
 * (mandatory on Android), --jinja, half the cores, an 8192 context, and the
 * server's built-in prefix/KV-cache reuse so the tool catalog is paid once.
 */
@CapacitorPlugin(name = "PolarisLlama")
public class PolarisLlamaPlugin extends Plugin {

    static final String BINARY_LIB = "libllamaserver.so";   // jniLibs name of llama-server
    static final String MODEL_SUBDIR = "canopus";
    static final String MODEL_FILE = "model.gguf";
    static final int DEFAULT_PORT = 8823;

    private final ExecutorService io = Executors.newSingleThreadExecutor();

    private File modelDir() {
        File d = new File(getContext().getFilesDir(), MODEL_SUBDIR);
        if (!d.exists()) d.mkdirs();
        return d;
    }
    private File modelFile() { return new File(modelDir(), MODEL_FILE); }

    private String binaryPath() {
        return getContext().getApplicationInfo().nativeLibraryDir + "/" + BINARY_LIB;
    }

    // ---- model download -----------------------------------------------------

    @PluginMethod
    public void downloadModel(final PluginCall call) {
        final String url = call.getString("url");
        if (url == null || url.isEmpty()) { call.reject("url is required"); return; }
        final long expected = call.getLong("expectedBytes", 0L);
        final String sha256 = call.getString("sha256");

        io.execute(() -> {
            try {
                File out = modelFile();
                // Already complete? Skip the download.
                if (out.exists() && (expected <= 0 || out.length() == expected)) {
                    JSObject r = new JSObject();
                    r.put("modelPath", out.getAbsolutePath());
                    r.put("bytes", out.length());
                    call.resolve(r);
                    return;
                }
                File part = new File(out.getAbsolutePath() + ".part");
                long already = part.exists() ? part.length() : 0L;

                HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
                c.setConnectTimeout(30000);
                c.setReadTimeout(60000);
                if (already > 0) c.setRequestProperty("Range", "bytes=" + already + "-");
                c.connect();
                int code = c.getResponseCode();
                boolean resumed = code == 206;
                if (code != 200 && code != 206) { part.delete(); call.reject("HTTP " + code); return; }

                long contentLen = c.getContentLengthLong();
                long total = expected > 0 ? expected
                        : (contentLen > 0 ? contentLen + (resumed ? already : 0) : -1);

                try (InputStream in = c.getInputStream();
                     RandomAccessFile raf = new RandomAccessFile(part, "rw")) {
                    if (resumed) raf.seek(already); else { raf.setLength(0); already = 0; }
                    byte[] buf = new byte[1 << 16];
                    long received = already;
                    long lastEmit = 0;
                    int n;
                    while ((n = in.read(buf)) > 0) {
                        raf.write(buf, 0, n);
                        received += n;
                        if (received - lastEmit >= (4L << 20)) {   // every ~4MB
                            emitProgress(received, total);
                            lastEmit = received;
                        }
                    }
                    emitProgress(received, total);
                }

                if (sha256 != null && !sha256.isEmpty() && !sha256.equalsIgnoreCase(sha256Hex(part))) {
                    part.delete();
                    call.reject("checksum mismatch");
                    return;
                }
                if (out.exists()) out.delete();
                if (!part.renameTo(out)) { call.reject("could not finalize model file"); return; }

                JSObject r = new JSObject();
                r.put("modelPath", out.getAbsolutePath());
                r.put("bytes", out.length());
                call.resolve(r);
            } catch (Exception e) {
                call.reject("download failed: " + e.getMessage(), e);
            }
        });
    }

    private void emitProgress(long received, long total) {
        JSObject ev = new JSObject();
        ev.put("receivedBytes", received);
        ev.put("totalBytes", total);
        ev.put("percent", total > 0 ? (int) (received * 100 / total) : -1);
        notifyListeners("downloadProgress", ev);
    }

    @PluginMethod
    public void deleteModel(PluginCall call) {
        File f = modelFile();
        File part = new File(f.getAbsolutePath() + ".part");
        if (f.exists()) f.delete();
        if (part.exists()) part.delete();
        call.resolve();
    }

    // ---- server lifecycle ---------------------------------------------------

    @PluginMethod
    public void start(final PluginCall call) {
        final File model = modelFile();
        if (!model.exists()) { call.reject("model not downloaded"); return; }
        if (!new File(binaryPath()).exists()) { call.reject("llama-server binary missing from the build"); return; }

        final int port = call.getInt("port", DEFAULT_PORT);
        int cores = Runtime.getRuntime().availableProcessors();
        final int threads = call.getInt("threads", Math.max(2, (cores + 1) / 2));
        final int ctx = call.getInt("contextSize", 8192);

        Context ctxApp = getContext();
        Intent i = new Intent(ctxApp, LlamaServerService.class);
        i.putExtra("binary", binaryPath());
        i.putExtra("model", model.getAbsolutePath());
        i.putExtra("port", port);
        i.putExtra("threads", threads);
        i.putExtra("ctx", ctx);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) ctxApp.startForegroundService(i);
        else ctxApp.startService(i);

        // Resolve only once the server answers /health (cold start loads weights).
        io.execute(() -> {
            String base = "http://127.0.0.1:" + port;
            long deadline = System.currentTimeMillis() + 120000;   // weights load can take a while
            while (System.currentTimeMillis() < deadline) {
                if (probe(base + "/health")) {
                    JSObject r = new JSObject();
                    r.put("url", base + "/v1");
                    r.put("port", port);
                    call.resolve(r);
                    return;
                }
                try { Thread.sleep(500); } catch (InterruptedException ignored) { }
            }
            ctxApp.stopService(new Intent(ctxApp, LlamaServerService.class));
            call.reject("llama-server did not become ready in time");
        });
    }

    @PluginMethod
    public void stop(PluginCall call) {
        Context c = getContext();
        c.stopService(new Intent(c, LlamaServerService.class));
        call.resolve();
    }

    @PluginMethod
    public void status(final PluginCall call) {
        io.execute(() -> {
            File model = modelFile();
            boolean ready = model.exists() && model.length() > 0;
            int port = call.getInt("port", DEFAULT_PORT);
            String base = "http://127.0.0.1:" + port;
            boolean running = probe(base + "/health");
            ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
            ActivityManager am = (ActivityManager) getContext().getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) am.getMemoryInfo(mi);
            PowerManager pm = (PowerManager) getContext().getSystemService(Context.POWER_SERVICE);
            boolean exempt = pm != null && pm.isIgnoringBatteryOptimizations(getContext().getPackageName());

            JSObject r = new JSObject();
            r.put("modelReady", ready);
            r.put("running", running);
            r.put("url", running ? base + "/v1" : "");
            r.put("modelPath", ready ? model.getAbsolutePath() : "");
            r.put("modelBytes", ready ? model.length() : 0);
            // Resource-kill signals: the phone must hold the whole model resident
            // (--no-mmap), so the host gates start on total RAM; batteryExempt tells
            // it whether OEM power management can still reap the foreground service.
            r.put("totalMemBytes", mi.totalMem);
            r.put("availMemBytes", mi.availMem);
            r.put("lowMemory", mi.lowMemory);
            r.put("batteryExempt", exempt);
            call.resolve(r);
        });
    }

    /** Ask the user to exempt the app from battery optimization so Doze / OEM
     *  power managers (MIUI SmartPower killed the eval harness in ~2 min) don't
     *  reap the model's foreground service mid-session. No-op if already exempt. */
    @PluginMethod
    public void requestBatteryExemption(PluginCall call) {
        Context c = getContext();
        PowerManager pm = (PowerManager) c.getSystemService(Context.POWER_SERVICE);
        boolean already = pm != null && pm.isIgnoringBatteryOptimizations(c.getPackageName());
        if (!already) {
            try {
                Intent i = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                        Uri.parse("package:" + c.getPackageName()));
                i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                c.startActivity(i);
            } catch (Exception e) {
                // Some OEMs block the direct request; the user can still do it
                // manually in Settings. Not fatal.
            }
        }
        JSObject r = new JSObject();
        r.put("exempt", already);
        call.resolve(r);
    }

    // ---- helpers ------------------------------------------------------------

    private boolean probe(String url) {
        try {
            HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(1000);
            c.setReadTimeout(1500);
            int code = c.getResponseCode();
            c.disconnect();
            return code == 200;
        } catch (Exception e) {
            return false;
        }
    }

    private static String sha256Hex(File f) throws Exception {
        MessageDigest md = MessageDigest.getInstance("SHA-256");
        try (InputStream in = new java.io.FileInputStream(f)) {
            byte[] buf = new byte[1 << 16];
            int n;
            while ((n = in.read(buf)) > 0) md.update(buf, 0, n);
        }
        StringBuilder sb = new StringBuilder();
        for (byte b : md.digest()) sb.append(String.format("%02x", b));
        return sb.toString();
    }
}
