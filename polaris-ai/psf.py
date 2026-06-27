"""Point-spread-function kernels for the synthetic degradation forward model.

Astronomical seeing is modelled well by a **Moffat** profile (heavier wings than
a Gaussian); a Gaussian is offered for quick experiments and an Airy term for a
diffraction-limited core. All kernels are returned as float32 and normalised to
sum 1 so convolution preserves flux.
"""
from __future__ import annotations

import numpy as np


def _odd(n: int) -> int:
    n = int(n)
    return n + 1 if n % 2 == 0 else n


def gaussian_kernel(sigma: float, size: int | None = None) -> np.ndarray:
    """2-D Gaussian PSF. ``sigma`` in pixels."""
    if size is None:
        size = _odd(2 * int(np.ceil(3 * sigma)) + 1)
    ax = np.arange(size) - size // 2
    xx, yy = np.meshgrid(ax, ax)
    k = np.exp(-(xx ** 2 + yy ** 2) / (2.0 * sigma ** 2))
    return (k / k.sum()).astype(np.float32)


def moffat_kernel(fwhm: float, beta: float = 3.0, size: int | None = None) -> np.ndarray:
    """2-D Moffat PSF. ``fwhm`` in pixels, ``beta`` controls the wings (2.5–4.5
    typical for seeing). Smaller beta = heavier wings."""
    alpha = fwhm / (2.0 * np.sqrt(2.0 ** (1.0 / beta) - 1.0))
    if size is None:
        size = _odd(2 * int(np.ceil(4 * alpha)) + 1)
    ax = np.arange(size) - size // 2
    xx, yy = np.meshgrid(ax, ax)
    r2 = xx ** 2 + yy ** 2
    k = (1.0 + r2 / alpha ** 2) ** (-beta)
    return (k / k.sum()).astype(np.float32)


def airy_kernel(radius: float, size: int | None = None) -> np.ndarray:
    """Airy diffraction pattern. ``radius`` ~ position of the first null in px.
    Useful to add a diffraction-limited core on top of seeing."""
    from scipy.special import j1  # local import; only needed if used

    if size is None:
        size = _odd(2 * int(np.ceil(3 * radius)) + 1)
    ax = np.arange(size) - size // 2
    xx, yy = np.meshgrid(ax, ax)
    r = np.sqrt(xx ** 2 + yy ** 2) + 1e-8
    x = 3.8317 * r / radius  # 3.8317 = first zero of J1
    k = (2.0 * j1(x) / x) ** 2
    return (k / k.sum()).astype(np.float32)


# FWHM <-> Gaussian sigma
FWHM_TO_SIGMA = 1.0 / 2.3548200450309493
SIGMA_TO_FWHM = 2.3548200450309493
