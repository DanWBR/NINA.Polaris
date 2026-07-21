# polaris: name=Gradient Removal; icon=🪄; scope=frame
"""Automatic background / gradient removal (based on AutoGradientRemoval).

Places NO sample points: a smooth low-order polynomial background is fitted on
every pixel that survives an iterative robust rejection, with bright-structure
protection so nebulae are not hollowed out. Choose the polynomial degree (1 for
a plain gradient, higher for vignetting / complex sky) and subtract or divide.
Writes the corrected frame next to the source. Needs numpy + astropy on the
host (install the scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Original: AutoGradientRemoval for Siril (GPL-3.0). Reimplemented against
polarispy: the PyQt6 GUI is replaced by a polarispy.Dialog and the pixels are
read / written through polarispy. This port uses the robust polynomial
background model (the original's "simplified" model); the multiscale surface
model is intentionally not carried over.
"""

import os

import polarispy

try:
    import numpy as np
except ImportError:
    np = None


# ---- numerical core (from AutoGradientRemoval) -----------------------------

def _box1d(a, r, axis):
    """One separable box blur of radius r along `axis`, via running sums."""
    if r < 1:
        return a
    n = a.shape[axis]
    w = 2 * r + 1
    pad = [(0, 0)] * a.ndim
    pad[axis] = (r, r)
    ap = np.pad(a, pad, mode="edge")
    cs = np.cumsum(ap, axis=axis)
    z = np.zeros_like(np.take(cs, [0], axis=axis))
    cs = np.concatenate([z, cs], axis=axis)
    hi = [slice(None)] * a.ndim; hi[axis] = slice(w, w + n)
    lo = [slice(None)] * a.ndim; lo[axis] = slice(0, n)
    return (cs[tuple(hi)] - cs[tuple(lo)]) / w


def _lowpass(img, r, passes=3):
    """Gaussian-like separable low-pass = `passes` box blurs."""
    a = img.astype(np.float64, copy=False)
    for _ in range(passes):
        a = _box1d(a, r, axis=1)
        a = _box1d(a, r, axis=0)
    return a


def _mad_sigma(x):
    """Robust (median, sigma) from the Median Absolute Deviation."""
    med = np.median(x)
    mad = np.median(np.abs(x - med))
    return med, 1.4826 * mad + 1e-12


def _poly_basis(h, w, degree):
    """Tensor polynomial basis terms x^i*y^j (i+j<=degree) on [-1,1]^2."""
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float64)
    xn = xx / max(1, w - 1) * 2.0 - 1.0
    yn = yy / max(1, h - 1) * 2.0 - 1.0
    terms = []
    for i in range(degree + 1):
        for j in range(degree + 1 - i):
            terms.append((xn ** i) * (yn ** j))
    return terms


def _poly_fit(ch, mask, terms):
    """Least-squares fit of the polynomial basis over masked pixels."""
    A = np.stack([t[mask] for t in terms], axis=1)
    b = ch[mask]
    coef, *_ = np.linalg.lstsq(A, b, rcond=None)
    model = np.zeros(ch.shape, dtype=np.float64)
    for c, t in zip(coef, terms):
        model += c * t
    return model


def _structure_mask(residual, grow_radius, protect_threshold, protect_amount):
    """Spatially-coherent mask of extended bright structures to protect."""
    det = (residual > protect_threshold).astype(np.float64)
    if det.max() == 0:
        return np.zeros(residual.shape, dtype=bool)
    grow_r = max(1, int(round(grow_radius * (0.5 + protect_amount))))
    grown = _lowpass(det, grow_r, passes=2)
    cutoff = (1.0 - protect_amount) * 0.5 + 1e-3
    return grown > cutoff


def estimate_background(ch, terms, grow_radius, protect=True, protect_threshold=0.05,
                        protect_amount=0.5, high_k=2.0, low_k=4.0, n_iter=20, log=print):
    """Robust polynomial background for one 2D channel. Even when the bright
    side is rejected, the global low-order fit still extrapolates the trend, so
    (unlike a local surface) this stays stable on strong gradients."""
    keep = np.ones(ch.shape, dtype=bool)
    model = _poly_fit(ch, keep, terms)
    prev = keep.sum()
    min_keep = max(16, int(0.02 * keep.size))

    for it in range(n_iter):
        residual = ch - model
        ref = residual[keep] if keep.any() else residual.ravel()
        med, sigma = _mad_sigma(ref)
        new_keep = (residual <= med + high_k * sigma) & \
                   (residual >= med - low_k * sigma)
        if protect:
            struct = _structure_mask(residual - med, grow_radius,
                                     protect_threshold, protect_amount)
            new_keep &= ~struct
        if new_keep.sum() < min_keep:
            thr = np.percentile(residual, 100 * min_keep / residual.size)
            new_keep = residual <= thr
        model = _poly_fit(ch, new_keep, terms)

        kept = int(new_keep.sum())
        change = abs(kept - prev) / keep.size
        log("  iteration %d: %.1f%% kept" % (it + 1, 100 * kept / keep.size))
        keep = new_keep
        prev = kept
        if it > 0 and change < 1e-4:
            log("  converged")
            break
    return model


def _downsample(img, f):
    """Area-average downsample by integer factor f (block mean)."""
    if f <= 1:
        return img.astype(np.float64, copy=True)
    h, w = img.shape
    hh, ww = (h // f) * f, (w // f) * f
    return img[:hh, :ww].reshape(hh // f, f, ww // f, f).mean(axis=(1, 3))


def _resize_bilinear(img, oh, ow):
    """Bilinear resize of a 2D array to (oh, ow)."""
    h, w = img.shape
    if (h, w) == (oh, ow):
        return img.astype(np.float64, copy=True)
    ys = np.linspace(0, h - 1, oh)
    xs = np.linspace(0, w - 1, ow)
    y0 = np.floor(ys).astype(int); y1 = np.minimum(y0 + 1, h - 1)
    x0 = np.floor(xs).astype(int); x1 = np.minimum(x0 + 1, w - 1)
    wy = (ys - y0)[:, None]; wx = (xs - x0)[None, :]
    Ia = img[np.ix_(y0, x0)]; Ib = img[np.ix_(y0, x1)]
    Ic = img[np.ix_(y1, x0)]; Id = img[np.ix_(y1, x1)]
    top = Ia * (1 - wx) + Ib * wx
    bot = Ic * (1 - wx) + Id * wx
    return top * (1 - wy) + bot * wy


def correct_channel(ch, degree, downsample, mode, protect=True, log=print):
    """Gradient-correct one 2D channel (values assumed in [0, 1])."""
    h, w = ch.shape
    small = _downsample(ch, downsample)
    terms = _poly_basis(small.shape[0], small.shape[1], degree)
    grow_r = max(1, int(round(0.02 * min(small.shape))))
    model_small = estimate_background(small, terms, grow_r, protect=protect, log=log)
    bg = _resize_bilinear(model_small, h, w)
    level = float(np.median(bg))
    if mode == "divide":
        return ch / np.maximum(bg, 1e-6) * level
    return ch - bg + level


def _shrink(a, maxside=500):
    """Fast spatial subsample of a normalized array for a preview."""
    if a.ndim == 2:
        h, w = a.shape
    elif a.ndim == 3 and a.shape[0] == 3:
        _, h, w = a.shape
    else:
        h, w = a.shape[:2]
    step = max(1, int(max(h, w) / float(maxside)))
    if step == 1:
        return a
    if a.ndim == 2:
        return a[::step, ::step]
    if a.shape[0] == 3:
        return a[:, ::step, ::step]
    return a[::step, ::step, :]


def _run_all(arr, vals, downsample):
    """Correct every channel of a normalized array with the given settings."""
    chans, layout = _channels(arr)
    out = np.empty_like(arr)
    for c, ch in chans:
        corr = correct_channel(ch, int(vals["degree"]), downsample, vals["mode"],
                               protect=bool(vals["protect"]), log=lambda *_: None)
        if layout == "mono":
            out = corr
        elif layout == "planar":
            out[c] = corr
        else:
            out[..., c] = corr
    return out


def _channels(arr):
    """Return [(index, 2D channel)] and a layout tag for mono / (3,H,W) / (H,W,3)."""
    if arr.ndim == 2:
        return [(None, arr)], "mono"
    if arr.ndim == 3 and arr.shape[0] == 3:
        return [(c, arr[c]) for c in range(3)], "planar"
    if arr.ndim == 3 and arr.shape[-1] == 3:
        return [(c, arr[..., c]) for c in range(3)], "interleaved"
    raise polarispy.PolarisError("unexpected image shape %r" % (arr.shape,))


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

    dlg = poe.dialog("Gradient Removal")
    dlg.info("Auto background/gradient removal: %s" % os.path.basename(path))
    dlg.number("degree", "Polynomial degree (1 = plain gradient)", default=2, min=1, max=6, step=1)
    dlg.select("mode", "Mode", ["subtract", "divide"], default="subtract")
    dlg.number("downsample", "Downsample (speed)", default=2, min=1, max=4, step=1)
    dlg.checkbox("protect", "Protect bright structures (nebulae)", True)

    _cache = {}

    def _preview(vals):
        if np is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        if "arr" not in _cache:
            d = poe.get_pixeldata()
            if d is None:
                raise polarispy.PolarisError("no pixel data")
            a = d.astype(np.float64)
            if np.issubdtype(d.dtype, np.integer):
                wp = float(np.iinfo(d.dtype).max)
            else:
                mx = float(a.max())
                wp = mx if mx > 1.0 else 1.0
            _cache["arr"] = _shrink(a / wp)
            _cache["white"] = wp
        return _run_all(_cache["arr"], vals, 1) * _cache["white"]

    dlg.preview(_preview)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    if np is None:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy. Install the scripting runtime in "
            "Settings > Scripts (the 'Install runtime' button).")

    poe.update_progress("Reading pixels", 0.2)
    data = poe.get_pixeldata()
    if data is None:
        poe.log("The frame has no pixel data.")
        return
    arr = data.astype(np.float64)

    # The rejection thresholds assume the [0,1] float range Siril works in, but
    # get_pixeldata returns raw ADU. Normalise to a white point so they mean the
    # same fraction of full scale, then scale the corrected result back.
    if np.issubdtype(data.dtype, np.integer):
        white = float(np.iinfo(data.dtype).max)
    else:
        mx = float(arr.max())
        white = mx if mx > 1.0 else 1.0
    poe.update_progress("Correcting", 0.5)
    out = _run_all(arr / white, v, int(v["downsample"])) * white
    poe.update_progress("Writing result", 0.9)
    written = poe.set_pixeldata(out.astype("float32"),
                                out_path=os.path.splitext(path)[0] + "_gradremoved.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
