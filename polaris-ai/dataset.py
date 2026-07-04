"""PyTorch dataset that turns sharp tiles into (blurred+sigma, sharp) pairs on
the fly via the synthetic forward model in ``synth.py``.

Sharp tiles live under a directory as ``.npy`` (preferred), ``.fits`` or 8/16-bit
images. Each is assumed to be a single-channel, **linear**, roughly-normalised
([0,1]) tile (the prep step in ``download.py`` writes them that way).
"""
from __future__ import annotations

import glob
import os

import numpy as np
import torch
from torch.utils.data import Dataset

import synth


def _load_tile(path: str) -> np.ndarray:
    ext = os.path.splitext(path)[1].lower()
    if ext == ".npy":
        a = np.load(path)
    elif ext in (".fits", ".fit", ".fts"):
        from astropy.io import fits

        with fits.open(path, memmap=False) as hdul:
            a = hdul[0].data.astype(np.float32)
    else:
        from PIL import Image

        a = np.asarray(Image.open(path).convert("F"), dtype=np.float32)
        a /= 65535.0 if a.max() > 1.5 else 1.0
    a = np.asarray(a, dtype=np.float32)
    if a.ndim == 3:                       # collapse colour to luminance
        a = a.mean(axis=2 if a.shape[2] <= 4 else 0)
    return a


def _robust_norm(a: np.ndarray) -> np.ndarray:
    """Map to ~[0,1] using a robust low/high percentile (astro data is linear
    with a small bright tail)."""
    lo = np.percentile(a, 1.0)
    hi = np.percentile(a, 99.9)
    if hi <= lo:
        hi = lo + 1.0
    return np.clip((a - lo) / (hi - lo), 0.0, 1.0).astype(np.float32)


# --- GraXpert-style log-domain normalization (per tile) -----------------------
# The percentile normalization above maps a per-tile 1st..99.9th window to
# [0,1]. On a tile that contains a SATURATED star (value clipped at the frame
# max) that window is dominated by the bright tail, and the linear map leaves
# the star core pinned at 1.0 with a steep shoulder -- exactly where the model
# tended to carve a dark ring ("bubble"). GraXpert's deconvolution instead
# normalizes in the LOG domain: subtract the tile min, log-compress, then
# zero-mean/unit-std scale by 0.1. Log compression flattens the huge dynamic
# range between a saturated core and the sky, so the network never sees the
# violent shoulder that drove the overshoot. See GraXpert deconvolution.py.
#
# IMPORTANT: at inference we only have the INPUT tile, so the (min, mean, std)
# params MUST be derived from the input (blurred) image and the SAME params are
# applied to the target during training. This mirrors the inference path in
# onnx-pipelines.js (DeconPipeline log-norm branch) so the trained model is a
# drop-in. The model predicts the corrected image directly (our `out = img +
# delta` convention), NOT GraXpert's separate residual, so de-normalization is a
# single exp() with no input subtraction.
LOG_EPS = 1e-5
LOG_SCALE = 0.1


def log_norm_pair(x: np.ndarray, y: np.ndarray):
    """Convert a (blurred+sigma, sharp) pair from linear ~[0,1] to the
    GraXpert log-mean-std domain. ``x`` is [2,H,W] (image + sigma), ``y`` is
    [1,H,W]. Returns the same shapes; only the image channel of ``x`` and all
    of ``y`` are transformed (the sigma channel is left untouched)."""
    img = x[0]
    mn = float(img.min())
    t = np.log(np.clip(img - mn, 0.0, None) + LOG_EPS)
    mean = float(t.mean())
    std = float(t.std())
    if std < 1e-8:
        std = 1e-8
    xn = ((t - mean) / std * LOG_SCALE).astype(np.float32)
    # Target with the SAME params (from the input). Clip the pre-log argument so
    # target pixels below the input min don't produce NaNs.
    ty = np.log(np.clip(y[0] - mn, 0.0, None) + LOG_EPS)
    yn = ((ty - mean) / std * LOG_SCALE).astype(np.float32)
    xo = x.copy()
    xo[0] = xn
    return xo, yn[None, :, :]


class DeconDataset(Dataset):
    def __init__(self, tiles_dir: str, tile: int = 256, augment: bool = True,
                 normalize: bool = True, seed: int = 0, log_norm: bool = False,
                 flux_aug: bool = False, noise_matched: bool = False,
                 noise_match_alpha: float = 1.0):
        self.paths = sorted(
            p for ext in ("npy", "fits", "fit", "fts", "png", "tif", "tiff")
            for p in glob.glob(os.path.join(tiles_dir, f"**/*.{ext}"), recursive=True)
        )
        if not self.paths:
            raise FileNotFoundError(f"no tiles found under {tiles_dir}")
        self.tile = tile
        self.augment = augment
        self.normalize = normalize
        self.base_seed = seed
        self.log_norm = log_norm
        self.flux_aug = flux_aug
        self.noise_matched = noise_matched
        self.noise_match_alpha = noise_match_alpha

    def __len__(self):
        return len(self.paths)

    def __getitem__(self, idx):
        rng = np.random.default_rng(self.base_seed * 1_000_003 + idx
                                    + int(torch.initial_seed() % 2_147_483_647))
        a = _load_tile(self.paths[idx])
        if self.normalize:
            a = _robust_norm(a)

        # random crop to tile size (pad small tiles)
        t = self.tile
        h, w = a.shape
        if h < t or w < t:
            a = np.pad(a, ((0, max(0, t - h)), (0, max(0, t - w))), mode="reflect")
            h, w = a.shape
        y0 = int(rng.integers(0, h - t + 1))
        x0 = int(rng.integers(0, w - t + 1))
        sharp = np.ascontiguousarray(a[y0:y0 + t, x0:x0 + t])

        if self.augment:
            if rng.random() < 0.5:
                sharp = sharp[:, ::-1]
            if rng.random() < 0.5:
                sharp = sharp[::-1, :]
            k = int(rng.integers(0, 4))
            if k:
                sharp = np.rot90(sharp, k)
            sharp = np.ascontiguousarray(sharp)

        # AIIMP flux augmentation: random exposure gain BEFORE the forward
        # model. Gains > 1 clip more star cores at 1.0, varying the
        # saturated-core morphology (the dark-ring failure surface); < 1
        # deepens the faint end. The per-tile log/percentile normalization is
        # gain-invariant for the unclipped part, so this specifically
        # exercises saturation, not brightness.
        if self.flux_aug:
            g = float(np.exp(rng.uniform(np.log(0.5), np.log(2.0))))
            sharp = np.clip(sharp * g, 0.0, 1.0).astype(np.float32)

        x, y, _ = synth.make_pair(sharp, rng, noise_matched=self.noise_matched,
                                  noise_match_alpha=self.noise_match_alpha)
        if self.log_norm:
            x, y = log_norm_pair(x, y)
        return torch.from_numpy(x), torch.from_numpy(y)
