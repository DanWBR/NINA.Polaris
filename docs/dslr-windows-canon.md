# Canon DSLR / Mirrorless on Windows (EDSDK)

On Windows, Polaris talks to Canon EOS bodies (DSLR and mirrorless)
through Canon's **EOS Digital SDK** (EDSDK). Linux users should use
the INDI gphoto driver instead — see `dslr-linux.md`.

## Compatible bodies

Every camera supported by EDSDK 13.x / 14.x. That covers everything
from the EOS 5D Mark II era forward (DSLRs) plus all the modern
EOS R / RP / Ra / R5 / R6 / R7 / R8 / R10 / R50 / R100 mirrorless
bodies. The Canon support matrix is the source of truth:
<https://developercommunity.usa.canon.com/>

## Install the EDSDK DLLs

Canon's EULA does not permit redistribution of EDSDK in third-party
software, so each user has to register and download the SDK
themselves. It's a one-time, free process.

1. Go to <https://developercommunity.usa.canon.com/> (or the
   equivalent regional Canon developer portal).
2. Sign up for a free developer account.
3. Accept the EDSDK licence and download the latest version (13.x
   or newer). The download is a Zip archive.
4. Extract the archive and locate the **Windows** folder. Inside it
   you'll find the DLLs you need:
   - `EDSDK.dll`
   - `EdsImage.dll`
   - `Mc.dll` (codec helper)
   - `EDSDK_64.dll` may also be present — copy that too.
5. Create a folder next to the Polaris executable called
   `plugins/canon-edsdk/` and drop the DLLs there.

   Alternative: copy them next to the Polaris executable directly
   (`NINA.Polaris.exe`). Either location works because the standard
   Windows DLL search path covers both.

6. Restart Polaris so the registry probe re-detects the SDK.

## Connect the camera in Polaris

1. Plug the camera into the host with a USB cable. Use the cable
   that came with the camera or a known-good USB 2.0 cable — USB 3.0
   cables sometimes confuse the body's USB controller.
2. Power the camera on, set the mode dial to **M** (manual).
3. In Polaris **Equipment** → Camera card, pick the **Canon EOS
   (EDSDK)** entry in the driver dropdown. If the entry shows
   *Not installed*, the SDK probe didn't find the DLLs — confirm
   they're in `plugins/canon-edsdk/` and restart.
4. Click **Detect** to refresh the list of connected Canon bodies.
5. Pick the body (you'll see "EOS RP", "EOS 6D Mark II", etc.)
   and click **Connect**.

## Capture behaviour

- **Save destination**: Polaris sets `SaveTo = Host` on every
  connect, so frames go directly into Polaris instead of the
  camera's SD card. You can leave the card removed if you want.
- **Image format**: leave the camera set to **RAW + JPEG**.
  Polaris uses the JPEG for the on-screen preview and saves the
  CR2 (or CR3) verbatim under `{rig}/lights/{target}/{filter}/{session}/`
  alongside what dedicated astronomy cameras produce.
- **ISO**: surfaced as a dropdown in the Camera card (instead of
  the gain field that astronomy cameras use). Common values from
  ISO 50 to ISO 102400.
- **Shutter speed**: Polaris picks the closest discrete Tv value
  for any exposure ≤ 30 s. Anything past 30 s automatically uses
  **Bulb** mode — the SDK opens the shutter, Polaris waits the
  requested time, then closes it.
- **AF / focus**: Polaris does not drive the camera's autofocus
  for you. Set the camera lens / body to **MF** before connecting
  so the Polaris auto-focus routine (which expects an INDI / ASCOM
  focuser) doesn't interact with the lens motor.

## Tips for tethered sessions

- **Power**: tether sessions keep the camera awake the whole time.
  Use a dummy battery + AC adapter (Canon DR-E18 / DR-E6 / DR-E17
  depending on body) or a USB-PD trigger that emulates the camera's
  battery slot.
- **Cable strain**: a USB cable hanging off a moving telescope can
  unplug itself mid-sequence. Loop the cable through the tripod /
  saddle once before plugging it in.
- **Tethering apps that fight Polaris**: only one application can
  hold an EDSDK session per camera. Close Canon EOS Utility,
  DigiCamControl, BackyardEOS, and similar tools before connecting
  in Polaris, otherwise the connect call returns
  `EDS_ERR_DEVICE_BUSY`.

## Troubleshooting

- **"EdsInitializeSDK failed"**: the DLLs aren't reachable. Verify
  `EDSDK.dll` is in `plugins/canon-edsdk/` or next to
  `NINA.Polaris.exe`, and that Polaris is running as a 64-bit
  process (it always is on .NET 10) so the 64-bit DLL is loaded.
- **"Detect" returns no cameras**: another app has the EDSDK
  session, or the camera's USB mode is set to **PC** mode but the
  cable is bad / the port is USB-power-only.
- **Capture hangs**: switch the camera to RAW + JPEG mode if it
  was set to RAW only. Polaris times out the JPEG download after
  300 ms; if your camera reports a longer transfer this is the
  first thing to suspect.
- **CR2 saved but no preview shown**: same as above — the JPEG
  asset never arrived. Most modern bodies always emit a JPEG even
  in RAW-only mode (as the image-review thumbnail), but some older
  bodies don't.

## EULA reminder

The EDSDK DLLs are licensed under Canon's terms; **don't redistribute**
them. The Polaris-side wrappers (`NINA.Camera.CanonEdsdk` in this
repo) are MPL 2.0 — they ship with Polaris but contain only the
P/Invoke surface, no Canon code.
