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


class DeconDataset(Dataset):
    def __init__(self, tiles_dir: str, tile: int = 256, augment: bool = True,
                 normalize: bool = True, seed: int = 0):
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

        x, y, _ = synth.make_pair(sharp, rng)
        return torch.from_numpy(x), torch.from_numpy(y)
