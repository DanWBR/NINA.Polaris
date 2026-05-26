# DSLR / Mirrorless on Linux (via INDI + gphoto2)

On Linux, Polaris reuses the INDI `indi_gphoto_ccd` driver to talk to
DSLR and mirrorless cameras. That driver wraps libgphoto2 internally
and emits captured frames as INDI BLOBs, which Polaris already
consumes via its INDI client. No additional Polaris configuration is
required beyond installing the driver and selecting the camera in the
Equipment tab.

The list of supported camera bodies is the libgphoto2 list:
<http://www.gphoto.org/proj/libgphoto2/support.php>

## Install the gphoto INDI driver

On Debian / Raspberry Pi OS:

```bash
sudo apt install indi-gphoto
```

On distributions that ship a minimal INDI metapackage, the full
3rd-party set covers it:

```bash
sudo apt install indi-3rdparty
```

If the package isn't in your distribution, build from the
[`indi-3rdparty`](https://github.com/indilib/indi-3rdparty) repo:

```bash
git clone https://github.com/indilib/indi-3rdparty.git
cd indi-3rdparty/indi-gphoto
mkdir build && cd build
cmake -DCMAKE_INSTALL_PREFIX=/usr ..
make -j$(nproc)
sudo make install
```

## Start the INDI server with the gphoto driver

Either include it in your usual `indiserver` invocation:

```bash
indiserver -v indi_gphoto_ccd
```

…or, if you use [INDI Web Manager](https://github.com/knro/indiwebmanager),
add **GPhoto** to the active profile and start the server from the
Web Manager UI.

## Connect the camera to Polaris

1. Plug the camera into the host with a USB cable (use a USB 2.0
   port or a powered hub, bus-powered USB 3.0 hubs are flaky with
   some camera bodies).
2. Power the camera on and set it to **M** (manual) shooting mode.
3. In Polaris **Settings**, point the INDI host at the machine
   running `indiserver` (typically `localhost:7624`) and click
   **Connect**.
4. Open **Equipment**. The camera dropdown should now list the body
   (e.g. *GPhoto CCD*, *EOS Ra*, *Z 6*, *α7R IV*).
5. Pick it, click **Connect**, capture a test frame.

## Tips

- **Battery life**: cameras don't sleep while in tether mode. Always
  use a dummy battery + AC adapter (or a USB-PD trigger that mimics
  the model's power input) for long sessions.
- **Storage**: by default the `indi_gphoto_ccd` driver pulls captures
  straight to the host (no SD card needed). If you also want a copy
  on the card, set the `CAPTURE_TARGET` switch to **Internal RAM +
  Card** via the INDI control panel.
- **Bulb mode**: exposures longer than the camera's native max (often
  30 s) require Bulb. The gphoto driver handles this automatically
  when the exposure is set above 30 s on bodies that support it.
- **RAW vs JPEG**: `indi_gphoto_ccd` exposes a `CAPTURE_FORMAT` switch.
  Most users want **Native** (the camera's CR2 / NEF / ARW) so the
  Studio panel can demosaic later; set **JPEG** if you just want quick
  preview frames.
- **Sensor temperature**: most DSLRs don't report a sensor temperature
  to the OS, Polaris shows the field as blank for these cameras. The
  Camera card hides cooler controls accordingly.

## Troubleshooting

- **Camera not detected**: check `lsusb` for the body. If it doesn't
  appear, suspect the cable or the USB port. If it appears but
  `gphoto2 --auto-detect` doesn't list it, kill any process that's
  claimed the device, `gvfsd-gphoto2` from GNOME / desktop file
  managers is the usual culprit (`pkill -f gvfsd-gphoto2`).
- **"Could not claim USB device" errors in `indiserver` logs**:
  another process is holding the USB handle. Same fix as above.
- **Driver crashes mid-capture**: turn the camera off, unplug the
  cable, plug it back in, power it on. The gphoto state machine can
  get wedged after a USB disconnect.

## On Windows

`indi_gphoto_ccd` doesn't run on Windows. For DSLRs on Windows,
Polaris uses native vendor SDKs instead, see the matching docs:

- `docs/dslr-windows-canon.md`, Canon EOS via EDSDK
- `docs/dslr-windows-nikon.md`, Nikon DSLR / Z via Nikon SDK
- `docs/dslr-windows-sony.md` , Sony α via Camera Remote SDK
