# polaris: name=Background Neutralization; icon=⚖️; scope=frame
"""Neutralize the sky background colour (polarispy pixel script).

Estimates each channel's background level (a low percentile) and subtracts the
per-channel offset so the faint sky becomes a neutral grey - the classic OSC
colour-calibration step before white balance. Works on a debayered colour image
(a raw OSC mosaic is debayered automatically). Writes the result next to the
source. Needs numpy + astropy on the host (install the scripting runtime in
Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Written for polarispy (a standard background-neutralization, not a port).
"""

import os

import polarispy

try:
    import numpy as np
except ImportError:
    np = None


def _neutralize(rgb, strength, percentile):
    """rgb: (H, W, 3) float in [0, 1]. Aligns per-channel background to neutral."""
    bg = np.array([float(np.percentile(rgb[..., c], percentile)) for c in range(3)])
    target = float(bg.min())
    out = rgb.copy()
    for c in range(3):
        out[..., c] = rgb[..., c] - strength * (bg[c] - target)
    return np.clip(out, 0.0, 1.0)


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

    dlg = poe.dialog("Background Neutralization")
    dlg.expects("linear")
    dlg.credits("Background Neutralization - polarispy pixel script (GPL-3.0-or-later).")
    dlg.info("Colour image: %s" % os.path.basename(path))
    dlg.slider("strength", "Strength", 0.0, 1.0, 1.0, step=0.05)
    dlg.slider("percentile", "Background percentile", 1.0, 40.0, 10.0, step=1.0)

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
        return _neutralize(_cache["rgb"], vals["strength"], vals["percentile"])

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
    poe.update_progress("Neutralizing", 0.6)
    out = _neutralize(rgb, v["strength"], v["percentile"])

    poe.update_progress("Writing result", 0.9)
    written = poe.set_pixeldata(np.moveaxis(out, -1, 0).astype("float32"),
                                out_path=os.path.splitext(path)[0] + "_neutralized.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
