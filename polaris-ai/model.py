"""ConditionedUNet -- an NPU/quantization-friendly residual U-Net for astro
deconvolution, now with stackable residual blocks per stage so capacity scales
to GraXpert-class sizes (tens of millions of params / hundreds of MB).

Design constraints (so int8/int16/fp16 exports run everywhere without surgery):
  * only Conv2d / BatchNorm2d / ReLU / MaxPool / nearest Upsample / concat / add
  * NO LayerNorm (Hexagon V68 rejects it), NO Inverse / ReduceSumSquare
  * reflect padding (no bright tile-edge frame), nearest-upsample (no checkerboard)
  * single input tensor: channel 0 = image, channels 1.. = condition map(s)
  * residual output: out = image + delta

Capacity knobs:
  base   -- channels at the top level (channels double each level)
  depth  -- number of resolution levels
  blocks -- residual blocks per stage  (the main capacity dial)

Param count grows ~ base^2 * blocks. Run `python model.py --base 96 --blocks 3`
to print params + fp32 MB for any config before committing to a long train.
"""
from __future__ import annotations

import torch
import torch.nn as nn


def cbr(cin: int, cout: int) -> nn.Sequential:
    """conv(3x3, reflect) -> BN -> ReLU."""
    return nn.Sequential(
        nn.Conv2d(cin, cout, 3, padding=1, padding_mode="reflect", bias=False),
        nn.BatchNorm2d(cout),
        nn.ReLU(inplace=True),
    )


class ResBlock(nn.Module):
    """Pre-activation-free residual block: keeps channel count, adds a learned
    correction. Quantizes cleanly (conv/BN/ReLU/add only)."""

    def __init__(self, c: int):
        super().__init__()
        self.c1 = nn.Conv2d(c, c, 3, padding=1, padding_mode="reflect", bias=False)
        self.b1 = nn.BatchNorm2d(c)
        self.c2 = nn.Conv2d(c, c, 3, padding=1, padding_mode="reflect", bias=False)
        self.b2 = nn.BatchNorm2d(c)
        self.act = nn.ReLU(inplace=True)

    def forward(self, x):
        h = self.act(self.b1(self.c1(x)))
        h = self.b2(self.c2(h))
        return self.act(x + h)


def res_stage(c: int, blocks: int) -> nn.Sequential:
    return nn.Sequential(*[ResBlock(c) for _ in range(blocks)])


class Up(nn.Module):
    """Nearest upsample -> 1x1 channel reduce -> concat skip -> fuse -> res stage."""

    def __init__(self, cin: int, cskip: int, cout: int, blocks: int):
        super().__init__()
        self.up = nn.Upsample(scale_factor=2, mode="nearest")
        self.reduce = nn.Conv2d(cin, cout, 1, bias=False)
        self.fuse = cbr(cout + cskip, cout)
        self.stage = res_stage(cout, blocks)

    def forward(self, x, skip):
        x = self.reduce(self.up(x))
        x = torch.cat([x, skip], dim=1)
        return self.stage(self.fuse(x))


class ConditionedUNet(nn.Module):
    """Residual U-Net used for all three Polaris tasks.

    * decon   : in_channels=2 (image + sigma map), out_channels=1, img_channels=1
    * denoise : in_channels=3 (RGB),               out_channels=3, img_channels=3
    * bge     : in_channels=3 (RGB),               out_channels=3, img_channels=3

    ``img_channels`` is how many leading input channels form the image the
    residual is added back to (``out = img + delta``); it must equal
    ``out_channels``. Defaults keep the original decon contract unchanged.
    """

    def __init__(self, in_channels: int = 2, base: int = 96, depth: int = 4,
                 blocks: int = 3, out_channels: int = 1,
                 img_channels: int | None = None):
        super().__init__()
        assert depth >= 2 and blocks >= 1
        self.in_channels = in_channels
        self.out_channels = out_channels
        self.img_channels = out_channels if img_channels is None else img_channels
        assert self.img_channels == out_channels, "img_channels must equal out_channels"
        chs = [base * (2 ** i) for i in range(depth)]

        self.inc = cbr(in_channels, chs[0])
        self.enc = nn.ModuleList(res_stage(chs[i], blocks) for i in range(depth))
        self.downs = nn.ModuleList(cbr(chs[i], chs[i + 1]) for i in range(depth - 1))
        self.pool = nn.MaxPool2d(2)
        self.ups = nn.ModuleList(
            Up(chs[i], chs[i - 1], chs[i - 1], blocks) for i in range(depth - 1, 0, -1)
        )
        self.outc = nn.Conv2d(chs[0], out_channels, 1)

    def forward(self, x):
        img = x[:, :self.img_channels]
        h = self.inc(x)
        skips = []
        depth = len(self.enc)
        for i in range(depth):
            h = self.enc[i](h)
            if i < depth - 1:
                skips.append(h)
                h = self.downs[i](self.pool(h))
        for j, up in enumerate(self.ups):
            h = up(h, skips[-1 - j])
        return img + self.outc(h)


class UpscaleNet(nn.Module):
    """Pre-upsampling super-resolution: nearest-upscale the LR input by ``scale``,
    then refine with a ConditionedUNet that learns a residual on the upscaled
    image (``HR = upsample(LR) + delta``). Keeps the NPU-safe op set (nearest
    upsample is already used in the decoder) and reuses the whole training/export
    stack -- only the spatial size changes.

    Input  [N, 3, h, w]  ->  output [N, 3, h*scale, w*scale]  (RGB)."""

    def __init__(self, scale: int = 2, base: int = 64, depth: int = 4,
                 blocks: int = 2):
        super().__init__()
        self.scale = scale
        self.up = nn.Upsample(scale_factor=scale, mode="nearest")
        self.net = ConditionedUNet(in_channels=3, base=base, depth=depth,
                                   blocks=blocks, out_channels=3)

    def forward(self, x):
        return self.net(self.up(x))


if __name__ == "__main__":
    import argparse

    ap = argparse.ArgumentParser(description="probe model size for a config")
    ap.add_argument("--base", type=int, default=96)
    ap.add_argument("--depth", type=int, default=4)
    ap.add_argument("--blocks", type=int, default=3)
    ap.add_argument("--in-ch", type=int, default=2)
    ap.add_argument("--out-ch", type=int, default=1)
    a = ap.parse_args()

    m = ConditionedUNet(in_channels=a.in_ch, base=a.base, depth=a.depth,
                        blocks=a.blocks, out_channels=a.out_ch)
    n = sum(p.numel() for p in m.parameters())
    y = m(torch.randn(1, a.in_ch, 256, 256))
    print(f"base={a.base} depth={a.depth} blocks={a.blocks} in={a.in_ch} out={a.out_ch}")
    print(f"  params : {n/1e6:.1f}M")
    print(f"  fp32   : {n*4/1e6:.0f} MB   (fp16 ~{n*2/1e6:.0f} MB, int8 ~{n/1e6:.0f} MB)")
    print(f"  output : {tuple(y.shape)}")
