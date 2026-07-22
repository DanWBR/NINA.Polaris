# polaris: name=Continuum Subtraction; icon=🔬; scope=any
"""Narrowband continuum subtraction (ContinuumSubtraction port).

Pick a narrowband channel (e.g. Ha) and a continuum / broadband channel from the
STUDIO library; the optimal continuum scale is found automatically by minimizing
the residual nonuniformity (AAD) over the whole frame, then the scaled continuum
is subtracted to leave the emission-line signal. An adjust multiplier lets you
nudge the scale. Writes the result next to the narrowband. Needs numpy + astropy
+ scipy on the host (install the scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Original: Narrowband Continuum Subtraction for Siril, (c) 2025 Adrian
Knagg-Baugh & Dave Lindner. Reimplemented against polarispy: the PyQt6 GUI +
region picker are replaced by a polarispy.Dialog over the whole frame; the scale
optimisation (coarse AAD scan + smooth-V curve_fit) is preserved.
"""

import os

import polarispy

try:
    import numpy as np
    from scipy.optimize import curve_fit as _curve_fit
except ImportError:
    np = None
    _curve_fit = None


def _to_mono(img):
    if img is None:
        return None
    a = img.astype(np.float32)
    if img.dtype in (np.uint16, np.int16):
        a = a / 65535.0
    if a.ndim == 3 and a.shape[-1] == 3:
        return 0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2]
    if a.ndim == 3 and a.shape[0] == 3:
        return 0.299 * a[0] + 0.587 * a[1] + 0.114 * a[2]
    return a


def _aad(d):
    return float(np.mean(np.abs(d - np.mean(d))))


def compute_scale(nb, co):
    """Optimal continuum scale c that minimizes AAD(nb - (co - median)*c)."""
    c_median = float(np.median(co))
    coarse = np.linspace(-1.0, 5.0, 12)
    approx = float(coarse[int(np.argmin([_aad(nb - (co - c_median) * s) for s in coarse]))])
    sf = np.linspace(approx - 1.0, approx + 1.0, 40)
    a = np.array([_aad(nb - (co - c_median) * s) for s in sf], dtype=np.float64)

    def smooth_v(x, A, s0, eps, B):
        return A * np.sqrt((x - s0) ** 2 + eps ** 2) + B

    s0_0 = float(sf[int(a.argmin())])
    p0 = [(a[-1] - a[0]) / (sf[-1] - sf[0]), s0_0, 0.01, float(a.min())]
    try:
        popt, _ = _curve_fit(smooth_v, sf, a, p0=p0,
                             bounds=([-1.0, 0.0, 0.0, 0.0], [np.inf, 2 * (approx + 1.0), np.inf, np.inf]),
                             maxfev=5000)
        c = float(np.clip(popt[1], 0.0, 1.0))
    except Exception:
        c = float(np.clip(s0_0, 0.0, 1.0))
    return c, c_median


def subtract(nb, co, c):
    c_median = float(np.median(co))
    return np.clip(nb - (co - c_median) * c, 0.0, 1.0).astype(np.float32)


def main():
    poe = polarispy.connect()
    frames = poe.list_dir()  # image files in the folder open in STUDIO Files
    if not frames:
        poe.log("No image files in the current STUDIO folder.")
        poe.update_progress("Nothing to do", 1.0)
        return

    dlg = poe.dialog("Continuum Subtraction")
    dlg.expects("linear")
    dlg.credits("Narrowband Continuum Subtraction for Siril, (c) 2025 Adrian Knagg-Baugh & Dave Lindner.\nGPL-3.0-or-later. Ported to polarispy.")
    dlg.info("Subtract a scaled continuum from a narrowband channel.")
    dlg.file("nb", "Narrowband (e.g. Ha)", frames)
    dlg.file("co", "Continuum / broadband", frames)
    dlg.slider("adjust", "Scale adjust", 0.5, 1.5, 1.0, step=0.02)

    _cache = {}

    def _small(path):
        if not path:
            return None
        if path not in _cache:
            m = _to_mono(poe.get_pixeldata(path))
            if m is None:
                return None
            step = max(1, int(max(m.shape[0], m.shape[1]) / 600.0))
            _cache[path] = m[::step, ::step]
        return _cache[path]

    def _auto_c(nb, co):
        key = ("c", id(nb), id(co))
        if key not in _cache:
            _cache[key] = compute_scale(nb, co)[0]
        return _cache[key]

    def _preview(vals):
        if np is None or _curve_fit is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        nb, co = _small(vals["nb"]), _small(vals["co"])
        if nb is None or co is None:
            raise polarispy.PolarisError("Pick both the narrowband and continuum channels.")
        if nb.shape != co.shape:
            raise polarispy.PolarisError("The channels must be the same size (register them first).")
        return subtract(nb, co, _auto_c(nb, co) * vals["adjust"])

    dlg.preview(_preview)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    if np is None or _curve_fit is None:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy + scipy. Install the scripting "
            "runtime in Settings > Scripts (the 'Install runtime' button).")
    if not v["nb"] or not v["co"]:
        raise polarispy.PolarisError("Pick both the narrowband and continuum channels.")

    poe.update_progress("Reading channels", 0.3)
    nb = _to_mono(poe.get_pixeldata(v["nb"]))
    co = _to_mono(poe.get_pixeldata(v["co"]))
    if nb.shape != co.shape:
        raise polarispy.PolarisError("The channels must be the same size (register them first).")

    poe.update_progress("Optimizing continuum scale", 0.55)
    c, _ = compute_scale(nb, co)
    c *= v["adjust"]
    poe.log("Continuum scale c = %.4f" % c)
    poe.update_progress("Subtracting", 0.8)
    out = subtract(nb, co, c)

    poe.update_progress("Writing result", 0.9)
    stem = os.path.splitext(v["nb"])[0]
    written = poe.set_pixeldata(out.astype("float32"), path=v["nb"],
                                out_path=stem + "_cs.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
