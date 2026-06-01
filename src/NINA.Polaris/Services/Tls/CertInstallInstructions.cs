namespace NINA.Polaris.Services.Tls;

/// <summary>
/// TLS-A1: per-OS step-by-step instructions for installing the Polaris
/// self-signed root certificate into the device's system trust store.
///
/// Kept server-side so the UI is a thin renderer and translators only
/// touch one place. Each step is plain text (no HTML) so we can pipe
/// it through a future localisation layer without sanitisation
/// concerns.
///
/// The wording assumes the user has already clicked "Download
/// certificate" and has the .crt file (or scanned the QR) reachable
/// on the target device.
/// </summary>
public static class CertInstallInstructions {
    public static CertInstallGuide? ForOs(string? os) => os?.ToLowerInvariant() switch {
        "ios" or "iphone" or "ipad" => IOS,
        "android" => Android,
        "windows" => Windows,
        "macos" or "mac" => MacOS,
        "linux" => Linux,
        _ => null
    };

    public static IReadOnlyList<CertInstallGuide> All => new[] {
        IOS, Android, Windows, MacOS, Linux
    };

    private static readonly CertInstallGuide IOS = new(
        OsKey: "ios",
        OsLabel: "iPhone / iPad",
        EstimatedMinutes: 3,
        Steps: new[] {
            "Open this Polaris page in Safari on the iPhone / iPad (Chrome on iOS won't import certs).",
            "Tap the Download button above — iOS shows 'Profile Downloaded'.",
            "Open Settings → General → VPN & Device Management.",
            "Under 'Downloaded Profile' tap 'NINA.Polaris (self-signed)'.",
            "Tap Install (top-right) → enter your device passcode → Install again → Done.",
            "STILL in Settings → General → About → scroll down to 'Certificate Trust Settings'.",
            "Find 'NINA.Polaris (self-signed)' and flip the switch to ON.",
            "Tap Continue when iOS warns about full trust.",
            "Open Polaris again in Safari — the address bar should now show a lock icon."
        });

    private static readonly CertInstallGuide Android = new(
        OsKey: "android",
        OsLabel: "Android",
        EstimatedMinutes: 2,
        Steps: new[] {
            "On the Android device, tap the Download button above.",
            "Open Settings → Security → Encryption & credentials → Install a certificate → CA certificate.",
            "Read the warning, tap 'Install anyway'.",
            "Pick the .crt file from Downloads.",
            "Android adds it to the user-installed CAs (NOT to the system store; some apps still warn).",
            "Open Polaris in Chrome — address bar should show the lock icon.",
            "Note: Android 11+ blocks user-installed CAs for apps that pin certs (banking, some social apps). " +
            "Polaris uses the browser, so it's fine."
        });

    private static readonly CertInstallGuide Windows = new(
        OsKey: "windows",
        OsLabel: "Windows 10 / 11",
        EstimatedMinutes: 2,
        Steps: new[] {
            "Click the Download button above to save polaris-root.crt.",
            "Double-click the .crt file.",
            "Click 'Install Certificate...' (the button only shows once; if Windows shows the cert as 'not trusted' that's fine).",
            "Pick 'Local Machine' (so all browsers on the PC trust it) → Next.",
            "Pick 'Place all certificates in the following store' → Browse... → 'Trusted Root Certification Authorities' → OK → Next → Finish.",
            "Click Yes on the security warning.",
            "Restart Chrome / Edge (Firefox uses its own store — see Firefox section).",
            "Open Polaris — the lock icon should now be green / closed."
        });

    private static readonly CertInstallGuide MacOS = new(
        OsKey: "macos",
        OsLabel: "macOS",
        EstimatedMinutes: 2,
        Steps: new[] {
            "Click the Download button above to save polaris-root.crt.",
            "Double-click the .crt file — Keychain Access opens.",
            "Pick the 'System' keychain (NOT 'login') and click Add.",
            "Enter your admin password.",
            "Find 'NINA.Polaris (self-signed)' in the list, double-click it.",
            "Expand the 'Trust' section.",
            "Under 'When using this certificate' pick 'Always Trust'.",
            "Close the window — macOS asks for your password again to save.",
            "Restart Safari / Chrome — Polaris should now load with the lock icon."
        });

    private static readonly CertInstallGuide Linux = new(
        OsKey: "linux",
        OsLabel: "Linux (Chrome / Chromium / Firefox)",
        EstimatedMinutes: 3,
        Steps: new[] {
            "Click the Download button above to save polaris-root.crt.",
            "FOR CHROME / CHROMIUM (NSS-based):",
            "  sudo apt install libnss3-tools   # if you don't have certutil yet",
            "  certutil -d sql:$HOME/.pki/nssdb -A -t 'C,,' -n 'NINA.Polaris' -i polaris-root.crt",
            "  Restart Chrome.",
            "FOR FIREFOX (separate trust store):",
            "  Preferences → Privacy & Security → Certificates → View Certificates → Authorities → Import → pick the .crt → check 'Trust to identify websites'.",
            "FOR SYSTEM-WIDE TRUST (curl, wget, etc.):",
            "  sudo cp polaris-root.crt /usr/local/share/ca-certificates/polaris-root.crt",
            "  sudo update-ca-certificates"
        });
}

public record CertInstallGuide(
    string OsKey,
    string OsLabel,
    int EstimatedMinutes,
    IReadOnlyList<string> Steps
);
