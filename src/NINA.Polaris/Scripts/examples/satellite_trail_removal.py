# polaris: name=Satellite Trail Removal; icon=🛰️; scope=frame
"""Detect and remove satellite / plane trails automatically (OpenCV).

Finds long bright streaks with a Canny + probabilistic Hough transform, builds a
thickened mask along them, and replaces the masked pixels with the local
smoothed background so the trail vanishes. Works on a debayered colour image (a
raw OSC mosaic is debayered automatically) or a mono frame. Writes the result
next to the source. Needs numpy + astropy + OpenCV on the host (install the
scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Written for polarispy (an automatic streak remover; inspired by the siril-scripts
Satellite_Trail_Removal, which is a manual point-and-click tool).
"""

import os

import polarispy

try:
    import numpy as np
    import cv2
except ImportError:
    np = None
    cv2 = None


def _luma01(arr):
    a = arr.astype(np.float32)
    mx = float(np.nanmax(a)) if a.size else 1.0
    if mx > 1.5:
        a = a / mx
    if a.ndim == 3 and a.shape[0] == 3:
        return np.clip(0.2126 * a[0] + 0.7152 * a[1] + 0.0722 * a[2], 0, 1), mx if mx > 1.5 else 1.0
    if a.ndim == 3 and a.shape[-1] == 3:
        return np.clip(0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2], 0, 1), mx if mx > 1.5 else 1.0
    return np.clip(a, 0, 1), mx if mx > 1.5 else 1.0


def _trail_mask(lum, sensitivity, thickness):
    """Boolean mask of detected linear streaks."""
    med = float(np.median(lum))
    mad = float(np.median(np.abs(lum - med))) * 1.4826 + 1e-6
    disp = np.clip((lum - med) / (8.0 * mad), 0.0, 1.0)     # reveal faint streaks
    u8 = (disp * 255.0).astype(np.uint8)
    edges = cv2.Canny(u8, 40, 120)
    minLen = int(0.25 * max(lum.shape))                     # a real trail is long
    thr = int(np.interp(sensitivity, [0.0, 1.0], [250, 60]))  # lower thr = more sensitive
    lines = cv2.HoughLinesP(edges, 1, np.pi / 180.0, threshold=thr,
                            minLineLength=minLen, maxLineGap=int(0.03 * max(lum.shape)))
    mask = np.zeros(lum.shape, np.uint8)
    if lines is not None:
        for l in lines:
            x1, y1, x2, y2 = l[0]
            cv2.line(mask, (x1, y1), (x2, y2), 255, max(1, int(thickness)))
    return mask > 0, (0 if lines is None else len(lines))


def remove_trails(arr, sensitivity, thickness):
    lum, _ = _luma01(arr)
    mask, n = _trail_mask(lum, sensitivity, thickness)
    if not mask.any():
        return arr.copy(), 0
    m3 = mask
    sigma = max(thickness * 1.5, 4.0)
    out = arr.astype(np.float32).copy()
    valid = (~m3).astype(np.float32)   # weight: exclude trail pixels from the fill

    def fill(ch):
        # Normalized convolution: the local background from non-trail pixels only.
        num = cv2.GaussianBlur(ch * valid, (0, 0), sigma)
        den = cv2.GaussianBlur(valid, (0, 0), sigma)
        filled = num / np.maximum(den, 1e-6)
        return np.where(m3, filled, ch)

    if out.ndim == 3 and out.shape[0] == 3:
        for c in range(3):
            out[c] = fill(out[c])
    elif out.ndim == 3 and out.shape[-1] == 3:
        for c in range(3):
            out[..., c] = fill(out[..., c])
    else:
        out = fill(out)
    return out.astype(arr.dtype), n


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

    dlg = poe.dialog("Satellite Trail Removal")
    dlg.credits("Automatic streak remover for polarispy (OpenCV Hough + inpaint).\nInspired by siril-scripts Satellite_Trail_Removal. GPL-3.0-or-later.")
    dlg.info("Remove satellite / plane trails from: %s" % os.path.basename(path))
    dlg.slider("sensitivity", "Detection sensitivity", 0.0, 1.0, 0.5, step=0.05)
    dlg.number("thickness", "Trail thickness (px)", default=7, min=1, max=25, step=1)

    _cache = {}

    def _preview(vals):
        if np is None or cv2 is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        if "arr" not in _cache:
            try:
                d = poe.get_rgb()
                a = np.moveaxis(d.astype(np.float32), -1, 0)
            except polarispy.PolarisError:
                a = poe.get_pixeldata()
                if a is None:
                    raise polarispy.PolarisError("no pixel data")
            step = max(1, int(max(a.shape[-2], a.shape[-1]) / 900.0))
            _cache["arr"] = a[..., ::step, ::step] if a.ndim == 3 else a[::step, ::step]
        out, n = remove_trails(_cache["arr"], vals["sensitivity"], max(2, int(vals["thickness"] / 2)))
        return out

    dlg.preview(_preview)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    if np is None or cv2 is None:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy + OpenCV. Install the scripting "
            "runtime in Settings > Scripts (the 'Install runtime' button).")

    poe.update_progress("Reading pixels", 0.3)
    try:
        rgb = poe.get_rgb()
        arr = np.moveaxis(rgb.astype(np.float32), -1, 0)
        planar = True
    except polarispy.PolarisError:
        arr = poe.get_pixeldata()
        planar = False
    if arr is None:
        poe.log("The frame has no pixel data.")
        return

    poe.update_progress("Detecting + removing trails", 0.6)
    out, n = remove_trails(arr, v["sensitivity"], int(v["thickness"]))
    poe.log("Detected %d line segment(s)." % n)

    poe.update_progress("Writing result", 0.9)
    written = poe.set_pixeldata(out.astype("float32") if planar else out,
                                out_path=os.path.splitext(path)[0] + "_notrail.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
