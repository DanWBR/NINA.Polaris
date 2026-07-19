package dev.danwbr.polaris.llama;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

import androidx.annotation.Nullable;

import java.io.BufferedReader;
import java.io.File;
import java.io.InputStreamReader;
import java.util.ArrayList;
import java.util.List;

/**
 * Foreground service that owns the llama-server process. A persistent
 * notification is required so Android's Doze and OEM power managers (MIUI's
 * SmartPower killed the eval harness after ~2 min) don't reap the model host
 * mid-session. The process is bound to the service lifecycle: stopService
 * (from the plugin) tears it down.
 */
public class LlamaServerService extends Service {

    private static final String TAG = "CANOPUS-LLAMA";
    private static final String CHANNEL = "canopus_llama";
    private static final int NOTIF_ID = 4823;

    private Process process;
    private Thread drain;

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        startForeground(NOTIF_ID, buildNotification());
        if (intent == null) return START_NOT_STICKY;
        if (process != null) return START_STICKY;   // already running

        String binary = intent.getStringExtra("binary");
        String model = intent.getStringExtra("model");
        int port = intent.getIntExtra("port", 8823);
        int threads = intent.getIntExtra("threads", 4);
        int ctx = intent.getIntExtra("ctx", 8192);

        List<String> cmd = new ArrayList<>();
        cmd.add(binary);
        cmd.add("--model"); cmd.add(model);
        cmd.add("--host"); cmd.add("127.0.0.1");
        cmd.add("--port"); cmd.add(String.valueOf(port));
        cmd.add("--no-mmap");                 // mandatory on Android (page-cache reclaim, 343x)
        cmd.add("--jinja");                   // use the model's chat template for tool calls
        cmd.add("-t"); cmd.add(String.valueOf(threads));
        cmd.add("-c"); cmd.add(String.valueOf(ctx));
        cmd.add("--cache-reuse"); cmd.add("256");   // reuse the KV prefix across turns
        cmd.add("--no-webui");

        try {
            ProcessBuilder pb = new ProcessBuilder(cmd);
            pb.directory(new File(binary).getParentFile());   // so it finds its sibling .so deps
            pb.redirectErrorStream(true);
            process = pb.start();
            drain = new Thread(this::drainLog, "llama-log");
            drain.setDaemon(true);
            drain.start();
            Log.i(TAG, "llama-server started on 127.0.0.1:" + port + " (t=" + threads + ", c=" + ctx + ")");
        } catch (Exception e) {
            Log.e(TAG, "failed to start llama-server", e);
            stopSelf();
        }
        return START_STICKY;
    }

    private void drainLog() {
        try (BufferedReader r = new BufferedReader(new InputStreamReader(process.getInputStream()))) {
            String line;
            while ((line = r.readLine()) != null) Log.i(TAG, line);
        } catch (Exception ignored) {
        }
    }

    private Notification buildNotification() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel ch = new NotificationChannel(
                CHANNEL, "Canopus on-device model", NotificationManager.IMPORTANCE_LOW);
            ch.setShowBadge(false);
            getSystemService(NotificationManager.class).createNotificationChannel(ch);
        }
        Notification.Builder b = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
            ? new Notification.Builder(this, CHANNEL)
            : new Notification.Builder(this);
        return b
            .setContentTitle("Canopus")
            .setContentText("On-device model running")
            .setSmallIcon(android.R.drawable.stat_sys_download_done)
            .setOngoing(true)
            .build();
    }

    @Override
    public void onDestroy() {
        if (process != null) {
            process.destroy();
            process = null;
        }
        if (drain != null) drain.interrupt();
        super.onDestroy();
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) { return null; }
}
