# polaris: name=Auto Stretch; icon=📊; scope=frame
"""MTF autostretch (AutoStretch_Preview port).

A PixInsight-style midtones-transfer autostretch: clips shadows at a sigma below
the median, then balances the midtones to a target background. Linked (one set
of params for all channels) or per-channel. Works on a debayered colour image
(a raw OSC mosaic is debayered automatically) or a mono frame. Expects LINEAR
data. Writes the result next to the source. Needs numpy + astropy on the host
(install the scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Original: AutoStretch_Preview for Siril (GPL-3.0). Reimplemented against
polarispy: the PyQt6 GUI is replaced by a polarispy.Dialog. Core math preserved.
"""

import os

import polarispy

try:
    import numpy as np
except ImportError:
    np = None

MAD_NORM = 1.4826


def _mtf_inverse(x, y):
    denom = (x + y - 2.0 * x * y)
    if denom <= 1e-12:
        return 0.5
    return float(min(max(x * (1.0 - y) / denom, 1e-6), 1.0 - 1e-6))


def _apply_mtf_array(data, shadows, midtones, highlights):
    span = highlights - shadows
    if span <= 0.0:
        span = 1.0
    data = np.clip((data - shadows) / span, 0.0, 1.0)
    m = midtones
    if abs(m - 0.5) < 1e-9:
        return data
    denom = (2.0 * m - 1.0) * data - m
    safe = np.where(np.abs(denom) < 1e-9, 1e-9, denom)
    return np.clip(((m - 1.0) * data) / safe, 0.0, 1.0)


def _params_for(med, mad, target_bg, sigma):
    if mad == 0.0:
        mad = 0.001
    shadows = max(0.0, med + sigma * mad)
    midtones = _mtf_inverse(med - shadows, target_bg)
    return shadows, midtones, 1.0


def autostretch(image, target_bg, sigma, linked):
    """image: (C, H, W) float in [0, 1]. Returns the stretched (C, H, W)."""
    c = image.shape[0]
    medians = [float(np.median(image[ch])) for ch in range(c)]
    mads = [float(np.median(np.abs(image[ch] - medians[ch]))) for ch in range(c)]

    if linked:
        valid = [(medians[i], mads[i]) for i in range(c) if medians[i] <= 0.5]
        if not valid:
            params = [(0.0, 0.5, 1.0)] * c
        else:
            med_mean = sum(m for m, _ in valid) / len(valid)
            mad_mean = sum(d for _, d in valid) / len(valid) * MAD_NORM
            params = [_params_for(med_mean, mad_mean, target_bg, sigma)] * c
    else:
        params = []
        for ch in range(c):
            if medians[ch] > 0.5:
                params.append((0.0, 0.5, 1.0))
            else:
                params.append(_params_for(medians[ch], mads[ch] * MAD_NORM, target_bg, sigma))

    out = np.empty_like(image)
    for ch in range(c):
        out[ch] = _apply_mtf_array(image[ch], *params[ch])
    return out


def _load_planar(poe):
    """Return (C, H, W) float in [0, 1], debayering a raw OSC mosaic."""
    try:
        img = poe.get_rgb()               # (H, W, 3)
        a = np.moveaxis(img.astype(np.float32), -1, 0)
    except polarispy.PolarisError:
        m = poe.get_pixeldata()
        if m is None or m.ndim != 2:
            raise polarispy.PolarisError("Auto Stretch needs a colour or mono frame.")
        a = m.astype(np.float32)[None]     # (1, H, W)
    mx = float(np.nanmax(a)) if a.size else 1.0
    white = float(np.iinfo(np.uint16).max) if mx > 1.5 else 1.0
    return np.clip(a / white, 0.0, 1.0)


def main():
    poe = polarispy.connect()
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=1)
        if not frames:
            poe.log("No frame. Open one in STUDIO, or capture a light.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("Auto Stretch")
    dlg.expects("linear")
    dlg.credits("AutoStretch_Preview for Siril - GPL-3.0-or-later.\nPorted to polarispy.")
    dlg.info("Linear frame: %s" % os.path.basename(path))
    dlg.slider("target_bg", "Target background", 0.05, 0.90, 0.25, step=0.01)
    dlg.slider("sigma", "Shadow clip (sigma)", -5.0, 0.0, -2.8, step=0.1)
    dlg.checkbox("linked", "Linked channels (uncheck for per-channel)", True)

    _cache = {}

    def _preview(vals):
        if np is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        if "img" not in _cache:
            a = _load_planar(poe)
            step = max(1, int(max(a.shape[1], a.shape[2]) / 720.0))
            _cache["img"] = a[:, ::step, ::step] if step > 1 else a
        return autostretch(_cache["img"], vals["target_bg"], vals["sigma"], bool(vals["linked"]))

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
    img = _load_planar(poe)
    poe.update_progress("Stretching", 0.6)
    out = autostretch(img, v["target_bg"], v["sigma"], bool(v["linked"]))

    poe.update_progress("Writing result", 0.9)
    data = out[0] if out.shape[0] == 1 else out
    written = poe.set_pixeldata(data.astype("float32"),
                                out_path=os.path.splitext(path)[0] + "_autostretch.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
