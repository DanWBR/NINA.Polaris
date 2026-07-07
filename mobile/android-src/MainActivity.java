package com.danielmedeiros.polaris;

import android.net.Uri;
import android.net.http.SslError;
import android.os.Bundle;
import android.webkit.SslErrorHandler;
import android.webkit.WebView;

import com.getcapacitor.Bridge;
import com.getcapacitor.BridgeActivity;
import com.getcapacitor.BridgeWebViewClient;

/**
 * Accepts the Polaris SBC's self-signed HTTPS cert on the LAN.
 *
 * Polaris serves its UI over HTTPS with a self-signed cert (needed for a
 * secure browsing context: WebGPU/WASM, geolocation). Android's stock
 * {@link BridgeWebViewClient} inherits the default {@code onReceivedSslError},
 * which cancels the load — so the Polaris UI iframe fails its TLS handshake
 * and the tab renders blank unless the user manually installs the cert.
 *
 * We install a {@link BridgeWebViewClient} subclass that proceeds ONLY for
 * private / link-local / loopback hosts (a LAN appliance). Any public host —
 * e.g. the Polaris Relay over a real cert — is cancelled and validated
 * normally, so this does not weaken security off-LAN. Everything else is
 * delegated to Capacitor's client via {@code super}.
 */
public class MainActivity extends BridgeActivity {
    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Bridge bridge = getBridge();
        if (bridge != null && bridge.getWebView() != null) {
            bridge.getWebView().setWebViewClient(new LanTolerantWebViewClient(bridge));
        }
    }

    static class LanTolerantWebViewClient extends BridgeWebViewClient {
        LanTolerantWebViewClient(Bridge bridge) {
            super(bridge);
        }

        @Override
        public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
            if (isLanHost(hostOf(error.getUrl()))) {
                handler.proceed();
            } else {
                handler.cancel();
            }
        }
    }

    static String hostOf(String url) {
        try {
            return Uri.parse(url).getHost();
        } catch (Exception e) {
            return null;
        }
    }

    /** True for private / link-local / loopback hosts (an IP literal or a name). */
    static boolean isLanHost(String host) {
        if (host == null) {
            return false;
        }
        String h = host.toLowerCase();
        if (h.isEmpty()) {
            return false;
        }
        if (h.equals("localhost") || h.endsWith(".local")) {
            return true;
        }
        // IPv6: loopback, link-local (fe80::/10), unique-local (fc00::/7).
        if (h.equals("::1") || h.startsWith("fe80:") || h.startsWith("fc") || h.startsWith("fd")) {
            return true;
        }
        // IPv4 private ranges + loopback + link-local.
        String[] p = h.split("\\.");
        if (p.length == 4) {
            try {
                int a = Integer.parseInt(p[0]);
                int b = Integer.parseInt(p[1]);
                if (a == 10) return true;                       // 10.0.0.0/8
                if (a == 172 && b >= 16 && b <= 31) return true; // 172.16.0.0/12
                if (a == 192 && b == 168) return true;          // 192.168.0.0/16
                if (a == 169 && b == 254) return true;          // 169.254.0.0/16
                if (a == 127) return true;                      // loopback
            } catch (NumberFormatException e) {
                return false;
            }
        }
        return false;
    }
}
