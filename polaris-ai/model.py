"""ConditionedUNet -- an NPU/quantization-friendly residual U-Net for astro
deconvolution.

Design constraints (so int8/int16/fp16 exports run everywhere without surgery):
  * only Conv2d / BatchNorm2d / ReLU / nearest Upsample / concat / add
  * NO LayerNorm (Hexagon V68 rejects it), NO Inverse / ReduceSumSquare
  * nearest-upsample + 1x1 conv instead of ConvTranspose (no checkerboard)
  * single input tensor: channel 0 = image, channels 1.. = condition map(s)
  * residual output: out = image + delta  (easier to learn, stays well-scaled)

BatchNorm folds into the preceding conv at inference, so it is free on-device and
helps keep activation ranges tight for clean quantization.
"""
from __future__ import annotations

import torch
import torch.nn as nn


def conv_block(cin: int, cout: int) -> nn.Sequential:
    # reflect padding avoids the bright "frame" artifact that zero-padding
    # produces at tile edges (the conv sees a hard 0 border otherwise).
    return nn.Sequential(
        nn.Conv2d(cin, cout, 3, padding=1, padding_mode="reflect", bias=False),
        nn.BatchNorm2d(cout),
        nn.ReLU(inplace=True),
        nn.Conv2d(cout, cout, 3, padding=1, padding_mode="reflect", bias=False),
        nn.BatchNorm2d(cout),
        nn.ReLU(inplace=True),
    )


class Down(nn.Module):
    def __init__(self, cin: int, cout: int):
        super().__init__()
        self.pool = nn.MaxPool2d(2)
        self.block = conv_block(cin, cout)

    def forward(self, x):
        return self.block(self.pool(x))


class Up(nn.Module):
    """Nearest upsample -> 1x1 channel reduce -> concat skip -> conv block."""

    def __init__(self, cin: int, cskip: int, cout: int):
        super().__init__()
        self.up = nn.Upsample(scale_factor=2, mode="nearest")
        self.reduce = nn.Conv2d(cin, cout, 1, bias=False)
        self.block = conv_block(cout + cskip, cout)

    def forward(self, x, skip):
        x = self.reduce(self.up(x))
        x = torch.cat([x, skip], dim=1)
        return self.block(x)


class ConditionedUNet(nn.Module):
    def __init__(self, in_channels: int = 2, base: int = 48, depth: int = 4):
        super().__init__()
        assert depth >= 2
        self.in_channels = in_channels
        chs = [base * (2 ** i) for i in range(depth)]   # e.g. 48,96,192,384
        self.inc = conv_block(in_channels, chs[0])
        self.downs = nn.ModuleList(Down(chs[i], chs[i + 1]) for i in range(depth - 1))
        self.ups = nn.ModuleList(
            Up(chs[i], chs[i - 1], chs[i - 1]) for i in range(depth - 1, 0, -1)
        )
        self.outc = nn.Conv2d(chs[0], 1, 1)

    def forward(self, x):
        img = x[:, :1]                # channel 0 is the image
        skips = [self.inc(x)]
        for down in self.downs:
            skips.append(down(skips[-1]))
        h = skips[-1]
        for i, up in enumerate(self.ups):
            h = up(h, skips[-2 - i])
        return img + self.outc(h)     # residual


if __name__ == "__main__":
    # quick shape sanity check
    m = ConditionedUNet()
    x = torch.randn(2, 2, 256, 256)
    y = m(x)
    n = sum(p.numel() for p in m.parameters())
    print("output", tuple(y.shape), "| params", f"{n/1e6:.2f}M")
