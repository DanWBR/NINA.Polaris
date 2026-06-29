"""Dataset for pre-generated (input, target) tile pairs (BGE + denoise).

Unlike ``DeconDataset`` (which synthesizes degradations on the fly), BGE and
denoise pairs are baked to disk by ``data_prep/`` already in the model's
MAD-normalized domain, so this loader just reads matching ``.npy`` files and
applies cheap geometric augmentation identically to both planes.

Layout::

    <root>/input/<name>.npy    # [C, H, W] float32, normalized model input
    <root>/target/<name>.npy   # [C, H, W] float32, normalized target
"""
from __future__ import annotations

import glob
import os

import numpy as np
import torch
from torch.utils.data import Dataset


class PairedTileDataset(Dataset):
    def __init__(self, root: str, augment: bool = True, tile: int | None = None,
                 seed: int = 0):
        self.in_dir = os.path.join(root, "input")
        self.tgt_dir = os.path.join(root, "target")
        self.names = sorted(
            os.path.basename(p) for p in glob.glob(os.path.join(self.in_dir, "*.npy"))
            if os.path.exists(os.path.join(self.tgt_dir, os.path.basename(p)))
        )
        if not self.names:
            raise FileNotFoundError(f"no paired tiles under {root} (input/+target/)")
        self.augment = augment
        self.tile = tile
        self.base_seed = seed

    def __len__(self):
        return len(self.names)

    def __getitem__(self, idx):
        rng = np.random.default_rng(self.base_seed * 1_000_003 + idx
                                    + int(torch.initial_seed() % 2_147_483_647))
        name = self.names[idx]
        x = np.load(os.path.join(self.in_dir, name)).astype(np.float32)
        y = np.load(os.path.join(self.tgt_dir, name)).astype(np.float32)
        if x.ndim == 2:
            x = x[None]
        if y.ndim == 2:
            y = y[None]

        # optional random square crop (e.g. denoise tiles are already 256²)
        if self.tile and x.shape[1] >= self.tile and x.shape[2] >= self.tile:
            t = self.tile
            y0 = int(rng.integers(0, x.shape[1] - t + 1))
            x0 = int(rng.integers(0, x.shape[2] - t + 1))
            x = x[:, y0:y0 + t, x0:x0 + t]
            y = y[:, y0:y0 + t, x0:x0 + t]

        if self.augment:
            if rng.random() < 0.5:
                x, y = x[:, :, ::-1], y[:, :, ::-1]
            if rng.random() < 0.5:
                x, y = x[:, ::-1, :], y[:, ::-1, :]
            k = int(rng.integers(0, 4))
            if k:
                x, y = np.rot90(x, k, axes=(1, 2)), np.rot90(y, k, axes=(1, 2))
            x, y = np.ascontiguousarray(x), np.ascontiguousarray(y)

        return torch.from_numpy(np.ascontiguousarray(x)), \
            torch.from_numpy(np.ascontiguousarray(y))
