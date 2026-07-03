# Model improvement roadmap (quality + speed)

Research + experiment plan for the polaris-ai model families (bge, denoise,
detail/decon, halo, upscale), excluding "more training data" (out of scope by
decision). Grounded in the code as of 2026-07-02; every experiment reports
against the Fase 0 measurement harness below. Pilot family: **detail/decon**.

Status legend: [x] infra implemented (this repo), [ ] training/eval run the
operator performs on the GPU rig. All commands are PowerShell; run
`$env:PYTHONUTF8=1` first in each session.

---

## Findings that reframed the plan (verified, not theory)

1. **The decon eval was measuring the wrong domain.** `eval_models.py` fed the
   model LINEAR tiles while the production Detail model trains (and the
   int8/int16 calib set is built) in the GraXpert log domain. Every historical
   decon number was invalid. Fixed (`--log-norm` default True).
2. **The "int16 +2.9 dB over fp32" anomaly was mixed vintages**, not
   quantization: `models/decon_fp32*.onnx` had been exported from the OLD
   percentile checkpoint while int16 came from the new log-trained `detail`
   checkpoint (initializer namespaces don't match). After re-exporting
   fp32/fp16 from `checkpoints/detail/best.pt`: fp32 32.65, fp16 +0.00,
   int16 **-0.03 dB** (expected). Rule: **all precision variants of a version
   must come from one export of one checkpoint.**
3. **Eval pairs were non-reproducible**: the synth seed used Python `hash()`,
   which is salted per process - every run evaluated different pairs. Fixed
   (crc32).
4. **int8 PTQ is truly broken on decon** (-9 dB, negative SSIM): the residual
   model's small delta rounds to garbage in 256 levels. Production int8 for
   detail must come from the QAT checkpoint (`checkpoints/detail_int8`).
5. **The 60M model is 4-10x oversized** for per-task tile restoration
   (NAFNet-class results land at 10-30M; NTIRE 2025 distills 5M students).
   The size directly causes the iOS 200 MB WebGPU cap problem and the SBC
   ms/tile. `base=48, blocks=2` = 10.6M params, fp16 ~21 MB.
6. **Denoise tiling wasted 4x compute** (stride 128 on a 256 window). Fixed to
   stride 192; verified vs the real model: composites agree at 117.6 dB PSNR,
   no seam spikes, 2.25x fewer inferences. Measured on the i9 CPU:
   31.7 s/MP -> 14.1 s/MP.

---

## Fase 0 - measurement (infra done; baseline run pending)

Tools (all in this folder):

- `eval_models.py` - PSNR/SSIM per precision. Now: decon log-norm domain,
  deterministic pairs (crc32), dataset-global range L, `--json-out`.
- `eval_star_metrics.py` - per-star **FWHM ratio** (did it deconvolve),
  **dark-ring index** (the "bubble" artifact, measured directly),
  **flux ratio** (photometry preserved). Synth pairs or `--real` tiles.
- `make_holdout.py` - frozen real-data holdout (fixed seed + manifest,
  `data/` stays gitignored).
- `bench_onnx.py` - ms/tile + projected **ms/megapixel per real tiling
  config** (that projection is where speedups live).

### [ ] Build the baseline snapshot (operator)

```powershell
$env:PYTHONUTF8=1
# real holdout, once, frozen forever
python make_holdout.py --sources "E:\Projeto Aguia\2026\RGB\lights" `
    "$env:USERPROFILE\Downloads\SV535\lights" --out data\holdout_real --count 40

# per family (repeat: denoise/bge/halo/upscale with --val-pairs)
python eval_models.py --task decon --models models --tiles-val data\own\decon_tiles_val `
    --json-out eval\baseline_decon.json
python eval_star_metrics.py --task decon --models models --tiles-val data\own\decon_tiles_val `
    --json-out eval\baseline_decon_stars.json
python eval_star_metrics.py --task decon --models models --real data\holdout_real `
    --json-out eval\baseline_decon_real.json
python bench_onnx.py --models models --report eval\timing_desktop.json
```

Commit the `eval/*.json` files (they are small and ARE the reference).

---

## Fase 1 - runtime wins, no retraining (done in code)

- [x] Denoise stride 128 -> 192 (`onnx-pipelines.js`), `opts.stride` for A/B.
  ~2.25x fewer inferences, verified output-identical.
- [x] 512-static sibling support: registry accepts `-512` versions;
  `prefer512OnDesktopGpu()` picks them on desktop WebGPU only. To produce one:
  `python export.py --task denoise --ckpt checkpoints\denoise\best.pt --out models --size 512`
  then deploy as e.g. `denoise-ai-models/2.0.0-512-fp16/model.onnx`.
- [x] `export.py --graph-opt`: ORT offline graph-optimized `_opt` sibling
  (BN folds pre-baked for browser WASM).
- [ ] **Halo int8 via QAT** (PTQ int8 is unsafe on residual models):

```powershell
python train_task.py --task halo --pairs data\own\halo_tiles --resume checkpoints\halo\best.pt `
    --qat --qat-bits 8 --epochs 15 --lr 5e-5 --out checkpoints\halo_qat8
python export.py --task halo --ckpt checkpoints\halo_qat8\best.pt --out models
python quantize.py calib --task halo --pairs data\own\halo_tiles --out models\calib_halo
python quantize.py int8 --onnx models\halo_fp32_256.onnx --calib models\calib_halo `
    --out models\halo_int8_256.onnx
```

- [ ] **w8a16 matrix**: `quantize.py w8a16` exists and was never evaluated. If
  it lands near int16 quality at int8 weight size, it becomes the preferred
  NPU/download format. Generate for each family and add to the eval JSONs.

---

## Fase 2 - training recipe, same architecture (flags done; pilot = decon)

All in `train_task.py`, opt-in, ablatable. **Adoption gate**: >= +0.2 dB OR a
clear FWHM-ratio / dark-ring / real-holdout win at matched epochs.

| Flag | What | Expected |
|---|---|---|
| `--ema 0.999` | EMA weights validated/checkpointed | +0.1-0.3 dB, most reliable single win |
| `--warmup-steps 500` | linear warmup -> per-step cosine | stability + lower run variance |
| `--accum 2..4` | effective batch 32-64 | small, helps A/B repeatability |
| `--w-fft 0.05..0.1` | FFT-magnitude L1 (decon/upscale) | visible sharpness; best FWHM-ratio candidate |
| `--w-ssim 0.15` | range-aware 1-SSIM (denoise/upscale) | structure-weighted quality |
| `--flux-aug` | (decon) exposure-gain aug, varies saturation | robustness of the dark-ring fix |

**Do not touch** star-protect / anti-ring weights - they encode the shipped
dark-ring fix; watch the dark-ring index for regressions instead.

### [ ] Pilot run (decon, GPU)

```powershell
python train_task.py --task decon --tiles data\own\decon_tiles --val-tiles data\own\decon_tiles_val `
    --epochs 60 --out checkpoints\detail_r2 `
    --ema 0.999 --warmup-steps 500 --accum 2 --w-fft 0.05 --flux-aug
python export.py --task decon --ckpt checkpoints\detail_r2\best.pt --out models_r2
python eval_models.py --task decon --models models_r2 --tiles-val data\own\decon_tiles_val --json-out eval\r2_decon.json
python eval_star_metrics.py --task decon --models models_r2 --tiles-val data\own\decon_tiles_val --json-out eval\r2_decon_stars.json
```

Winning recipe then retrains the other tasks - those checkpoints become the
**teachers** for Fase 3.

---

## Fase 3 - shrink 4-6x (distillation; the strategic payoff)

Solves the 60M oversize AND the iOS cap (student fp16 ~21-40 MB) and stacks
multiplicatively with Fase 1.

### [ ] Capacity + norm ablation (denoise, short 40-epoch runs)

Grid: `--base {48,64} x --blocks {2,3} x {default BN | --norm none --res-scale 0.1}`.
Param guide: 64/4/2 ~ 18M, 48/4/3 ~ 15M, 48/4/2 ~ 10.6M. BN folds into conv at
export (zero inference cost) - this ablation is about TRAINING quality and
QDQ activation-range behavior; the fp32 winner must also pass an int16/int8
PTQ spot-check before being declared.

### [ ] Distill 60M teacher -> student

```powershell
python train_task.py --task decon --tiles data\own\decon_tiles --val-tiles data\own\decon_tiles_val `
    --epochs 80 --out checkpoints\detail_student `
    --base 48 --blocks 2 `
    --distill-teacher checkpoints\detail_r2\best.pt --distill-w 1.0 `
    --ema 0.999 --warmup-steps 500 --w-fft 0.05
```

Goal: within ~0.2 dB of the teacher at 3-5x speed. Verify on-device: RKNN
conversion + NPU timing, ncnn-Vulkan sanity (the smaller net may dodge the
denoise-v3 NaN - check explicitly), browser WebGPU/WASM timing, iOS load.

### Rejected by default: NAFNet-lite blocks

SimpleGate/GAP+Mul are a pnnx/ncnn conversion risk (plain-conv decon already
fails to convert faithfully), activation-x-activation Mul is an int8 weak
point, and distillation with plain convs should hit the target. Revisit only
if the student gap stays > 0.3 dB - and then FIRST push an untrained dummy
export through RKNN-toolkit2 + ORT Web + ncnn before spending any GPU time.

### Optional: RepConv (structural reparameterization)

3x3+1x1+identity branches in training, algebraically folded to a single 3x3
at export (post-fold ONNX identical in structure to today's). +0.05-0.15 dB
free at inference. Implement in `model.py`+`export.py` if the schedule allows.

---

## Fase 4 - optional quality modes

- **x8 self-ensemble TTA** toggle (JS only, off by default): flips/rot90
  averaged; a distilled student with TTA x8 is still faster than the current
  60M without it.
- **FFDNet-style noise-map conditioning** for denoise (3 -> 4 input channels,
  constant sigma map; the decon sigma channel is the template across
  model/export/JS). Gives a REAL strength slider. Fold into the Fase 3
  denoise retrain so it costs no extra run.

---

## Deployment invariants (learned the hard way)

- One checkpoint -> one export -> all precision variants. Never mix vintages
  in a `models/` dir or a deployed version folder.
- int8 for residual-learning models only via QAT.
- After swapping deployed model files: Settings -> AI "Re-scan models" +
  "Clear model cache" + hard reload (IndexedDB caches by hash).
- ONNX files and `data/` never go to git (Supabase bucket for distribution).
