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
        ln = getattr(args, "log_norm", False)
        fa = getattr(args, "flux_aug", False)
        nm = getattr(args, "noise_matched_target", False)
        extra = []
        if args.tiles2:
            extra.append(DeconDataset(args.tiles2, tile=args.tile, log_norm=ln,
                                      flux_aug=fa, noise_matched=nm))
        tr = DeconDataset(args.tiles, tile=args.tile, log_norm=ln, flux_aug=fa,
                          noise_matched=nm)
        full = ConcatDataset([tr] + extra) if extra else tr
        # Validation never augments (flux or geometric) so the metric is stable,
        # but it MUST match the target definition (noise_matched) so val loss is
        # comparable to the train objective.
        val = DeconDataset(args.val_tiles, tile=args.tile, augment=False,
                           log_norm=ln, noise_matched=nm) \
            if args.val_tiles else None
        return full, val
    # denoise / bge: pre-baked pairs
    from paired_dataset import PairedTileDataset
    tr = PairedTileDataset(args.pairs, augment=True, tile=args.crop or None)
    val = PairedTileDataset(args.val_pairs, augment=False) if args.val_pairs else None
    return tr, val


def anti_ring(p, x_img, y, w=0.5):
    """Penalise the output dipping BELOW both the input and the target -- exactly
    the negative overshoot that carves the dark ring ("bubble") around bright
    stars. The clean target has no such dip and neither does the degraded input,
    so anything darker than min(input, target) is pure ringing."""
    import torch
    floor = torch.minimum(x_img, y)
    return w * torch.relu(floor - p).mean()


def fft_loss(p, y):
    """L1 between FFT magnitude spectra. Charbonnier under-weights the residual
    blur a deconvolution leaves behind (small per-pixel error, large missing
    high-frequency energy); matching |FFT| directly rewards recovering it.
    Computed in fp32 -- half-precision FFTs are numerically shaky under AMP."""
    import torch
    pf = torch.fft.rfft2(p.float())
    yf = torch.fft.rfft2(y.float())
    return (pf.abs() - yf.abs()).abs().mean()


def ssim_loss(p, y):
    """1 - SSIM (single scale, 7x7 uniform window). The stabilisation constants
    scale with the batch target's range (the tensors live in MAD/log domains,
    not 0..1 -- same range-aware trick as eval_models.py)."""
    import torch
    import torch.nn.functional as F
    p = p.float()
    y = y.float()
    L = (y.amax() - y.amin()).clamp_min(1e-6).detach()
    C1, C2 = (0.01 * L) ** 2, (0.03 * L) ** 2
    k = 7

    def mu(t):
        return F.avg_pool2d(t, k, 1, k // 2, count_include_pad=False)

    mp, my = mu(p), mu(y)
    vp = mu(p * p) - mp * mp
    vy = mu(y * y) - my * my
    cov = mu(p * y) - mp * my
    s = ((2 * mp * my + C1) * (2 * cov + C2)) / \
        ((mp * mp + my * my + C1) * (vp + vy + C2) + 1e-12)
    return 1.0 - s.mean()


def task_loss(task, p, y, w_grad, w_star, x=None, w_fft=0.0, w_ssim=0.0):
    # halo removal protects bright cores (star_protect) so it only touches the
    # faint halo, like decon.
    if task in ("decon", "halo"):
        loss = charbonnier(p, y) + w_grad * grad_loss(p, y) + w_star * star_protect(p, y)
        if x is not None:
            # x[:, :out_ch] is the image channel(s); decon out=1 so x[:, :1].
            loss = loss + anti_ring(p, x[:, :p.shape[1]], y)
    elif task in ("denoise", "upscale"):
        loss = charbonnier(p, y) + w_grad * grad_loss(p, y)
    else:
        loss = charbonnier(p, y)  # bge: smooth background, plain robust L1
    # AIIMP recipe terms (all opt-in; 0 keeps the legacy loss exactly).
    if w_fft > 0:
        loss = loss + w_fft * fft_loss(p, y)
    if w_ssim > 0:
        loss = loss + w_ssim * ssim_loss(p, y)
    return loss


class Ema:
    """Exponential moving average of the model weights. Validation and the
    best/last checkpoints use the EMA weights (the standard restoration-recipe
    trick: smooths the tail of training for a near-free +0.1..0.3 dB).
    Not used under --qat (parametrized modules complicate the shadow)."""

    def __init__(self, net, decay: float):
        self.decay = decay
        self.shadow = {k: v.detach().clone()
                       for k, v in net.state_dict().items()}

    @torch.no_grad()
    def update(self, net):
        for k, v in net.state_dict().items():
            s = self.shadow[k]
            if v.dtype.is_floating_point:
                s.mul_(self.decay).add_(v.detach(), alpha=1.0 - self.decay)
            else:
                s.copy_(v)


def make_net(args, spec):
    norm = getattr(args, "norm", "bn")
    res_scale = getattr(args, "res_scale", 1.0)
    if args.task == "upscale":
        return UpscaleNet(scale=args.scale, base=args.base, depth=args.depth,
                          blocks=args.blocks, norm=norm, res_scale=res_scale)
    return ConditionedUNet(in_channels=spec["in"], base=args.base, depth=args.depth,
                           blocks=args.blocks, out_channels=spec["out"],
                           norm=norm, res_scale=res_scale)


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
    ap.add_argument("--qat-batch", type=int, default=0,
                    help="batch size override for QAT (0 = auto: batch//4, or batch//8 "
                         "for upscale which 2×-upsamples the input before the UNet). "
                         "Ignored unless --qat is set.")
    ap.add_argument("--scale", type=int, default=2, choices=[2, 3, 4],
                    help="(upscale) super-resolution factor")
    # ---- AIIMP training recipe (all opt-in; defaults = legacy behaviour) ----
    ap.add_argument("--ema", type=float, default=0.0,
                    help="EMA decay (e.g. 0.999); 0 = off. Validation and the "
                         "best/last checkpoints use the EMA weights. Ignored "
                         "under --qat.")
    ap.add_argument("--warmup-steps", type=int, default=0,
                    help="Linear LR warmup over N optimizer steps, then cosine "
                         "per step (0 = legacy per-epoch cosine, no warmup).")
    ap.add_argument("--accum", type=int, default=1,
                    help="Gradient accumulation: optimizer steps every N "
                         "batches (effective batch = batch * N).")
    ap.add_argument("--w-fft", type=float, default=0.0,
                    help="FFT-magnitude L1 loss weight (decon/upscale: rewards "
                         "recovering the high-frequency energy PSNR "
                         "under-weights). Try 0.05..0.1.")
    ap.add_argument("--w-ssim", type=float, default=0.0,
                    help="(1 - SSIM) loss weight, range-aware single scale "
                         "(denoise/upscale). Try 0.15.")
    ap.add_argument("--flux-aug", action="store_true",
                    help="(decon) random exposure gain x0.5..2.0 on the sharp "
                         "tile BEFORE the forward model; gain > 1 clips more "
                         "stars, varying the saturated-core morphology the "
                         "dark-ring fix targets. (Paired tasks skip it: their "
                         "MAD/log per-tile normalization is gain-invariant, a "
                         "global gain would be a no-op.)")
    ap.add_argument("--noise-matched-target", action="store_true",
                    help="(decon) BlurXTerminator formulation: target = f*g' + n "
                         "with the SAME additive noise as the input, instead of "
                         "the clean reference-PSF target. The net then only "
                         "replaces the PSF and passes noise through untouched "
                         "(deconvolution != denoising), which removes the "
                         "over-smoothing pressure that carves dark rings around "
                         "saturated cores. NOTE: eval it with the matching "
                         "--noise-matched flag or PSNR will read low (the model "
                         "correctly outputs noise a clean eval target lacks).")
    ap.add_argument("--distill-teacher", default="",
                    help="Path to a teacher checkpoint (e.g. the 60M best.pt). "
                         "Adds w * L1(student, teacher(x)) so a small --base/"
                         "--blocks student learns the big model's mapping.")
    ap.add_argument("--distill-w", type=float, default=1.0)
    ap.add_argument("--teacher-base", type=int, default=96)
    ap.add_argument("--teacher-depth", type=int, default=4)
    ap.add_argument("--teacher-blocks", type=int, default=3)
    ap.add_argument("--norm", default="bn", choices=["bn", "none"],
                    help="Block normalization: 'bn' (legacy) or 'none' "
                         "(EDSR-style, pair with --res-scale 0.1).")
    ap.add_argument("--res-scale", type=float, default=1.0,
                    help="Residual-branch scale inside ResBlocks (0.1 "
                         "stabilises deep no-norm stacks).")
    ap.add_argument("--log-norm", action=argparse.BooleanOptionalAction, default=True,
                    help="(decon/detail) GraXpert-style log-mean-std per-tile "
                         "normalization instead of the 1st/99.9th percentile map. "
                         "Log-compresses the dynamic range so saturated star cores "
                         "no longer drive the dark-ring overshoot. This is now the "
                         "DEFAULT and matches the default inference path in "
                         "onnx-pipelines.js, so a plainly-named export (e.g. 1.2) "
                         "runs log. Pass --no-log-norm to train the legacy "
                         "percentile model (name that export with a '-pct' tag).")
    args = ap.parse_args()

    out = args.out or f"checkpoints/{args.task}"
    os.makedirs(out, exist_ok=True)
    dev = "cuda" if torch.cuda.is_available() else "cpu"

    # QAT fake-quant hooks materialise extra activation copies for each
    # Conv that stay alive for backward, multiplying peak memory. To avoid
    # OOM and Windows TDR (GPU watchdog killing a kernel that runs >~2s),
    # auto-scale the batch down when --qat is active.
    # Upscale is doubly expensive: UpscaleNet runs self.up(x) BEFORE the
    # UNet, so the network sees (scale×tile)² tiles internally → 4× memory
    # vs a same-sized standard tile, hence the extra halving.
    if args.qat:
        import os as _os
        # Reduce CUDA memory fragmentation (recommended by PyTorch OOM messages).
        if dev == "cuda":
            _os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")
        if args.qat_batch > 0:
            eff_batch = args.qat_batch
        elif args.task == "upscale":
            eff_batch = max(1, args.batch // 8)
        else:
            eff_batch = max(1, args.batch // 4)
        if eff_batch != args.batch:
            print(f"QAT: auto-scaling batch {args.batch} → {eff_batch} "
                  f"(fake-quant hooks multiply activation memory; use --qat-batch N to override)")
        args.batch = eff_batch

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

    # Distillation teacher: frozen big model whose OUTPUT the student mimics
    # (output-level only; feature distillation doesn't pay its complexity at
    # this size gap). Runs under the same autocast, no grad.
    teacher = None
    if args.distill_teacher:
        if args.task == "upscale":
            teacher = UpscaleNet(scale=args.scale, base=args.teacher_base,
                                 depth=args.teacher_depth, blocks=args.teacher_blocks)
        else:
            teacher = ConditionedUNet(in_channels=spec["in"], base=args.teacher_base,
                                      depth=args.teacher_depth,
                                      blocks=args.teacher_blocks,
                                      out_channels=spec["out"])
        teacher.load_state_dict(torch.load(args.distill_teacher, map_location="cpu"))
        teacher.to(dev).eval()
        for tp in teacher.parameters():
            tp.requires_grad_(False)
        print(f"distill: teacher {args.teacher_base}/{args.teacher_depth}/"
              f"{args.teacher_blocks} from {args.distill_teacher} (w={args.distill_w})")

    ema = None
    if args.ema > 0 and not args.qat:
        ema = Ema(net, args.ema)
        print(f"EMA enabled (decay {args.ema}); checkpoints hold EMA weights")

    accum = max(1, args.accum)
    opt = torch.optim.AdamW(net.parameters(), lr=args.lr, weight_decay=1e-5)
    # Warmup > 0 switches to a PER-STEP schedule (linear warmup then cosine to
    # the same zero endpoint); warmup 0 keeps the legacy per-epoch cosine so
    # old runs reproduce exactly.
    steps_per_epoch = max(1, len(tl) // accum)
    if args.warmup_steps > 0:
        total_steps = steps_per_epoch * args.epochs
        warm = torch.optim.lr_scheduler.LinearLR(
            opt, start_factor=1e-3, total_iters=args.warmup_steps)
        cos = torch.optim.lr_scheduler.CosineAnnealingLR(
            opt, T_max=max(1, total_steps - args.warmup_steps))
        sched = torch.optim.lr_scheduler.SequentialLR(
            opt, [warm, cos], milestones=[args.warmup_steps])
        per_step_sched = True
    else:
        sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
        per_step_sched = False
    scaler = torch.amp.GradScaler("cuda", enabled=amp_on)

    best = float("inf")
    for ep in range(args.epochs):
        net.train()
        run = 0.0
        opt.zero_grad(set_to_none=True)
        for bi, (x, y) in enumerate(tqdm(tl, desc=f"epoch {ep+1}/{args.epochs}")):
            x, y = x.to(dev), y.to(dev)
            with torch.amp.autocast("cuda", enabled=amp_on):
                p = net(x)
                loss = task_loss(args.task, p, y, args.w_grad, args.w_star, x=x,
                                 w_fft=args.w_fft, w_ssim=args.w_ssim)
                if teacher is not None:
                    with torch.no_grad():
                        tout = teacher(x)
                    loss = loss + args.distill_w * (p - tout).abs().mean()
            scaler.scale(loss / accum).backward()
            if (bi + 1) % accum == 0:
                scaler.step(opt)
                scaler.update()
                opt.zero_grad(set_to_none=True)
                if ema is not None:
                    ema.update(net)
                if per_step_sched:
                    sched.step()
            run += loss.item()
        if not per_step_sched:
            sched.step()

        # Validate (and checkpoint) with the EMA weights when enabled: swap the
        # shadow in, evaluate/save, then restore the live training weights.
        backup = None
        if ema is not None:
            backup = {k: v.detach().clone() for k, v in net.state_dict().items()}
            net.load_state_dict(ema.shadow)
        net.eval()
        vrun = 0.0
        with torch.no_grad():
            for x, y in vl:
                x, y = x.to(dev), y.to(dev)
                vrun += charbonnier(net(x), y).item()
        vloss = vrun / max(1, len(vl))
        print(f"  train {run/max(1,len(tl)):.5f} | val {vloss:.5f} "
              f"| lr {sched.get_last_lr()[0]:.2e}"
              + (" | ema" if ema is not None else ""))

        # Under QAT the live model carries parametrizations/observers, so its
        # state_dict won't load into a plain net. Save those to *_qat.pt for
        # resume; the export-ready (baked) best.pt is written after training.
        best_name = "best_qat.pt" if args.qat else "best.pt"
        torch.save(net.state_dict(), os.path.join(out, "last_qat.pt" if args.qat else "last.pt"))
        if vloss < best:
            best = vloss
            torch.save(net.state_dict(), os.path.join(out, best_name))
            print(f"  -> new best, saved {best_name}")
        if backup is not None:
            net.load_state_dict(backup)

    if args.qat:
        # Reload best, bake the rounded weights into plain Conv2d.weight, drop the
        # observers/hooks, and save a clean best.pt that export.py loads unchanged.
        from quant_layers import bake_qat
        bp = os.path.join(out, "best_qat.pt")
        if os.path.isfile(bp):
            net.load_state_dict(torch.load(bp, map_location=dev))
        bake_qat(net)
        clean = ConditionedUNet(in_channels=spec["in"], base=args.base, depth=args.depth,
                                blocks=args.blocks, out_channels=spec["out"],
                                norm=args.norm, res_scale=args.res_scale)
        clean.load_state_dict(net.state_dict())   # structural match after bake
        torch.save(clean.state_dict(), os.path.join(out, "best.pt"))
        print("baked QAT weights -> best.pt (fp32, int-grid-ready for export+PTQ)")

    print("done. best val:", best)


if __name__ == "__main__":
    main()
