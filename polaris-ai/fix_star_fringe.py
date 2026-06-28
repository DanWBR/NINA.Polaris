"""Repair the coloured fringe (blue/magenta block) on bright star cores in an OSC
FITS -- the debayer/saturation colour artifact that channel alignment can't fix.

Approach: build a feathered mask over bright star cores (+ a dilated rim to cover
the fringe), then cap the *excess* blue (B above max(R,G)) and excess red there,
pulling the fringe toward neutral while leaving normal stars and nebula colour
alone. Conservative + masked, so it doesn't desaturate the whole image.

  python fix_star_fringe.py --src aligned.fit --out repaired.fit --preview chk.png
"""
from __future__ import annotations

import argparse
import numpy as np
from astropy.io import fits
from scipy import ndimage


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--preview", default="")
    ap.add_argument("--strength", type=float, default=1.0, help="0..1 fringe removal")
    ap.add_argument("--thr", type=float, default=0.0, help="bright-core lum threshold (0=auto)")
    ap.add_argument("--grow", type=int, default=4, help="dilate core mask (px) to cover the rim")
    args = ap.parse_args()

    with fits.open(args.src, memmap=False) as h:
        data = h[0].data.astype(np.float32); hdr = h[0].header
    rgb = np.transpose(data, (1, 2, 0)) if data.shape[0] == 3 else data[..., :3]
    R, G, B = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    lum = rgb.mean(2)

    thr = args.thr if args.thr > 0 else max(0.30 * float(lum.max()),
                                            float(np.percentile(lum, 99.7)))
    core = lum > thr
    mask = ndimage.binary_dilation(core, iterations=args.grow)
    w = np.clip(ndimage.gaussian_filter(mask.astype(np.float32), 3.0), 0, 1) * args.strength
    print(f"bright-core mask: {int(core.sum())} core px, threshold {thr:.3f}")

    # SCNR-style: in the bright mask cap blue to the GREEN channel, neutralising the
    # blue rim. Green is the cleanest OSC channel (2x Bayer pixels). Warm cores
    # (B<G) are untouched, and we DON'T touch red so legitimately-red stars keep
    # their colour.
    blue_excess = np.clip(B - G, 0, None)
    Bn = B - w * blue_excess
    Rn = R

    out = np.stack([Rn, G, Bn], axis=-1).astype(np.float32)
    fits.writeto(args.out, np.transpose(out, (2, 0, 1)).astype(np.float32),
                 header=hdr, overwrite=True)
    print("wrote", args.out)

    if args.preview:
        from PIL import Image
        lsm = ndimage.gaussian_filter(lum, 1.0)
        y, x = np.unravel_index(np.argmax(lsm * (np.arange(lum.size).reshape(lum.shape) > -1)), lum.shape)
        # pick brightest non-edge
        b = 60
        le = lsm.copy(); le[:b] = 0; le[-b:] = 0; le[:, :b] = 0; le[:, -b:] = 0
        y, x = np.unravel_index(np.argmax(le), le.shape)
        win = 45

        def hard(a):
            bg = np.percentile(a, 50)
            z = np.clip((a - bg) / max(1e-6, np.percentile(a, 99.99) - bg), 0, 1)
            return (np.arcsinh(10000 * z) / np.arcsinh(10000) * 255).astype(np.uint8)

        bef = hard(rgb[y-win:y+win, x-win:x+win])
        aft = hard(out[y-win:y+win, x-win:x+win])
        gap = np.zeros((bef.shape[0], 6, 3), np.uint8)
        panel = np.concatenate([bef, gap, aft], axis=1)
        Image.fromarray(np.kron(panel, np.ones((4, 4, 1), np.uint8))).save(args.preview)
        print(f"wrote {args.preview}  (left: before | right: after)")


if __name__ == "__main__":
    main()
