"""Train the deconvolution ConditionedUNet (fp32).

Loss = Charbonnier (robust L1) + gradient (edge) term + a star-protection term
that keeps bright cores from ringing (the exact artifact that plagued the NPU
GraXpert path).

  python train.py --tiles data/tiles --epochs 60 --batch 16 --out checkpoints/decon
"""
from __future__ import annotations

import argparse
import os

import torch
import torch.nn.functional as F
from torch.utils.data import DataLoader, random_split
from tqdm import tqdm

from dataset import DeconDataset
from model import ConditionedUNet


def charbonnier(pred, target, eps=1e-3):
    return torch.mean(torch.sqrt((pred - target) ** 2 + eps ** 2))


def grad_loss(pred, target):
    # finite-difference gradients; penalise edge mismatch (sharpness)
    px = pred[:, :, :, 1:] - pred[:, :, :, :-1]
    tx = target[:, :, :, 1:] - target[:, :, :, :-1]
    py = pred[:, :, 1:, :] - pred[:, :, :-1, :]
    ty = target[:, :, 1:, :] - target[:, :, :-1, :]
    return F.l1_loss(px, tx) + F.l1_loss(py, ty)


def star_protect(pred, target, thresh=0.7):
    """Extra penalty on bright pixels so cores stay faithful (anti-ring)."""
    mask = (target > thresh).float()
    denom = mask.sum().clamp_min(1.0)
    return (mask * (pred - target).abs()).sum() / denom


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tiles", required=True)
    ap.add_argument("--out", default="checkpoints/decon")
    ap.add_argument("--epochs", type=int, default=60)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--tile", type=int, default=256)
    ap.add_argument("--lr", type=float, default=2e-4)
    ap.add_argument("--base", type=int, default=48)
    ap.add_argument("--depth", type=int, default=4)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--val-frac", type=float, default=0.05)
    ap.add_argument("--w-grad", type=float, default=0.5)
    ap.add_argument("--w-star", type=float, default=0.25)
    ap.add_argument("--resume", default="")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    print("device:", dev)

    full = DeconDataset(args.tiles, tile=args.tile)
    n_val = max(1, int(len(full) * args.val_frac))
    n_tr = len(full) - n_val
    tr, va = random_split(full, [n_tr, n_val],
                          generator=torch.Generator().manual_seed(42))
    tl = DataLoader(tr, batch_size=args.batch, shuffle=True,
                    num_workers=args.workers, pin_memory=(dev == "cuda"), drop_last=True)
    vl = DataLoader(va, batch_size=args.batch, shuffle=False,
                    num_workers=args.workers, pin_memory=(dev == "cuda"))
    print(f"tiles: {len(full)} (train {n_tr}, val {n_val})")

    net = ConditionedUNet(in_channels=2, base=args.base, depth=args.depth).to(dev)
    if args.resume and os.path.isfile(args.resume):
        net.load_state_dict(torch.load(args.resume, map_location=dev))
        print("resumed from", args.resume)
    opt = torch.optim.AdamW(net.parameters(), lr=args.lr, weight_decay=1e-5)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
    scaler = torch.cuda.amp.GradScaler(enabled=(dev == "cuda"))

    best = float("inf")
    for ep in range(args.epochs):
        net.train()
        run = 0.0
        for x, y in tqdm(tl, desc=f"epoch {ep+1}/{args.epochs}"):
            x, y = x.to(dev), y.to(dev)
            opt.zero_grad(set_to_none=True)
            with torch.cuda.amp.autocast(enabled=(dev == "cuda")):
                p = net(x)
                loss = (charbonnier(p, y)
                        + args.w_grad * grad_loss(p, y)
                        + args.w_star * star_protect(p, y))
            scaler.scale(loss).backward()
            scaler.step(opt)
            scaler.update()
            run += loss.item()
        sched.step()

        # validation
        net.eval()
        vrun = 0.0
        with torch.no_grad():
            for x, y in vl:
                x, y = x.to(dev), y.to(dev)
                p = net(x)
                vrun += charbonnier(p, y).item()
        vloss = vrun / max(1, len(vl))
        print(f"  train {run/len(tl):.5f} | val {vloss:.5f} | lr {sched.get_last_lr()[0]:.2e}")

        torch.save(net.state_dict(), os.path.join(args.out, "last.pt"))
        if vloss < best:
            best = vloss
            torch.save(net.state_dict(), os.path.join(args.out, "best.pt"))
            print("  -> new best, saved best.pt")

    print("done. best val:", best)


if __name__ == "__main__":
    main()
