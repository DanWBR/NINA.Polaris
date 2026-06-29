"""Shared helpers for building Polaris's own BGE / denoise / deconvolution
training data from the hand-curated linear RGB FITS in ``data/own/raw``.

The math here is deliberately kept identical to how Polaris consumes the models
at inference time (``src/NINA.Polaris/wwwroot/js/onnx-pipelines.js``) so a model
trained on these tiles is a drop-in replacement:

  * BGE / denoise run in a **per-channel MAD-normalized** domain:
        v' = (v - median) / MAD * 0.04
    (clip is applied per task), and the model predicts in that same domain.
  * BGE predicts the **background plane** on the whole frame downsampled to 256².
  * Denoise predicts the clean image, tiled at 256².
  * Decon stays luminance + a sigma condition channel (handled by ``synth.py``).

The user's FITS are ``(3, H, W)`` float32 already scaled to ~[0, 1] linear, so we
work in that space directly (no /65535 step the browser needs for uint16).
"""
from __future__ import annotations

import os
import sys

import numpy as np

# Make the polaris-ai root importable (synth, psf, model) when run as a script.
_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)

# Normalization scale used by the GraXpert/Polaris pipelines. Keep in sync with
# the `* 0.04` in onnx-pipelines.js (BGE + denoise).
MAD_SCALE = 0.04


# --------------------------------------------------------------------------- #
# FITS I/O  (plane-sequential (3, H, W), the FITSReader / repo convention)
# --------------------------------------------------------------------------- #
def load_fits(path: str) -> np.ndarray:
    """Load a FITS file as float32. Returns ``(3, H, W)`` for colour or
    ``(H, W)`` for mono, byte-order normalized to native."""
    from astropy.io import fits

    with fits.open(path, memmap=False) as hdul:
        a = np.ascontiguousarray(hdul[0].data).astype(np.float32)
    return a


def load_fits_rgb(path: str) -> np.ndarray:
    """Load and coerce to ``(3, H, W)`` float32. Mono is replicated to 3 planes;
    a trailing-channel ``(H, W, 3)`` is transposed to plane-sequential."""
    a = load_fits(path)
    if a.ndim == 2:
        a = np.stack([a, a, a], axis=0)
    elif a.ndim == 3 and a.shape[0] not in (3, 4) and a.shape[2] in (3, 4):
        a = np.transpose(a, (2, 0, 1))
    if a.shape[0] >= 3:
        a = a[:3]
    else:                                   # 1 or 2 planes -> replicate plane 0
        a = np.stack([a[0]] * 3, axis=0)
    return np.ascontiguousarray(a.astype(np.float32))


def save_fits_rgb(path: str, arr: np.ndarray) -> None:
    """Write ``(3, H, W)`` (or ``(H, W)``) float32 to a FITS file."""
    from astropy.io import fits

    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    fits.writeto(path, np.asarray(arr, dtype=np.float32), overwrite=True)


def to_luminance(rgb: np.ndarray) -> np.ndarray:
    """``(3, H, W)`` -> ``(H, W)`` mean luminance (matches dataset._load_tile)."""
    if rgb.ndim == 2:
        return rgb.astype(np.float32)
    return rgb.mean(axis=0).astype(np.float32)


# --------------------------------------------------------------------------- #
# Per-channel MAD normalize / denormalize  (mirror onnx-pipelines.js)
# --------------------------------------------------------------------------- #
def median_mad(plane: np.ndarray) -> tuple[float, float]:
    """Robust centre + scale of a 2-D plane. MAD floored at 1e-6 like the JS."""
    med = float(np.median(plane))
    mad = float(np.median(np.abs(plane - med)))
    return med, (mad if mad > 1e-6 else 1e-6)


def mad_normalize(plane: np.ndarray, med: float, mad: float) -> np.ndarray:
    return ((plane - med) / mad * MAD_SCALE).astype(np.float32)


def mad_denormalize(plane: np.ndarray, med: float, mad: float) -> np.ndarray:
    return (plane * mad / MAD_SCALE + med).astype(np.float32)


def normalize_rgb(rgb: np.ndarray, stats: list[tuple[float, float]] | None = None,
                  clip: float | None = None):
    """Per-channel MAD-normalize a ``(3, H, W)`` image. If ``stats`` is given
    (list of (median, mad) per channel) those are reused (so an input and its
    target share one normalization); otherwise stats are measured here.
    Returns ``(normalized (3,H,W), stats)``."""
    out = np.empty_like(rgb, dtype=np.float32)
    if stats is None:
        stats = [median_mad(rgb[c]) for c in range(rgb.shape[0])]
    for c in range(rgb.shape[0]):
        med, mad = stats[c]
        v = mad_normalize(rgb[c], med, mad)
        if clip is not None:
            v = np.clip(v, -clip, clip)
        out[c] = v
    return out, stats


# --------------------------------------------------------------------------- #
# Resampling
# --------------------------------------------------------------------------- #
def downsample_rgb(rgb: np.ndarray, size: int = 256) -> np.ndarray:
    """Bilinear-resize each plane of ``(3, H, W)`` to ``(3, size, size)``
    (BGE runs on the whole frame downsampled to 256²)."""
    from scipy.ndimage import zoom

    c, h, w = rgb.shape
    zy, zx = size / h, size / w
    out = np.empty((c, size, size), dtype=np.float32)
    for i in range(c):
        out[i] = zoom(rgb[i], (zy, zx), order=1).astype(np.float32)[:size, :size]
    return out


# --------------------------------------------------------------------------- #
# Dense tiling (reflect-pad short frames), shared coords for paired images
# --------------------------------------------------------------------------- #
def tile_coords(h: int, w: int, tile: int = 256, stride: int = 192):
    """Yield ``(y0, x0)`` top-left corners covering an HxW frame, last row/col
    snapped to the edge so the whole frame is covered."""
    ys = list(range(0, max(1, h - tile + 1), stride))
    xs = list(range(0, max(1, w - tile + 1), stride))
    if ys[-1] != h - tile:
        ys.append(max(0, h - tile))
    if xs[-1] != w - tile:
        xs.append(max(0, w - tile))
    for y0 in ys:
        for x0 in xs:
            yield y0, x0


def pad_to_tile(rgb: np.ndarray, tile: int) -> np.ndarray:
    c, h, w = rgb.shape
    if h >= tile and w >= tile:
        return rgb
    return np.pad(rgb, ((0, 0), (0, max(0, tile - h)), (0, max(0, tile - w))),
                  mode="reflect")


def is_mostly_empty(tile_plane: np.ndarray, max_thr: float = 0.02,
                    std_thr: float = 0.0015) -> bool:
    """Skip near-blank tiles (port of download.py ingest's filter, scaled for
    the dark [0,1] linear data here -- median background ~0.008)."""
    return float(tile_plane.max()) < max_thr or float(tile_plane.std()) < std_thr


# --------------------------------------------------------------------------- #
# Synthetic degradations
# --------------------------------------------------------------------------- #
def add_sensor_noise(plane: np.ndarray, rng: np.random.Generator,
                     read_noise_e: float = 5.0,
                     full_well_scale: float = 40000.0) -> np.ndarray:
    """Poisson (shot) + Gaussian (read) noise on a linear ~[0,1] plane, NO blur
    (denoise target stays sharp). Same electron-space model as synth.degrade."""
    electrons = np.clip(plane, 0.0, None) * full_well_scale
    shot = rng.poisson(np.clip(electrons, 0, None)).astype(np.float32)
    read = rng.normal(0.0, read_noise_e, size=plane.shape).astype(np.float32)
    return np.clip((shot + read) / full_well_scale, 0.0, 1.0).astype(np.float32)


def add_sensor_noise_rgb(rgb: np.ndarray, rng: np.random.Generator,
                         read_noise_e: float | None = None,
                         full_well_scale: float | None = None) -> np.ndarray:
    """Per-channel noise with one randomized (read-noise, full-well) draw per
    image when not given (domain randomization)."""
    # Real residual noise on these (already-clean) masters measures ~0.0001-0.0002
    # std on the background; this range spans from ~that up to clearly heavier
    # single-sub noise so the denoiser generalizes across stack depths.
    rn = float(rng.uniform(2.0, 25.0)) if read_noise_e is None else read_noise_e
    fw = float(rng.uniform(8000.0, 120000.0)) if full_well_scale is None else full_well_scale
    out = np.empty_like(rgb, dtype=np.float32)
    for c in range(rgb.shape[0]):
        out[c] = add_sensor_noise(rgb[c], rng, rn, fw)
    return out


def synth_gradient(h: int, w: int, rng: np.random.Generator,
                   amp: float) -> np.ndarray:
    """A smooth low-order background plane (light pollution / amp glow / vignette)
    over an HxW grid, peak-to-peak roughly ``amp``. Combines a random low-order
    2-D polynomial with an optional off-centre radial term."""
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    yy = (yy / max(1, h - 1)) * 2.0 - 1.0          # [-1, 1]
    xx = (xx / max(1, w - 1)) * 2.0 - 1.0
    # random plane + quadratic curvature
    a = rng.uniform(-1.0, 1.0)
    b = rng.uniform(-1.0, 1.0)
    cxx = rng.uniform(-0.6, 0.6)
    cyy = rng.uniform(-0.6, 0.6)
    cxy = rng.uniform(-0.4, 0.4)
    g = a * xx + b * yy + cxx * xx * xx + cyy * yy * yy + cxy * xx * yy
    # optional radial vignetting / amp glow from a random corner
    if rng.random() < 0.7:
        ox, oy = rng.uniform(-1.0, 1.0), rng.uniform(-1.0, 1.0)
        r2 = (xx - ox) ** 2 + (yy - oy) ** 2
        g = g + rng.uniform(-1.0, 1.0) * np.exp(-r2 / rng.uniform(0.5, 3.0))
    g = g - g.min()
    pk = float(g.max())
    if pk > 1e-6:
        g = g / pk
    return (g * amp).astype(np.float32)


def add_gradient_rgb(clean_bg: np.ndarray, rng: np.random.Generator):
    """Add a per-channel smooth gradient to a (mostly) background-free image.

    Returns ``(with_gradient (3,H,W), gradient_plane (3,H,W))`` -- the gradient
    plane is the BGE training **target** (what the model must predict)."""
    c, h, w = clean_bg.shape
    grad = np.empty_like(clean_bg, dtype=np.float32)
    out = np.empty_like(clean_bg, dtype=np.float32)
    for ch in range(c):
        med = float(np.median(clean_bg[ch]))
        # gradient amplitude a few× the channel background, randomized
        amp = max(1e-4, med) * float(rng.uniform(0.5, 6.0))
        g = synth_gradient(h, w, rng, amp)
        grad[ch] = g
        out[ch] = np.clip(clean_bg[ch] + g, 0.0, 1.0)
    return out, grad


# --------------------------------------------------------------------------- #
# Tile writers
# --------------------------------------------------------------------------- #
def save_pair_tile(input_dir: str, target_dir: str, name: str,
                   x: np.ndarray, y: np.ndarray) -> None:
    """Write a matching (input, target) .npy pair under input_dir/target_dir."""
    os.makedirs(input_dir, exist_ok=True)
    os.makedirs(target_dir, exist_ok=True)
    np.save(os.path.join(input_dir, name + ".npy"), x.astype(np.float32))
    np.save(os.path.join(target_dir, name + ".npy"), y.astype(np.float32))


def basename_no_ext(path: str) -> str:
    return os.path.splitext(os.path.basename(path))[0]
