import type { CapacitorConfig } from '@capacitor/cli';

/**
 * The shell launches from the bundled `www/` (the connect screen). After
 * the operator picks a Polaris host, the WebView navigates to it (e.g.
 * https://polaris-app.local:5000). `server.allowNavigation` keeps the
 * Capacitor bridge -- and our `polaris-onnx` plugin -- alive on that
 * remote origin so the unchanged Polaris UI can call native inference.
 *
 * androidScheme stays https so the remote LAN HTTPS origin is treated as
 * a secure context (also lets the Pi self-signed cert flow be handled).
 */
const config: CapacitorConfig = {
  appId: 'dev.danwbr.polaris',
  appName: 'Polaris',
  webDir: 'www',
  server: {
    androidScheme: 'https',
    // Allow navigation to any LAN host / the Relay. Tighten to specific
    // hosts later if desired. Wildcards per Capacitor allowNavigation.
    allowNavigation: ['*.local', '*'],
  },
  android: {
    // LAN HTTPS uses a self-signed cert; allow it in dev builds. For a
    // hardened build, pin the fingerprint from /api/system/server-cert
    // instead of blanket-allowing mixed/insecure content.
    allowMixedContent: true,
  },
  ios: {
    // Same rationale; ATS exceptions for the LAN cert are set in the
    // generated Info.plist after `npx cap add ios`.
    limitsNavigationsToAppBoundDomains: false,
  },
};

export default config;
