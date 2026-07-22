# polaris: name=Selective Color; icon=🎨; scope=frame
"""Hue-band selective colour (distilled from AstroColorMixer).

Targets one hue band (red, orange, yellow, green, cyan, blue, magenta) with a
feathered circular-hue mask and adjusts its hue, saturation and luminance -
useful for taming a colour cast or boosting nebula tones without touching the
rest of the frame. Works on a debayered colour image (a raw OSC mosaic is
debayered automatically). Expects stretched data. Writes the result next to the
source. Needs numpy + astropy on the host (install the scripting runtime in
Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Distilled from AstroColorMixer for Siril, (c) 2026 Yannick Dutertre (Cuiv),
after Patrick Cosgrove's PixInsight Astro Color Mixer. The hue-mask math
(rgb_to_hsl / circular_hue_distance / hue_mask) is preserved; the multi-pass /
curves / presets GUI of the original is not ported.
"""

import os

import polarispy

try:
    import numpy as np
except ImportError:
    np = None

_EPS = 1e-6

_BANDS = {"Red": 0.0, "Orange": 30.0, "Yellow": 60.0, "Green": 120.0,
          "Cyan": 180.0, "Blue": 240.0, "Magenta": 300.0}


def _luma709(rgb):
    return 0.2126 * rgb[:, :, 0] + 0.7152 * rgb[:, :, 1] + 0.0722 * rgb[:, :, 2]


def rgb_to_hsl(rgb):
    r, g, b = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    mx = np.max(rgb, axis=2)
    mn = np.min(rgb, axis=2)
    d = mx - mn
    light = (mx + mn) * 0.5
    sat = np.zeros_like(light, dtype=np.float32)
    nz = d > _EPS
    sat[nz] = np.where(light[nz] > 0.5,
                       d[nz] / np.maximum(2.0 - mx[nz] - mn[nz], _EPS),
                       d[nz] / np.maximum(mx[nz] + mn[nz], _EPS))
    hue = np.zeros_like(light, dtype=np.float32)
    rmax = nz & (mx == r); gmax = nz & (mx == g); bmax = nz & (mx == b)
    hue[rmax] = ((g[rmax] - b[rmax]) / np.maximum(d[rmax], _EPS)) % 6.0
    hue[gmax] = ((b[gmax] - r[gmax]) / np.maximum(d[gmax], _EPS)) + 2.0
    hue[bmax] = ((r[bmax] - g[bmax]) / np.maximum(d[bmax], _EPS)) + 4.0
    hue = (hue * 60.0) % 360.0
    return hue.astype(np.float32), sat.astype(np.float32), light.astype(np.float32)


def _circular_hue_distance(hue, center):
    delta = np.abs((hue % 360.0) - (center % 360.0))
    return np.minimum(delta, 360.0 - delta)


def hue_mask(hue, center, width, feather):
    distance = _circular_hue_distance(hue, center)
    outer = max(float(width), _EPS)
    inner = outer * (1.0 - np.clip(float(feather), 0.0, 1.0))
    if outer - inner <= _EPS:
        return (distance <= outer).astype(np.float32)
    t = np.clip((distance - inner) / (outer - inner), 0.0, 1.0)
    return (1.0 - t * t * (3.0 - 2.0 * t)).astype(np.float32)


def hsl_to_rgb(h, s, l):
    c = (1.0 - np.abs(2.0 * l - 1.0)) * s
    hp = (h % 360.0) / 60.0
    x = c * (1.0 - np.abs(hp % 2.0 - 1.0))
    m = l - c / 2.0
    seg = np.floor(hp).astype(int) % 6
    conds = [seg == i for i in range(6)]
    r = np.select(conds, [c, x, 0.0, 0.0, x, c])
    g = np.select(conds, [x, c, c, x, 0.0, 0.0])
    b = np.select(conds, [0.0, 0.0, x, c, c, x])
    return np.clip(np.stack([r + m, g + m, b + m], axis=-1), 0.0, 1.0).astype(np.float32)


def selective_color(rgb, center, hue_shift, saturation, luminance, width, feather):
    h, s, l = rgb_to_hsl(np.clip(rgb, 0.0, 1.0))
    mask = hue_mask(h, center, width, feather)
    h2 = (h + mask * hue_shift) % 360.0
    s2 = np.clip(s * (1.0 + mask * (saturation - 1.0)), 0.0, 1.0)
    l2 = np.clip(l * (1.0 + mask * (luminance - 1.0)), 0.0, 1.0)
    return hsl_to_rgb(h2, s2, l2)


def _load_rgb(poe):
    rgb = poe.get_rgb()  # (H, W, 3), debayers a raw OSC mosaic
    if rgb is None:
        raise polarispy.PolarisError("no pixel data")
    a = rgb.astype(np.float32)
    mx = float(np.nanmax(a)) if a.size else 1.0
    white = float(np.iinfo(np.uint16).max) if mx > 1.5 else 1.0
    return np.clip(a / white, 0.0, 1.0)


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

    dlg = poe.dialog("Selective Color")
    dlg.expects("stretched")
    dlg.credits("Distilled from AstroColorMixer for Siril, (c) 2026 Yannick Dutertre (Cuiv),\nafter Patrick Cosgrove's PixInsight Astro Color Mixer. GPL-3.0-or-later.")
    dlg.info("Colour image: %s" % os.path.basename(path))
    dlg.select("band", "Hue band", list(_BANDS.keys()), default="Red")
    dlg.slider("hue_shift", "Hue shift (deg)", -30.0, 30.0, 0.0, step=1.0)
    dlg.slider("saturation", "Saturation", 0.0, 3.0, 1.0, step=0.05)
    dlg.slider("luminance", "Luminance", 0.5, 1.5, 1.0, step=0.02)
    dlg.slider("width", "Band width (deg)", 10.0, 90.0, 40.0, step=1.0)
    dlg.slider("feather", "Feather", 0.0, 1.0, 0.5, step=0.05)

    _cache = {}

    def _params(v):
        return (_BANDS[v["band"]], v["hue_shift"], v["saturation"],
                v["luminance"], v["width"], v["feather"])

    def _preview(vals):
        if np is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        if "rgb" not in _cache:
            a = _load_rgb(poe)
            step = max(1, int(max(a.shape[0], a.shape[1]) / 720.0))
            _cache["rgb"] = a[::step, ::step, :] if step > 1 else a
        return selective_color(_cache["rgb"], *_params(vals))

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
    rgb = _load_rgb(poe)
    poe.update_progress("Adjusting colour", 0.6)
    out = selective_color(rgb, *_params(v))

    poe.update_progress("Writing result", 0.9)
    written = poe.set_pixeldata(np.moveaxis(out, -1, 0).astype("float32"),
                                out_path=os.path.splitext(path)[0] + "_selcolor.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
