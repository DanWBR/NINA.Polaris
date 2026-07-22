# polaris: name=Color Saturation; icon=🌈; scope=frame
"""Boost colour saturation on a stretched image (polarispy pixel script).

Pushes each pixel away from its luminance (grey) to increase chroma, with an
optional background protection that keeps faint sky from getting colour noise
amplified. Works on a debayered colour image (a raw OSC mosaic is debayered
automatically). Writes the result next to the source. Needs numpy + astropy on
the host (install the scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Written for polarispy (a standard chroma-boost, not a port of a specific script).
"""

import os

import polarispy

try:
    import numpy as np
except ImportError:
    np = None

# Rec. 709 luminance weights.
_LW = (0.2126, 0.7152, 0.0722)


def _saturate(rgb, amount, protect):
    """rgb: (H, W, 3) float in [0, 1]. Returns the saturated image."""
    lum = _LW[0] * rgb[..., 0] + _LW[1] * rgb[..., 1] + _LW[2] * rgb[..., 2]
    lum3 = lum[..., None]
    sat = lum3 + amount * (rgb - lum3)
    if protect > 0.0:
        bg = float(np.percentile(lum, 25))
        hi = float(np.percentile(lum, 90))
        t = np.clip((lum - bg) / max(hi - bg, 1e-6), 0.0, 1.0)
        mask = (t ** (1.0 + 4.0 * protect))[..., None]
        sat = rgb + mask * (sat - rgb)
    return np.clip(sat, 0.0, 1.0)


def main():
    poe = polarispy.connect()
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=1)
        if not frames:
            poe.log("No frame. Open a colour image in STUDIO.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("Color Saturation")
    dlg.expects("stretched")
    dlg.credits("Color Saturation - polarispy pixel script (GPL-3.0-or-later).")
    dlg.info("Colour image: %s" % os.path.basename(path))
    dlg.slider("amount", "Saturation", 0.5, 3.0, 1.5, step=0.05)
    dlg.slider("protect", "Background protection", 0.0, 1.0, 0.5, step=0.05)

    _cache = {}

    def _load_rgb():
        rgb = poe.get_rgb()  # (H, W, 3), debayers a raw OSC mosaic
        if rgb is None:
            raise polarispy.PolarisError("no pixel data")
        a = rgb.astype(np.float32)
        mx = float(np.nanmax(a)) if a.size else 1.0
        white = float(np.iinfo(np.uint16).max) if mx > 1.5 else 1.0
        return np.clip(a / white, 0.0, 1.0)

    def _preview(vals):
        if np is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        if "rgb" not in _cache:
            a = _load_rgb()
            step = max(1, int(max(a.shape[0], a.shape[1]) / 720.0))
            _cache["rgb"] = a[::step, ::step, :] if step > 1 else a
        return _saturate(_cache["rgb"], vals["amount"], vals["protect"])

    dlg.preview(_preview)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    if np is None:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy. Install the scripting runtime in "
            "Settings > Scripts (the 'Install runtime' button).")

    poe.update_progress("Reading pixels", 0.3)
    rgb = _load_rgb()
    poe.update_progress("Saturating", 0.6)
    out = _saturate(rgb, v["amount"], v["protect"])

    poe.update_progress("Writing result", 0.9)
    written = poe.set_pixeldata(np.moveaxis(out, -1, 0).astype("float32"),
                                out_path=os.path.splitext(path)[0] + "_saturated.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
