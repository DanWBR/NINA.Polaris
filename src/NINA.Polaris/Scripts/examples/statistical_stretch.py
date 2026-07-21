# Statistical Stretch, ported to polarispy.
#
# Algorithm from Seti Astro's Statistical Stretch (the PyQt version by
# Cyril Richard, (c) 2026), reimplemented against Polaris's polarispy: the
# native PyQt6 GUI is replaced by a polarispy.Dialog and the pixels are read /
# written through polarispy. Core stretch preserved; the HDR-compress and
# luma-only-recombine options of the original are not (yet) ported.
#
# SPDX-License-Identifier: GPL-3.0-or-later
# Original code: Seti Astro Statistical Stretch / Cyril Richard.
# polaris: name=Statistical Stretch; icon=📈; scope=frame

"""Statistical Stretch (Seti Astro) for the open STUDIO frame.

A histogram-median transfer stretch: brings the image background to a target
level, with optional per-channel (unlinked) mode, a sigma-based black point, a
gentle curves boost, and normalization. Needs numpy + astropy on the host.
"""

import numpy as np

import polarispy


# ---- the ported algorithm (numpy) ---------------------------------------
def _robust_sigma_lower_half(x):
    x = x.reshape(-1)
    if x.size > 400000:
        x = x[:: x.size // 400000]
    med = float(np.median(x))
    lo = x[x <= med]
    if lo.size < 16:
        mad = float(np.median(np.abs(x - med)))
    else:
        mad = float(np.median(np.abs(lo - float(np.median(lo)))))
    return 1.4826 * mad


def _blackpoint_sigma(img, sigma):
    med = float(np.median(img))
    bp = med - float(sigma) * _robust_sigma_lower_half(img)
    return float(min(max(float(img.min()), bp), 0.99)), med


def _curves(image, target_median, curves_boost):
    if curves_boost <= 0.0:
        return np.clip(image, 0.0, 1.0).astype(np.float32)
    img = np.clip(image.astype(np.float32), 0.0, 1.0)
    tm, cb = float(target_median), float(curves_boost)
    p3x = 0.25 * (1.0 - tm) + tm
    p4x = 0.75 * (1.0 - tm) + tm
    p3y = p3x ** (1.0 - cb)
    p4y = (p4x ** (1.0 - cb)) ** (1.0 - cb)
    xs = np.array([0.0, 0.5 * tm, tm, p3x, p4x, 1.0], dtype=np.float32)
    ys = np.array([0.0, 0.5 * tm, tm, p3y, p4y, 1.0], dtype=np.float32)
    return np.clip(np.interp(img, xs, ys), 0.0, 1.0).astype(np.float32)


def _stretch(img, target_median, clip_black=True, sigma=3.0,
             apply_curves=False, curves_boost=0.0, normalize=False):
    tm = max(0.01, min(0.99, float(target_median)))
    bp = _blackpoint_sigma(img, sigma)[0] if clip_black else float(img.min())
    resc = (img - bp) / max(1.0 - bp, 1e-12)
    med_r = float(np.median(resc))
    num = (med_r - 1.0) * tm * resc
    den = med_r * (tm + resc - 1.0) - tm * resc
    den = np.where(np.abs(den) < 1e-12, 1e-12, den)
    out = num / den
    if apply_curves:
        out = _curves(out, tm, curves_boost)
    if normalize:
        mx = float(out.max())
        if mx > 0:
            out = out / mx
    return np.clip(out, 0.0, 1.0).astype(np.float32)


# ---- FITS pixel <-> [0,1] float, colour-axis aware -----------------------
def _to_float01(data):
    orig = np.asarray(data)
    a = orig.astype(np.float32)
    if np.issubdtype(orig.dtype, np.integer):
        a = a / float(np.iinfo(orig.dtype).max)
    else:
        mx = float(a.max()) if a.size else 1.0
        if mx > 1.5:
            a = a / mx
    color = False
    if a.ndim == 3:
        if a.shape[0] == 3 and a.shape[-1] != 3:
            a = np.transpose(a, (1, 2, 0)); color = True   # (3,H,W) -> (H,W,3)
        elif a.shape[-1] == 3:
            color = True
        elif a.shape[0] == 1:
            a = a[0]
    return np.clip(a, 0.0, 1.0), color


def _to_fits(a, color):
    return np.transpose(a, (2, 0, 1)).astype(np.float32) if color else a.astype(np.float32)


def main():
    poe = polarispy.connect()
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=1)
        if not frames:
            poe.log("No frame. Open one in STUDIO, or capture a light.")
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("Statistical Stretch")
    dlg.info("Frame: %s" % path)
    dlg.slider("target", "Target background", 0.05, 0.90, 0.25, step=0.01)
    dlg.checkbox("linked", "Linked RGB (uncheck for per-channel)", True)
    dlg.checkbox("clip_black", "Clip black point (sigma)", True)
    dlg.number("sigma", "Black point sigma", default=3.0, min=0.0, max=10.0, step=0.5)
    dlg.checkbox("curves", "Curves boost", False)
    dlg.slider("boost", "Curves amount", 0.0, 1.0, 0.0, step=0.05)
    dlg.checkbox("normalize", "Normalize to full range", False)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    poe.update_progress("Reading pixels", 0.25)
    data = poe.get_pixeldata()
    if data is None:
        poe.log("The frame has no pixel data.")
        return
    img, color = _to_float01(data)

    poe.update_progress("Stretching", 0.55)
    kw = dict(target_median=v["target"], clip_black=v["clip_black"], sigma=v["sigma"],
              apply_curves=v["curves"], curves_boost=v["boost"], normalize=v["normalize"])
    if color and v["linked"]:
        out = _stretch(img, **kw)
    elif color:
        out = np.stack([_stretch(img[..., c], **kw) for c in range(3)], axis=-1)
    else:
        out = _stretch(img, **kw)

    poe.update_progress("Writing result", 0.8)
    written = poe.set_pixeldata(_to_fits(out, color))
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)


if __name__ == "__main__":
    try:
        main()
    except polarispy.PolarisError as exc:
        polarispy.connect().log("Statistical Stretch failed: %s" % exc)
