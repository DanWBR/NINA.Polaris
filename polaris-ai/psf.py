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


def elliptical_moffat_kernel(fwhm, ell=1.0, theta=0.0, beta=3.0, size=None):
    """Elongated Moffat PSF. ``fwhm`` is the geometric-mean FWHM (px); ``ell`` is
    the axis ratio major/minor (>=1, 1.0 = round); ``theta`` the position angle
    (rad). Models non-circular stars from tracking drift, coma, tilt, astigmatism."""
    g = 2.0 * np.sqrt(2.0 ** (1.0 / beta) - 1.0)
    a_maj = (fwhm * np.sqrt(ell)) / g
    a_min = (fwhm / np.sqrt(ell)) / g
    if size is None:
        size = _odd(2 * int(np.ceil(4 * max(a_maj, a_min))) + 1)
    ax = np.arange(size) - size // 2
    xx, yy = np.meshgrid(ax, ax)
    ct, st = np.cos(theta), np.sin(theta)
    xr = xx * ct + yy * st
    yr = -xx * st + yy * ct
    r2 = (xr / a_maj) ** 2 + (yr / a_min) ** 2
    k = (1.0 + r2) ** (-beta)
    return (k / k.sum()).astype(np.float32)


def spike_kernel(size, angles, length, width=0.8):
    """Diffraction-spike pattern (spider vanes). ``angles`` in radians; each makes
    a two-sided streak decaying as exp(-|along|/length) with a thin Gaussian
    cross-section. Returned peak-normalised (caller weights + sums into the PSF)."""
    ax = np.arange(size) - size // 2
    xx, yy = np.meshgrid(ax, ax)
    out = np.zeros((size, size), dtype=np.float64)
    for th in angles:
        dx, dy = np.cos(th), np.sin(th)
        along = xx * dx + yy * dy
        perp = -xx * dy + yy * dx
        out += np.exp(-(perp ** 2) / (2.0 * width ** 2)) * np.exp(-np.abs(along) / length)
    m = out.max()
    return (out / m).astype(np.float32) if m > 0 else out.astype(np.float32)


def make_aberrated_psf(rng, fwhm, beta=3.0):
    """Compose a realistic, randomly-aberrated PSF at the given core FWHM:
    elliptical Moffat core (+ random elongation/angle), optional diffraction
    spikes, optional faint obstruction halo. Returns a flux-normalised kernel."""
    ell = float(min(1.8, 1.0 + abs(rng.normal(0.0, 0.22))))     # mostly round, sometimes elongated
    theta = float(rng.uniform(0, np.pi))
    core = elliptical_moffat_kernel(fwhm, ell=ell, theta=theta, beta=beta)
    size = core.shape[0]
    psf = core.astype(np.float64)

    if rng.random() < 0.35:                                     # diffraction spikes
        nv = rng.choice([4, 6])                                 # 4-vane (X/+) or 3-vane (6 spikes)
        base = float(rng.uniform(0, np.pi))
        step = np.pi / (nv / 2)
        angles = [base + i * step for i in range(nv // 2)]
        length = float(rng.uniform(size * 0.15, size * 0.45))
        spikes = spike_kernel(size, angles, length, width=float(rng.uniform(0.6, 1.2)))
        psf += float(rng.uniform(0.02, 0.18)) * spikes

    if rng.random() < 0.30:                                     # faint wide halo (obstruction)
        halo = gaussian_kernel(fwhm * float(rng.uniform(2.0, 4.0)), size=size)
        psf += float(rng.uniform(0.03, 0.12)) * (halo / halo.max())

    s = psf.sum()
    return (psf / s).astype(np.float32) if s > 0 else psf.astype(np.float32)


# FWHM <-> Gaussian sigma
FWHM_TO_SIGMA = 1.0 / 2.3548200450309493
SIGMA_TO_FWHM = 2.3548200450309493
