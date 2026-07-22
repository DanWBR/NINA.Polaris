# polaris: name=Narrowband to RGB; icon=🌈; scope=any
"""Combine mono Ha / OIII (+ optional SII) into an RGB image (NBtoRGBstars port).

Picks mono narrowband channels from the STUDIO library and blends them into a
colour image (R = Ha/SII, G = Ha:OIII mix, B = OIII), with optional star stretch,
a green-cast SCNR and a saturation lift. Expects stretched channels. Writes the
result next to the Ha channel. Needs numpy + astropy on the host (install the
scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Original: NBtoRGBstars for Siril, (c) Cyril Richard from Franklin Marek (SAS)
code (2025). Reimplemented against polarispy: the PyQt6 GUI is replaced by a
polarispy.Dialog with file dropdowns; the combination math is preserved.
"""

import os

import polarispy

try:
    import numpy as np
except ImportError:
    np = None


def _to_mono(img):
    """Normalize a frame to a mono float plane in [0, 1]."""
    if img is None:
        return None
    a = img.astype(np.float32)
    if img.dtype in (np.uint16, np.int16):
        a = a / 65535.0
    else:
        a = np.clip(a, 0.0, 1.0)
    if a.ndim == 3 and a.shape[-1] == 3:
        return 0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2]
    if a.ndim == 3 and a.shape[0] == 3:
        return 0.299 * a[0] + 0.587 * a[1] + 0.114 * a[2]
    return a


def _star_stretch(image, factor):
    image = np.clip(image, 0.0, 1.0)
    a, b = 3.0, factor
    return np.clip(((a ** b) * image) / (((a ** b) - 1) * image + 1), 0.0, 1.0)


def _scnr(image):
    r, g, b = image[..., 0], image[..., 1].copy(), image[..., 2]
    max_rb = np.maximum(r, b)
    mask = g > max_rb
    g[mask] = max_rb[mask]
    return np.stack([r, g, b], axis=-1)


def combine_mono(ha, oiii, sii, ratio, star_stretch, factor):
    """ha/oiii/(sii) mono float [0,1] of equal shape. Returns (H, W, 3)."""
    r = 0.5 * ha + 0.5 * (sii if sii is not None else ha)
    g = ratio * ha + (1.0 - ratio) * oiii
    b = oiii
    img = np.clip(np.stack([r, g, b], axis=-1), 0.0, 1.0)
    if star_stretch:
        img = _star_stretch(img, factor)
    img = _scnr(img)
    mean = np.mean(img, axis=-1, keepdims=True)
    return np.clip(mean + (img - mean) * 1.2, 0.0, 1.0).astype(np.float32)


def main():
    poe = polarispy.connect()
    frames = poe.list_dir()  # image files in the folder open in STUDIO Files
    if not frames:
        poe.log("No image files in the current STUDIO folder.")
        poe.update_progress("Nothing to do", 1.0)
        return

    def _p(f):
        return f.get("path") or f.get("Path") or ""

    sii_choices = [{"value": "", "label": "(none)"}]
    sii_choices += [{"value": _p(f), "label": os.path.basename(_p(f))} for f in frames if _p(f)]

    dlg = poe.dialog("Narrowband to RGB")
    dlg.expects("stretched")
    dlg.credits("NBtoRGBstars for Siril, (c) Cyril Richard from Franklin Marek (SAS) code (2025).\nGPL-3.0-or-later. Ported to polarispy.")
    dlg.info("Combine mono Ha / OIII (+ optional SII) into an RGB image.")
    dlg.file("ha", "Ha channel", frames)
    dlg.file("oiii", "OIII channel", frames)
    dlg.select("sii", "SII (optional)", sii_choices, default="")
    dlg.slider("ratio", "Ha : OIII ratio (green)", 0.0, 1.0, 0.3, step=0.05)
    dlg.checkbox("star_stretch", "Star stretch", False)
    dlg.slider("factor", "Stretch factor", 0.0, 1.0, 0.5, step=0.05)

    _cache = {}

    def _small(path):
        if not path:
            return None
        if path not in _cache:
            d = poe.get_pixeldata(path)
            m = _to_mono(d)
            if m is None:
                return None
            step = max(1, int(max(m.shape[0], m.shape[1]) / 720.0))
            _cache[path] = m[::step, ::step]
        return _cache[path]

    def _preview(vals):
        if np is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        ha, oiii = _small(vals["ha"]), _small(vals["oiii"])
        if ha is None or oiii is None:
            raise polarispy.PolarisError("Pick both the Ha and OIII channels.")
        sii = _small(vals["sii"])
        if ha.shape != oiii.shape or (sii is not None and sii.shape != ha.shape):
            raise polarispy.PolarisError("The channels must be the same size (register them first).")
        return combine_mono(ha, oiii, sii, vals["ratio"], bool(vals["star_stretch"]), vals["factor"])

    dlg.preview(_preview)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    if np is None:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy. Install the scripting runtime in "
            "Settings > Scripts (the 'Install runtime' button).")
    if not v["ha"] or not v["oiii"]:
        raise polarispy.PolarisError("Pick both the Ha and OIII channels.")

    poe.update_progress("Reading channels", 0.3)
    ha = _to_mono(poe.get_pixeldata(v["ha"]))
    oiii = _to_mono(poe.get_pixeldata(v["oiii"]))
    sii = _to_mono(poe.get_pixeldata(v["sii"])) if v["sii"] else None
    if ha.shape != oiii.shape or (sii is not None and sii.shape != ha.shape):
        raise polarispy.PolarisError("The channels must be the same size (register them first).")

    poe.update_progress("Combining", 0.6)
    out = combine_mono(ha, oiii, sii, v["ratio"], bool(v["star_stretch"]), v["factor"])

    poe.update_progress("Writing result", 0.9)
    stem = os.path.splitext(v["ha"])[0]
    written = poe.set_pixeldata(np.moveaxis(out, -1, 0).astype("float32"),
                                path=v["ha"], out_path=stem + "_HOO.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The RGB result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
