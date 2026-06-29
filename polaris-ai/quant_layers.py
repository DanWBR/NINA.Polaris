"""Straight-through-estimator (STE) fake quantization for in-house QAT.

This is "train int8/int16 from scratch" done the way it actually works: weights
stay float during training, but a fake-quant op rounds them onto the int grid in
the forward pass (gradients pass straight through), so the network learns weights
that survive quantization. We OWN the model, so we insert fake-quant via a weight
parametrization + an activation forward-hook, train, then **bake** the rounded
weights back into plain ``Conv2d.weight`` and drop the hooks. The exported model
is therefore an ordinary fp32 ONNX whose weights already sit on the int grid —
``quantize.py int8`` (ORT QDQ) then reproduces them near-losslessly, and the
vendor NPU toolchains re-quantize the same fp32 with their own calibration.

This deliberately avoids torch.ao FX QAT, whose graph does not export to ONNX on
the current torch build (fake-quant / quantized::conv2d ops don't lower).

Recipe: per-output-channel **symmetric** int weights; per-tensor **affine**
int activations with an EMA-tracked range. BN is left unfused (simpler; the next
conv's activation fake-quant still bounds the range) — good enough to de-risk int8.
"""
from __future__ import annotations

import torch
import torch.nn as nn
from torch.nn.utils import parametrize


def fake_quant(x: torch.Tensor, scale: torch.Tensor, qmin: int, qmax: int,
               zero_point: torch.Tensor | float = 0.0) -> torch.Tensor:
    """Round x onto an int grid then back to float, with an STE backward pass."""
    q = torch.clamp(torch.round(x / scale) + zero_point, qmin, qmax)
    dq = (q - zero_point) * scale
    return x + (dq - x).detach()          # STE: d/dx (dq) == 1


class WeightFakeQuant(nn.Module):
    """Parametrization: per-output-channel symmetric fake-quant of a conv weight
    ``[out, in, kh, kw]``. Stateless (range derived from the weight each step)."""

    def __init__(self, bits: int = 8):
        super().__init__()
        self.qmax = 2 ** (bits - 1) - 1   # 127 (int8) / 32767 (int16)
        self.qmin = -self.qmax

    def forward(self, w: torch.Tensor) -> torch.Tensor:
        amax = w.detach().abs().amax(dim=(1, 2, 3), keepdim=True).clamp_min(1e-8)
        scale = amax / self.qmax
        return fake_quant(w, scale, self.qmin, self.qmax, 0.0)


class ActFakeQuant(nn.Module):
    """Per-tensor affine fake-quant of an activation, range tracked by EMA during
    training (frozen at eval)."""

    def __init__(self, bits: int = 8, momentum: float = 0.99):
        super().__init__()
        self.qmax = 2 ** bits - 1
        self.m = momentum
        self.register_buffer("mn", torch.zeros(1))
        self.register_buffer("mx", torch.ones(1))
        self.register_buffer("ready", torch.zeros(1))

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        if self.training:
            mn = x.detach().min().reshape(1).float()
            mx = x.detach().max().reshape(1).float()
            if self.ready.item() == 0:
                self.mn.copy_(mn); self.mx.copy_(mx); self.ready.fill_(1)
            else:
                self.mn.mul_(self.m).add_((1 - self.m) * mn)
                self.mx.mul_(self.m).add_((1 - self.m) * mx)
        scale = (self.mx - self.mn).clamp_min(1e-8) / self.qmax
        zp = torch.round(-self.mn / scale)
        return fake_quant(x, scale.to(x.dtype), 0, self.qmax, zp.to(x.dtype))


def apply_qat(model: nn.Module, bits: int = 8) -> nn.Module:
    """Insert weight + activation fake-quant on every Conv2d. Idempotent guard via
    ``model._qat_handles``. Call once before training."""
    if getattr(model, "_qat_handles", None) is not None:
        return model
    handles = []
    for m in model.modules():
        if isinstance(m, nn.Conv2d):
            parametrize.register_parametrization(m, "weight", WeightFakeQuant(bits))
            m.add_module("_afq", ActFakeQuant(bits))

            def pre_hook(mod, inp):
                return (mod._afq(inp[0]),)

            handles.append(m.register_forward_pre_hook(pre_hook))
    model._qat_handles = handles
    return model


def bake_qat(model: nn.Module) -> nn.Module:
    """Remove the fake-quant machinery, writing the rounded weights into plain
    ``Conv2d.weight`` and deleting the activation observers + hooks. Afterwards the
    model is structurally a vanilla ConditionedUNet whose state_dict loads into one
    (so export.py works unchanged)."""
    for h in getattr(model, "_qat_handles", []) or []:
        h.remove()
    for m in model.modules():
        if isinstance(m, nn.Conv2d):
            if parametrize.is_parametrized(m, "weight"):
                parametrize.remove_parametrizations(m, "weight", leave_parametrized=True)
            if hasattr(m, "_afq"):
                del m._afq
    if hasattr(model, "_qat_handles"):
        del model._qat_handles
    return model
