# polaris: name=HDR Compression; icon=🌗; scope=frame
"""Multiscale HDR compression (HDR_multiscale port).

Compresses high dynamic range with an à trous wavelet decomposition and a
luminance mask, in Lab space to preserve colour: bright cores are tamed while
faint detail is revealed. Works on a debayered colour image (a raw OSC mosaic
is debayered automatically) or a mono frame. Expects stretched data. Writes the
result next to the source. Needs numpy + astropy + scipy on the host (install
the scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Original: HDR_multiscale for Siril, from Franklin Marek (SAS) code (2025).
Reimplemented against polarispy: the PyQt6 GUI is replaced by a polarispy.Dialog
and the pixels are read / written through polarispy. Core math preserved.
"""

import os

import polarispy

try:
    import numpy as np
    from scipy.ndimage import convolve as _nd_convolve
except ImportError:
    np = None
    _nd_convolve = None


def _b3():
    return np.array([1, 4, 6, 4, 1], dtype=np.float32) / 16.0


def _conv_sep_reflect(image2d, k1d, axis):
    if axis == 1:
        return _nd_convolve(image2d, k1d.reshape(1, -1), mode="reflect")
    return _nd_convolve(image2d, k1d.reshape(-1, 1), mode="reflect")


def _build_spaced_kernel(kernel, scale_idx):
    if scale_idx == 0:
        return kernel.astype(np.float32, copy=False)
    step = 2 ** scale_idx
    spaced = np.zeros(len(kernel) + (len(kernel) - 1) * (step - 1), dtype=np.float32)
    spaced[0::step] = kernel
    return spaced


def _atrous_decompose(img2d, n_scales, base_k):
    current = img2d.astype(np.float32, copy=True)
    scales = []
    for s in range(n_scales):
        k = _build_spaced_kernel(base_k, s)
        smooth = _conv_sep_reflect(_conv_sep_reflect(current, k, axis=1), k, axis=0)
        scales.append(current - smooth)
        current = smooth
    scales.append(current)
    return scales


def _atrous_reconstruct(scales):
    out = scales[-1].astype(np.float32, copy=True)
    for w in scales[:-1]:
        out += w
    return out


def _rgb_to_lab(rgb):
    M = np.array([[0.4124564, 0.3575761, 0.1804375],
                  [0.2126729, 0.7151522, 0.0721750],
                  [0.0193339, 0.1191920, 0.9503041]], dtype=np.float32)
    rgb = np.clip(rgb, 0.0, 1.0).astype(np.float32, copy=False)
    xyz = (rgb.reshape(-1, 3) @ M.T).reshape(rgb.shape)
    xyz[..., 0] /= 0.95047
    xyz[..., 2] /= 1.08883
    delta = 6 / 29

    def f(t):
        return np.where(t > delta ** 3, np.cbrt(t), (t / (3 * delta ** 2)) + (4 / 29))

    fx, fy, fz = f(xyz[..., 0]), f(xyz[..., 1]), f(xyz[..., 2])
    return np.stack([116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)], axis=-1)


def _lab_to_rgb(lab):
    M_inv = np.array([[3.2404542, -1.5371385, -0.4985314],
                      [-0.9692660, 1.8760108, 0.0415560],
                      [0.0556434, -0.2040259, 1.0572252]], dtype=np.float32)
    delta = 6 / 29
    fy = (lab[..., 0] + 16.0) / 116.0
    fx = fy + lab[..., 1] / 500.0
    fz = fy - lab[..., 2] / 200.0

    def finv(t):
        return np.where(t > delta, t ** 3, 3 * delta ** 2 * (t - 4 / 29))

    xyz = np.stack([0.95047 * finv(fx), finv(fy), 1.08883 * finv(fz)], axis=-1)
    rgb = (xyz.reshape(-1, 3) @ M_inv.T).reshape(xyz.shape)
    return np.clip(rgb, 0.0, 1.0).astype(np.float32, copy=False)


def _mask_from_L(L, gamma):
    m = np.clip(L / 100.0, 0.0, 1.0).astype(np.float32)
    return np.power(m, gamma, dtype=np.float32) if gamma != 1.0 else m


def _apply_dim_curve(rgb, gamma):
    return np.power(np.clip(rgb, 0.0, 1.0), gamma, dtype=np.float32)


def _compress_L(L0, n_scales, compression, mask_gamma, decay_rate):
    """à trous HDR compression of an L-like plane (0..100). Returns compressed L."""
    scales = _atrous_decompose(L0, n_scales, _b3())
    mask = _mask_from_L(L0, mask_gamma)
    planes, residual = scales[:-1], scales[-1]
    for i, wp in enumerate(planes):
        scale = (1.0 + (compression - 1.0) * mask * (decay_rate ** i)) * 2.0
        planes[i] = wp * scale
    Lr = _atrous_reconstruct(planes + [residual])
    med0 = float(np.median(L0))
    med1 = float(np.median(Lr)) or 1.0
    return np.clip(Lr * (med0 / med1), 0.0, 100.0)


def compute_hdr(image, n_scales, compression, mask_gamma, decay_rate):
    """image: (H,W,3) or (H,W) float in [0,1]. Returns the compressed image."""
    g = 1.0 + n_scales * 0.2
    if image.ndim == 3:
        lab = _rgb_to_lab(image)
        lab[..., 0] = _compress_L(lab[..., 0].astype(np.float32, copy=True),
                                  n_scales, compression, mask_gamma, decay_rate)
        return _apply_dim_curve(_lab_to_rgb(lab), g)
    L0 = np.clip(image, 0.0, 1.0).astype(np.float32) * 100.0
    out = _compress_L(L0, n_scales, compression, mask_gamma, decay_rate) / 100.0
    return _apply_dim_curve(np.clip(out, 0.0, 1.0), g)


def main():
    poe = polarispy.connect()
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=1)
        if not frames:
            poe.log("No frame. Open a stretched image in STUDIO.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("HDR Compression")
    dlg.expects("stretched")
    dlg.credits("HDR_multiscale for Siril, from Franklin Marek (SAS) code (2025)\nGPL-3.0-or-later. Ported to polarispy.")
    dlg.info("Stretched image: %s" % os.path.basename(path))
    dlg.number("n_scales", "Wavelet scales", default=5, min=2, max=8, step=1)
    dlg.slider("compression", "Compression", 1.0, 4.0, 1.5, step=0.1)
    dlg.slider("mask_gamma", "Mask gamma (protect shadows)", 0.2, 3.0, 1.0, step=0.1)
    dlg.slider("decay", "Scale decay", 0.1, 1.0, 0.5, step=0.05)

    _cache = {}

    def _load():
        try:
            img = poe.get_rgb()  # (H,W,3), debayers a raw OSC mosaic
        except polarispy.PolarisError:
            m = poe.get_pixeldata()
            if m is None or m.ndim != 2:
                raise polarispy.PolarisError("HDR needs a colour or mono frame.")
            img = m
        a = img.astype(np.float32)
        mx = float(np.nanmax(a)) if a.size else 1.0
        white = float(np.iinfo(np.uint16).max) if mx > 1.5 else 1.0
        return np.clip(a / white, 0.0, 1.0)

    def _preview(vals):
        if np is None or _nd_convolve is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        if "img" not in _cache:
            a = _load()
            step = max(1, int(max(a.shape[0], a.shape[1]) / 720.0))
            _cache["img"] = (a[::step, ::step] if a.ndim == 2 else a[::step, ::step, :])
        return compute_hdr(_cache["img"], int(vals["n_scales"]), vals["compression"],
                           vals["mask_gamma"], vals["decay"])

    dlg.preview(_preview)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    if np is None or _nd_convolve is None:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy + scipy. Install the scripting "
            "runtime in Settings > Scripts (the 'Install runtime' button).")

    poe.update_progress("Reading pixels", 0.3)
    img = _load()
    poe.update_progress("Compressing HDR", 0.6)
    out = compute_hdr(img, int(v["n_scales"]), v["compression"], v["mask_gamma"], v["decay"])

    poe.update_progress("Writing result", 0.9)
    data = np.moveaxis(out, -1, 0) if out.ndim == 3 else out
    written = poe.set_pixeldata(data.astype("float32"),
                                out_path=os.path.splitext(path)[0] + "_hdr.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
