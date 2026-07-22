# polaris: name=Autocrop; icon=✂️; scope=frame
"""Autocrop the black borders left by a framing=max / mosaic stack (Autocrop port).

Builds a content mask (OpenCV threshold + morphology) and finds the largest
axis-aligned rectangle fully inside it, then crops the frame to it - removing the
ragged black borders a max-framing integration leaves. Run on a stacked /
registered image. Writes the cropped frame next to the source. Needs numpy +
astropy + OpenCV on the host (install the scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Original: Autocrop for Siril by Gottfried Rotter (2025). Reimplemented against
polarispy: the PyQt6 GUI is replaced by a polarispy.Dialog; the largest interior
rectangle is computed with a numpy maximal-rectangle pass (no numba lir dep).
"""

import os

import polarispy

try:
    import numpy as np
    import cv2
except ImportError:
    np = None
    cv2 = None


def _luma(arr):
    """Luminance plane (H, W) float in [0, 1] from a mono / colour array."""
    a = arr.astype(np.float32)
    mx = float(np.nanmax(a)) if a.size else 1.0
    if mx > 1.5:
        a = a / mx
    if a.ndim == 3 and a.shape[0] == 3:
        return 0.2126 * a[0] + 0.7152 * a[1] + 0.0722 * a[2]
    if a.ndim == 3 and a.shape[-1] == 3:
        return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]
    return a


def _content_mask(luma, pct):
    """Boolean mask of the content region (True = keep) using OpenCV cleanup."""
    lo, hi = float(np.percentile(luma, 1)), float(np.percentile(luma, 99.5))
    stretched = np.clip((luma - lo) / max(1e-6, hi - lo), 0.0, 1.0)
    thr = max(0.0, float(np.percentile(stretched, pct)) - 0.05)
    m = (stretched > thr).astype(np.uint8) * 255
    m = cv2.GaussianBlur(m, (5, 5), 0)
    _, m = cv2.threshold(m, 25, 255, cv2.THRESH_BINARY)
    k = np.ones((5, 5), np.uint8)
    m = cv2.dilate(m, k, iterations=1)
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, k)
    return m > 0


def _largest_interior_rect(mask):
    """Largest all-True axis-aligned rectangle. Returns (x, y, w, h)."""
    H, W = mask.shape
    heights = np.zeros(W, dtype=np.int32)
    best_area, best = 0, (0, 0, W, H)
    for r in range(H):
        heights = np.where(mask[r], heights + 1, 0)
        stack = []  # (start_col, height), increasing heights
        for c in range(W + 1):
            cur = int(heights[c]) if c < W else 0
            start = c
            while stack and stack[-1][1] > cur:
                s_c, s_h = stack.pop()
                area = s_h * (c - s_c)
                if area > best_area:
                    best_area = area
                    best = (s_c, r - s_h + 1, c - s_c, s_h)
                start = s_c
            if not stack or stack[-1][1] < cur:
                stack.append((start, cur))
    return best


def _crop(arr, box):
    x, y, w, h = box
    if arr.ndim == 3 and arr.shape[0] == 3:
        return arr[:, y:y + h, x:x + w]
    return arr[y:y + h, x:x + w]


def compute_box(arr, pct, downscale=4):
    """Find the crop box (x, y, w, h) in full-res coords."""
    luma = _luma(arr)
    mask = _content_mask(luma, pct)
    # LIR on a downscaled mask for speed, then scale the box back up.
    small = mask[::downscale, ::downscale]
    x, y, w, h = _largest_interior_rect(small)
    H, W = mask.shape
    X, Y = x * downscale, y * downscale
    return (X, Y, min(w * downscale, W - X), min(h * downscale, H - Y))


def main():
    poe = polarispy.connect()
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=1)
        if not frames:
            poe.log("No frame. Open a stacked image in STUDIO.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("Autocrop")
    dlg.credits("Autocrop for Siril by Gottfried Rotter (2025) - GPL-3.0-or-later.\nPorted to polarispy (numpy largest-interior-rectangle).")
    dlg.info("Trim the black borders of: %s" % os.path.basename(path))
    dlg.slider("sensitivity", "Border sensitivity (percentile)", 80.0, 99.0, 95.0, step=1.0)

    _cache = {}

    def _preview(vals):
        if np is None or cv2 is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        if "arr" not in _cache:
            d = poe.get_pixeldata()
            if d is None:
                raise polarispy.PolarisError("no pixel data")
            _cache["arr"] = d
        arr = _cache["arr"]
        box = compute_box(arr, vals["sensitivity"])
        out = _crop(arr, box)
        # Return a downsampled copy for the preview.
        step = max(1, int(max(out.shape[-2], out.shape[-1]) / 720.0))
        return out[..., ::step, ::step] if out.ndim == 3 else out[::step, ::step]

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
    arr = poe.get_pixeldata()
    if arr is None:
        poe.log("The frame has no pixel data.")
        return
    poe.update_progress("Finding crop box", 0.6)
    box = compute_box(arr, v["sensitivity"])
    x, y, w, h = box
    poe.log("Crop box: x=%d y=%d w=%d h=%d (from %dx%d)" % (x, y, w, h, arr.shape[-1], arr.shape[-2]))
    out = np.ascontiguousarray(_crop(arr, box))

    poe.update_progress("Writing result", 0.9)
    written = poe.set_pixeldata(out, out_path=os.path.splitext(path)[0] + "_crop.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The cropped frame appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
