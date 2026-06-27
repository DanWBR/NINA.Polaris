"""Synthetic degradation forward model: turn a sharp tile into a realistic
blurred + noisy observation, plus the condition value the model is told about.

    y = noise( PSF(fwhm) ⊛ x )

We control the PSF, so (x, y) is a perfect supervised pair. The model is given a
normalised ``sigma`` channel so a single network can undo a range of seeing.
"""
from __future__ import annotations

import numpy as np
from scipy.signal import fftconvolve

from psf import moffat_kernel

# FWHM range (pixels) the model is trained to undo. The condition channel is the
# FWHM scaled by 1/SIGMA_NORM so it lands in a friendly ~[0,1] range for quant.
FWHM_MIN = 1.5
FWHM_MAX = 6.0
SIGMA_NORM = FWHM_MAX  # condition = fwhm / SIGMA_NORM  -> ~[0.25, 1.0]


def degrade(
    sharp: np.ndarray,
    fwhm: float,
    beta: float = 3.0,
    gain_e_per_adu: float = 1.0,
    read_noise_e: float = 3.0,
    full_well_scale: float = 60000.0,
    rng: np.random.Generator | None = None,
) -> np.ndarray:
    """Blur a linear, ~[0,1] float32 image with a Moffat PSF and add Poisson
    (shot) + Gaussian (read) noise. Returns a float32 image clipped to [0,1].

    The noise is applied in an electron space scaled by ``full_well_scale`` so
    brighter pixels get proportionally less relative noise (true Poisson).
    """
    rng = rng or np.random.default_rng()
    k = moffat_kernel(fwhm, beta)
    blurred = fftconvolve(sharp, k, mode="same")
    blurred = np.clip(blurred, 0.0, None)

    electrons = blurred * full_well_scale
    shot = rng.poisson(np.clip(electrons, 0, None)).astype(np.float32)
    read = rng.normal(0.0, read_noise_e, size=sharp.shape).astype(np.float32)
    noisy = (shot + read) / full_well_scale
    return np.clip(noisy, 0.0, 1.0).astype(np.float32)


def sample_fwhm(rng: np.random.Generator) -> float:
    """Draw a training FWHM, biased slightly toward the smaller (common) end."""
    u = rng.random()
    return float(FWHM_MIN + (FWHM_MAX - FWHM_MIN) * (u ** 1.3))


def condition_value(fwhm: float) -> float:
    """Normalised condition fed to the model as a constant channel."""
    return float(fwhm / SIGMA_NORM)


def make_pair(
    sharp: np.ndarray, rng: np.random.Generator, beta_range=(2.2, 4.5)
):
    """Produce one training example from a sharp tile.

    Returns (x, y, c) where:
      x : [2, H, W] float32 -- [degraded image, sigma map]
      y : [1, H, W] float32 -- sharp target
      c : float             -- the normalised condition (for logging)
    """
    fwhm = sample_fwhm(rng)
    beta = float(rng.uniform(*beta_range))
    deg = degrade(sharp, fwhm, beta=beta, rng=rng)
    c = condition_value(fwhm)
    cond = np.full_like(sharp, c, dtype=np.float32)
    x = np.stack([deg, cond], axis=0)          # [2, H, W]
    y = sharp[None, :, :].astype(np.float32)   # [1, H, W]
    return x, y, c
