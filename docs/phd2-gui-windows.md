# Embedding the PHD2 GUI on Windows (TightVNC + noVNC)

Polaris embeds PHD2's native GUI into the **GUIDE → PHD2 GUI** tab so you
can run the Profile Wizard, Brain dialog, equipment chooser, Guiding
Assistant, and dark library without leaving the browser.

The backend differs by host OS:

| Polaris host | Backend                | Setup           |
|--------------|------------------------|-----------------|
| Linux        | xpra (PHD2 in dummy X) | `apt install xpra xserver-xorg-video-dummy` ([details](phd2-gui-embedding.md)) |
| **Windows**  | **TightVNC + noVNC**   | this document   |
| macOS        | not supported          | use PHD2's native window directly |

On Windows we don't have xpra. Instead, the TightVNC service captures
the Windows desktop on `127.0.0.1:5900`, Polaris bridges that RFB
stream over a WebSocket, and the browser renders it with the noVNC
HTML5 client. PHD2 runs in its real Windows window — you maximize
it once and the embedded view shows everything you'd see on the
mini-PC's monitor.

## One-time setup

### 1. Install TightVNC

1. Download the Windows installer from
   <https://www.tightvnc.com/download.php> (pick the 64-bit MSI for
   modern Windows).
2. Run the installer. When the **Service Configuration** dialog
   appears:
   - **Set a password** for "Password for Remote Access". You'll be
     prompted for this once per browser session — Polaris does not
     store it (privacy posture).
   - Leave "Accept incoming connections" enabled (default).
3. Finish the installer. TightVNC registers a Windows Service named
   `tvnserver` that starts automatically.

### 2. (Optional but recommended) Restrict TightVNC to loopback

By default TightVNC accepts connections on every network interface.
Polaris doesn't need that — it only ever connects via
`127.0.0.1:5900`. Restricting TightVNC to loopback removes a
needlessly exposed surface:

1. Right-click the TightVNC tray icon → **Configuration**.
2. **Network** tab → set "IP access control" to allow `127.0.0.1`
   only, or check "Loopback connections only".
3. Apply.

Polaris will warn (red status) in the Settings card if it detects
TightVNC listening on a public interface.

### 3. Re-detect from Polaris

1. Open Polaris in your browser, sign in.
2. Go to **GUIDE** tab → **PHD2 GUI** sub-tab.
3. Click **⟳ Re-detect**. The card should report
   "TightVNC vX.Y detected" + "Service: Running" + the canvas should
   become available.

## Using it

1. Click into the **PHD2 GUI** sub-tab.
2. The noVNC client prompts for the password you set during install.
   Enter it (this happens once per browser session).
3. The Windows desktop appears inside the iframe. Maximize the PHD2
   window so it fills the view.
4. Use PHD2 normally — Profile Wizard, Brain, GA, dark library, all
   work exactly as if you were sitting in front of the mini-PC.

## Troubleshooting

**The Re-detect button says "TightVNC not installed" but I just
installed it.**
- Confirm `tvnserver.exe` exists in `C:\Program Files\TightVNC\`
  (or `Program Files (x86)` for the 32-bit installer).
- Polaris reads the registry key `HKLM\SOFTWARE\TightVNC\Server`.
  If the install wrote to a non-standard path, re-run the TightVNC
  installer and pick the default install location.

**The card shows "TightVNC service is stopped" and the Start button
fails.**
- Polaris needs admin privileges to control Windows services.
  Either:
  - Restart Polaris elevated (right-click → Run as administrator), or
  - Open `services.msc`, find `tvnserver`, and start it manually
    from there. Polaris will pick up the state on the next 15-second
    health probe.

**The canvas connects but the password is rejected.**
- TightVNC has two passwords: "Password for Remote Access"
  (full control) and "View-only password". The noVNC prompt expects
  the full-control one. If you only set the view-only password,
  re-run the TightVNC installer or use the tray Configuration
  dialog to set the main password.

**The canvas connects but I see a black screen.**
- The Windows session might be locked. Connect via RDP or sit at the
  mini-PC's keyboard to unlock the desktop. TightVNC mirrors the
  active session; if no one is logged in, there's nothing to mirror.

**Performance is sluggish on slow WiFi.**
- TightVNC encoder defaults are aggressive. In the tray
  Configuration → Server → Encoding, drop the "Use compression"
  level (lower number = less CPU, more bandwidth) or raise it
  (more CPU on the mini-PC, less bandwidth). Defaults are usually
  fine on a LAN.

## How it works (architecture)

```
Browser ──── HTTPS GET /phd2-vnc/ ────→  Polaris   (serves static noVNC HTML5)
        ──── WS    /phd2-vnc-ws  ────→  Polaris   (WS↔TCP bridge)
                                              ↓ raw TCP
                                         127.0.0.1:5900
                                              ↓
                                         TightVNC service
                                              ↓ captures
                                         Windows desktop
```

- `Services/Phd2VncSessionService.cs` detects TightVNC (registry +
  service controller), probes the listening port, and exposes
  Start/Stop service buttons (admin required).
- `Program.cs` maps `/phd2-vnc-ws` to a 60-line WebSocket↔TCP pump
  pair. Both directions stream 16 KB chunks; cancellation links so
  closing either end tears down both.
- `wwwroot/phd2-vnc/index.html` loads noVNC's `RFB` class from the
  vendored MPL 2.0 bundle and points it at the same-origin
  `/phd2-vnc-ws` URL.
- All three paths (`/phd2-vnc/*` static, `/phd2-vnc-ws` WS, the
  status REST endpoints) go through `AuthMiddleware` — only
  authenticated Polaris users can reach the bridge.

## Licenses

- **TightVNC**: GPLv2. Polaris invokes it as a separate Windows
  service via the BCL `ServiceController`. No code mixing.
- **noVNC**: MPL 2.0. Vendored under
  `wwwroot/js/lib/novnc/` with `LICENSE.txt` adjacent. Compatible
  with Polaris's MPL 2.0.
