"""Synthetic degradation forward model: turn a sharp tile into a realistic
blurred + noisy observation, plus the condition value the model is told about.

    y = noise( PSF(fwhm) ⊛ x )

We control the PSF, so (x, y) is a perfect supervised pair. The model is given a
normalised ``sigma`` channel so a single network can undo a range of seeing.
"""
from __future__ import annotations

import numpy as np
from scipy.signal import fftconvolve

from psf import gaussian_kernel, moffat_kernel, make_aberrated_psf, FWHM_TO_SIGMA

# FWHM range (pixels) the model is trained to undo. Real survey/CMOS stars are
# often 4-8 px, so the range goes wider than the first synthetic-only model.
# FWHM_MIN is deliberately set BELOW TARGET_FWHM so the training set includes
# inputs that are already as sharp as (or sharper than) the target. Those cases
# teach the model to LEAVE ALREADY-SHARP STARS ALONE (or gently soften an
# over-sharp core) instead of always sharpening -- the previous 1.5>1.3 gap made
# every example a sharpen op, so on real (already-tight) stars the model
# over-deconvolved and carved dark rings ("bubbles"). See diag: out.min < 0.
FWHM_MIN = 1.0
FWHM_MAX = 9.0
SIGMA_NORM = FWHM_MAX  # condition = fwhm / SIGMA_NORM  -> ~[0.11, 1.0]

# The model restores to a small REFERENCE PSF, not to a literal point source.
# Asking it to recover a 1-px delta from a blurred blob is ill-posed (pure
# super-resolution) and makes it hallucinate ringy, fractured stars. Real bright
# stars cannot be pushed to ~1 px without violent ringing, so the reference PSF
# is a GENTLE ~2.2 px sharpen: well-posed, stable, and safe on bright/saturated
# cores (the 1.3 px target was the main driver of the dark-ring artifact).
TARGET_FWHM = 2.2


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


def degrade_with_kernel(sharp, kernel, gain_e_per_adu=1.0, read_noise_e=3.0,
                        full_well_scale=60000.0, rng=None):
    """Like ``degrade`` but with a caller-supplied PSF kernel (e.g. an aberrated
    one from ``make_aberrated_psf``). Same Poisson + read-noise model."""
    rng = rng or np.random.default_rng()
    blurred = np.clip(fftconvolve(sharp, kernel, mode="same"), 0.0, None)
    electrons = blurred * full_well_scale
    shot = rng.poisson(np.clip(electrons, 0, None)).astype(np.float32)
    read = rng.normal(0.0, read_noise_e, size=sharp.shape).astype(np.float32)
    return np.clip((shot + read) / full_well_scale, 0.0, 1.0).astype(np.float32)


def sample_fwhm(rng: np.random.Generator) -> float:
    """Draw a training FWHM, biased slightly toward the smaller (common) end."""
    u = rng.random()
    return float(FWHM_MIN + (FWHM_MAX - FWHM_MIN) * (u ** 1.3))


def condition_value(fwhm: float) -> float:
    """Normalised condition fed to the model as a constant channel."""
    return float(fwhm / SIGMA_NORM)


def _add_hdr_point_stars(sharp: np.ndarray, kernel_peak: float,
                         rng: np.random.Generator) -> np.ndarray:
    """Add synthetic point-source stars at HIGH DYNAMIC RANGE, so that after the
    make_pair PSF convolutions some saturate (clip at 1.0) in BOTH the input and
    the target -- realistic saturated flat-top cores.

    A point of integrated flux F becomes, after convolution:
      input  peak = F * kernel_peak   (seeing PSF, wider  -> lower peak)
      target peak = F * ref_peak      (reference PSF, tighter -> higher peak)
    Both clip at 1.0, so a bright star -> a saturated flat-top in the input and a
    SMALLER/tighter saturated flat-top in the target. This is the exact signal the
    model needs: "shrink the saturated disk, do NOT carve a dark ring around it".
    We draw the desired *observed input peak* p (log-uniform, many > 1 so they
    saturate) and set F = p / kernel_peak so saturation is controlled regardless
    of the sampled seeing PSF.
    """
    h, w = sharp.shape
    out = sharp.copy()
    if rng.random() < 0.15:        # ~15% of tiles stay pure nebula (no added stars)
        return out
    n = int(rng.integers(3, 45))
    kp = max(kernel_peak, 1e-6)
    for _ in range(n):
        y = int(rng.integers(0, h)); x = int(rng.integers(0, w))
        # desired observed (pre-clip) input peak: faint 0.2 -> saturating 6.0
        peak = float(np.exp(rng.uniform(np.log(0.2), np.log(6.0))))
        out[y, x] += peak / kp
    return out


def make_pair(
    sharp: np.ndarray, rng: np.random.Generator, beta_range=(2.2, 4.5)
):
    """Produce one training example from a sharp tile.

    Returns (x, y, c) where:
      x : [2, H, W] float32 -- [degraded image, sigma map]
      y : [1, H, W] float32 -- sharp target
      c : float             -- the normalised condition (for logging)
    """
    fwhm = sample_fwhm(rng)                     # seeing of the INPUT (>= FWHM_MIN)
    beta = float(rng.uniform(*beta_range))
    # ABERRATED PSF: elliptical core (tracking/coma/tilt) + optional diffraction
    # spikes (spider) + optional obstruction halo, so the model learns to round
    # out and clean up real-telescope star shapes, not just circular blur.
    kernel = make_aberrated_psf(rng, fwhm, beta=beta)
    ref = gaussian_kernel(TARGET_FWHM * FWHM_TO_SIGMA)

    # STAR SYNTHESIS: inject HDR point stars (incl. saturating) into the shared
    # scene BEFORE both convolutions, so input and target stay physically
    # consistent and both clip at 1.0 -> the model learns to tighten saturated
    # stars without ringing (the real-image failure mode). Point stars added to
    # `sharp` become PSF-shaped by the convolutions below.
    scene = _add_hdr_point_stars(sharp, float(kernel.max()), rng)

    # DOMAIN RANDOMIZATION: vary SNR widely so the model learns to NOT amplify
    # noise (the failure mode on real data -- it sharpened the noise floor into
    # speckles). Random read noise + full-well (shot noise) per sample.
    read_noise = float(rng.uniform(1.0, 25.0))
    full_well = float(rng.uniform(8000.0, 100000.0))
    deg = degrade_with_kernel(scene, kernel, rng=rng,
                              read_noise_e=read_noise, full_well_scale=full_well)
    # TARGET = the same scene at the GENTLE reference PSF (clean, no noise). This
    # is what makes the restoration well-posed: input(seeing) -> target(ref).
    # When the input fwhm is already <= TARGET_FWHM the target is BLURRIER than
    # the input, so the model learns to leave (or gently soften) sharp stars
    # rather than push them further and ring -- the anti-bubble teaching signal.
    target = np.clip(fftconvolve(scene, ref, mode="same"), 0.0, 1.0).astype(np.float32)
    c = condition_value(fwhm)
    cond = np.full_like(sharp, c, dtype=np.float32)
    x = np.stack([deg, cond], axis=0)          # [2, H, W]
    y = target[None, :, :]                     # [1, H, W]
    return x, y, c
