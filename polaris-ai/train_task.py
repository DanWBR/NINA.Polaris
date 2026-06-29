"""Unified trainer for Polaris's three models (fp32).

Reuses the decon training loop/optimizer (AdamW + cosine + AMP + best/last
checkpoints) and the loss helpers from ``train.py``; picks the dataset, channel
count and loss mix per task:

  * decon   : DeconDataset (synth forward model), in=2 out=1,
              Charbonnier + grad + star-protect (unchanged from train.py)
  * denoise : PairedTileDataset (pre-baked pairs), in=3 out=3,
              Charbonnier + small grad
  * bge     : PairedTileDataset (pre-baked pairs), in=3 out=3,
              Charbonnier (target is a smooth background plane)

Examples:
  python train_task.py --task denoise --pairs data/own/denoise_tiles \
      --val-pairs data/own/denoise_val --epochs 80 --out checkpoints/denoise
  python train_task.py --task bge --pairs data/own/bge_tiles \
      --val-pairs data/own/bge_val --epochs 120 --out checkpoints/bge
  python train_task.py --task decon --tiles data/own/decon_tiles \
      --out checkpoints/decon
"""
from __future__ import annotations

import argparse
import os

import torch
from torch.utils.data import ConcatDataset, DataLoader, random_split
from tqdm import tqdm

from model import ConditionedUNet, UpscaleNet
from train import charbonnier, grad_loss, star_protect

TASKS = {
    "decon":   {"in": 2, "out": 1},
    "denoise": {"in": 3, "out": 3},
    "bge":     {"in": 3, "out": 3},
    "upscale": {"in": 3, "out": 3},
    "halo":    {"in": 3, "out": 3},
}


def build_dataset(args):
    """Return (train_ds, val_ds_or_None) for the chosen task."""
    if args.task == "decon":
        from dataset import DeconDataset
        extra = []
        if args.tiles2:
            extra.append(DeconDataset(args.tiles2, tile=args.tile))
        tr = DeconDataset(args.tiles, tile=args.tile)
        full = ConcatDataset([tr] + extra) if extra else tr
        val = DeconDataset(args.val_tiles, tile=args.tile, augment=False) \
            if args.val_tiles else None
        return full, val
    # denoise / bge: pre-baked pairs
    from paired_dataset import PairedTileDataset
    tr = PairedTileDataset(args.pairs, augment=True, tile=args.crop or None)
    val = PairedTileDataset(args.val_pairs, augment=False) if args.val_pairs else None
    return tr, val


def task_loss(task, p, y, w_grad, w_star):
    # halo removal protects bright cores (star_protect) so it only touches the
    # faint halo, like decon.
    if task in ("decon", "halo"):
        return charbonnier(p, y) + w_grad * grad_loss(p, y) + w_star * star_protect(p, y)
    if task in ("denoise", "upscale"):
        return charbonnier(p, y) + w_grad * grad_loss(p, y)
    return charbonnier(p, y)  # bge: smooth background, plain robust L1


def make_net(args, spec):
    if args.task == "upscale":
        return UpscaleNet(scale=args.scale, base=args.base, depth=args.depth,
                          blocks=args.blocks)
    return ConditionedUNet(in_channels=spec["in"], base=args.base, depth=args.depth,
                           blocks=args.blocks, out_channels=spec["out"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--task", required=True, choices=list(TASKS))
    # decon inputs
    ap.add_argument("--tiles", default="", help="(decon) sharp-tile dir for DeconDataset")
    ap.add_argument("--tiles2", default="", help="(decon) optional extra sharp-tile dir")
    ap.add_argument("--val-tiles", default="", help="(decon) optional val sharp-tile dir")
    # denoise/bge inputs
    ap.add_argument("--pairs", default="", help="(denoise/bge) paired-tile root (input/+target/)")
    ap.add_argument("--val-pairs", default="", help="(denoise/bge) val paired-tile root")
    ap.add_argument("--crop", type=int, default=0, help="(denoise/bge) random crop size, 0=off")
    # common
    ap.add_argument("--out", default="")
    ap.add_argument("--epochs", type=int, default=80)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--tile", type=int, default=256)
    ap.add_argument("--lr", type=float, default=2e-4)
    ap.add_argument("--base", type=int, default=96)
    ap.add_argument("--depth", type=int, default=4)
    ap.add_argument("--blocks", type=int, default=3)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--val-frac", type=float, default=0.05)
    ap.add_argument("--w-grad", type=float, default=0.5)
    ap.add_argument("--w-star", type=float, default=0.25)
    ap.add_argument("--resume", default="")
    ap.add_argument("--qat", action="store_true",
                    help="quantization-aware training: STE fake-quant so int8/int16 "
                         "export is near-lossless (best fine-tuned from an fp32 --resume)")
    ap.add_argument("--qat-bits", type=int, default=8, choices=[8, 16])
    ap.add_argument("--scale", type=int, default=2, choices=[2, 3, 4],
                    help="(upscale) super-resolution factor")
    args = ap.parse_args()

    out = args.out or f"checkpoints/{args.task}"
    os.makedirs(out, exist_ok=True)
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    print("device:", dev, "| task:", args.task)

    full, val_ds = build_dataset(args)
    if val_ds is None:
        n_val = max(1, int(len(full) * args.val_frac))
        tr, va = random_split(full, [len(full) - n_val, n_val],
                              generator=torch.Generator().manual_seed(42))
    else:
        tr, va = full, val_ds
    tl = DataLoader(tr, batch_size=args.batch, shuffle=True, num_workers=args.workers,
                    pin_memory=(dev == "cuda"), drop_last=True)
    vl = DataLoader(va, batch_size=args.batch, shuffle=False, num_workers=args.workers,
                    pin_memory=(dev == "cuda"))
    print(f"samples: train {len(tr)} | val {len(va)}")

    spec = TASKS[args.task]
    net = make_net(args, spec).to(dev)
    nparams = sum(p.numel() for p in net.parameters())
    print(f"model: base={args.base} depth={args.depth} blocks={args.blocks} "
          f"in={spec['in']} out={spec['out']} -> {nparams/1e6:.1f}M params "
          f"(~{nparams*4/1e6:.0f} MB fp32)")
    if args.resume and os.path.isfile(args.resume):
        net.load_state_dict(torch.load(args.resume, map_location=dev))
        print("resumed from", args.resume)

    # QAT: insert STE fake-quant (after loading fp32 weights, so it fine-tunes).
    # AMP is disabled under QAT for numerical stability.
    if args.qat:
        from quant_layers import apply_qat, bake_qat
        apply_qat(net, args.qat_bits)
        net.to(dev)   # move the newly-added activation observers onto the device
        print(f"QAT enabled: STE fake-quant, {args.qat_bits}-bit "
              f"(per-channel weights, per-tensor activations)")
    amp_on = (dev == "cuda" and not args.qat)

    opt = torch.optim.AdamW(net.parameters(), lr=args.lr, weight_decay=1e-5)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
    scaler = torch.amp.GradScaler("cuda", enabled=amp_on)

    best = float("inf")
    for ep in range(args.epochs):
        net.train()
        run = 0.0
        for x, y in tqdm(tl, desc=f"epoch {ep+1}/{args.epochs}"):
            x, y = x.to(dev), y.to(dev)
            opt.zero_grad(set_to_none=True)
            with torch.amp.autocast("cuda", enabled=amp_on):
                p = net(x)
                loss = task_loss(args.task, p, y, args.w_grad, args.w_star)
            scaler.scale(loss).backward()
            scaler.step(opt)
            scaler.update()
            run += loss.item()
        sched.step()

        net.eval()
        vrun = 0.0
        with torch.no_grad():
            for x, y in vl:
                x, y = x.to(dev), y.to(dev)
                vrun += charbonnier(net(x), y).item()
        vloss = vrun / max(1, len(vl))
        print(f"  train {run/max(1,len(tl)):.5f} | val {vloss:.5f} "
              f"| lr {sched.get_last_lr()[0]:.2e}")

        # Under QAT the live model carries parametrizations/observers, so its
        # state_dict won't load into a plain net. Save those to *_qat.pt for
        # resume; the export-ready (baked) best.pt is written after training.
        best_name = "best_qat.pt" if args.qat else "best.pt"
        torch.save(net.state_dict(), os.path.join(out, "last_qat.pt" if args.qat else "last.pt"))
        if vloss < best:
            best = vloss
            torch.save(net.state_dict(), os.path.join(out, best_name))
            print(f"  -> new best, saved {best_name}")

    if args.qat:
        # Reload best, bake the rounded weights into plain Conv2d.weight, drop the
        # observers/hooks, and save a clean best.pt that export.py loads unchanged.
        from quant_layers import bake_qat
        bp = os.path.join(out, "best_qat.pt")
        if os.path.isfile(bp):
            net.load_state_dict(torch.load(bp, map_location=dev))
        bake_qat(net)
        clean = ConditionedUNet(in_channels=spec["in"], base=args.base, depth=args.depth,
                                blocks=args.blocks, out_channels=spec["out"])
        clean.load_state_dict(net.state_dict())   # structural match after bake
        torch.save(clean.state_dict(), os.path.join(out, "best.pt"))
        print("baked QAT weights -> best.pt (fp32, int-grid-ready for export+PTQ)")

    print("done. best val:", best)


if __name__ == "__main__":
    main()
