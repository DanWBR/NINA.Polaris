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
- [x] **Halo int8 via QAT** (PTQ int8 is unsafe on residual models):

```powershell
python train_task.py --task halo --pairs data\own\halo_tiles --resume checkpoints\halo\best.pt `
    --qat --qat-bits 8 --epochs 15 --lr 5e-5 --out checkpoints\halo_qat8
python export.py --task halo --ckpt checkpoints\halo_qat8\best.pt --out models
python quantize.py calib --task halo --pairs data\own\halo_tiles --out models\calib_halo
python quantize.py int8 --onnx models\halo_fp32_256.onnx --calib models\calib_halo `
    --out models\halo_int8_256.onnx
```

- [x] **w8a16 matrix**: `quantize.py w8a16` exists and was never evaluated.
  DONE for halo (`eval\halo_qat.json`, 200 val samples, range L 107.96):

  | variant | PSNR dB | SSIM | Δ vs fp32 |
  |---|---|---|---|
  | fp32  | 48.36 | 0.9816 | - |
  | fp16  | 48.36 | 0.9816 | +0.01 |
  | int16 | 48.33 | 0.9816 | -0.03 |
  | **w8a16** | **48.33** | **0.9816** | **-0.03** |
  | int8  | 46.19 | 0.9759 | -2.17 |

  **Verdict: w8a16 is the preferred NPU/download format.** It matches int16
  quality (-0.03 dB, imperceptible) at int8 WEIGHT size (~half int16 on disk).
  The int8 -2.17 dB gap is ACTIVATION quantization, not weights: int16 and
  w8a16 both carry int8-grade weights and both tie fp32, so the QAT weights
  are correct; only 8-bit activations degrade int8. Repeat the same
  `quantize.py w8a16` + `eval_models.py` for denoise/bge/detail/upscale before
  declaring each family's download format. (`eval_models.py` now scans the
  w8a16 variant automatically.)

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

### Pilot r2 result (2026-07-03): recipe REGRESSED, do not adopt

Apples-to-apples vs the shipped `detail` checkpoint (same val, same range L 3.82):

| | fp32 PSNR | SSIM | FWHM ratio | dark-ring | flux ratio |
|---|---|---|---|---|---|
| baseline (detail) | **43.05** | **0.9365** | 1.0587 | **0.0224** | 0.8221 |
| r2 (ema+warmup+accum+w-fft+flux-aug) | 39.67 | 0.8626 | 1.0558 | 0.0373 | **0.9281** |

The "+7 dB vs 32.65" first seen was a range-L artifact (old baseline had a
different L); the honest comparison shows the recipe LOST 3.4 dB / 0.074 SSIM,
did NOT sharpen (FWHM ~unchanged), and **regressed dark-ring +66%**. Only flux
ratio improved (flux-aug). Prime suspect: `--w-fft` pushes high-freq -> ringing
= a dark ring around saturated cores (explains PSNR down + dark-ring up + FWHM
flat together). Next ablation: drop `--w-fft`, keep flux-aug + ema/warmup
(`detail_r4`).

### BXT noise-matched target (`--noise-matched-target`) - the grounded next lever

From "The Mathematics of BlurXTerminator" (RC-Astro). BXT's loss is
`e = f*g' + n - F[f*g + n]`: the target KEEPS the input's noise, so the net only
replaces the PSF and passes noise through -- deconvolution is NOT denoising. We
already match 2 of BXT's 3 pillars (non-delta reference target PSF
TARGET_FWHM=2.2; HDR saturated-core training). The gap: `synth.make_pair`'s
target is CLEAN (no noise), so our net is forced to sharpen AND denoise at once
-- the denoising pressure near saturated cores is a classic dark-ring generator.
Implemented `--noise-matched-target` (synth builds ONE additive noise field n
and adds it to both the seeing blur (input) and the reference blur (target); it
MUST be the SAME realization, else the MSE optimum is the clean target and the
net still learns to denoise). Eval with the matching `--noise-matched` flag on
both eval scripts or PSNR reads low (model correctly outputs noise a clean eval
target lacks) - judge on dark-ring / FWHM. (BXT pillar 3, separate stellar vs
non-stellar output PSFs, is the strategic future direction, a separate epic.)

```powershell
python train_task.py --task decon --tiles data\own\decon_tiles --val-tiles data\own\decon_tiles_val `
    --epochs 60 --out checkpoints\detail_r5 --batch 8 --accum 2 `
    --ema 0.999 --warmup-steps 500 --flux-aug --noise-matched-target
python export.py --task decon --ckpt checkpoints\detail_r5\best.pt --out models_r5
python eval_star_metrics.py --task decon --models models_r5 --tiles-val data\own\decon_tiles_val --noise-matched --json-out eval\r5_decon_stars.json
python eval_models.py --task decon --models models_r5 --tiles-val data\own\decon_tiles_val --noise-matched --json-out eval\r5_decon.json
```

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

---

## Detail r5 quantization result (2026-07-04)

Measured on 200 synth decon tiles (log-norm eval, fixed range L):

| variant | PSNR (dB) | SSIM | verdict |
|---|---|---|---|
| fp32  | 35.53 | 0.7682 | reference |
| fp16  | 35.53 | 0.7681 | lossless (pure cast) |
| int16 | 35.53 | 0.7682 | lossless (PTQ int16 == fp16) |
| w8a16 | 35.54 | 0.7689 | lossless (delta is rounding noise) |
| int8  | 34.57 | 0.3246 | BROKEN |

- **int8 PTQ collapses** on the residual-heavy Detail model: the tell is not
  the -0.96 dB PSNR but the SSIM crater 0.77 -> 0.32. Cause = int8 *activation*
  quantization crushing the small residual. w8a16 keeps int16 activations, so
  it is untouched. Plain int8 for Detail only via QAT (`train_task.py --qat`).
- **w8a16 is the preferred NPU/download quant for Detail/decon**: int8 weight
  size, int16-activation quality, lossless vs fp32.
- Real-image dark-ring/bubble check is still the true arbiter; synth PSNR/SSIM
  only proves the quant didn't regress vs fp16.

### w8a16 is now a first-class deploy tag (Polaris side)

Promoting r5 exposed that `w8a16` was unsupported end-to-end:

- `OnnxModelRegistry.VersionRegex` only matched `fp16|int16|int8` -> a
  `{family}/{version}-w8a16/model.onnx` folder was silently rejected and never
  registered. Fixed to accept `w8a16`.
- `app.js` model picker: `(W8A16, NPU)` label + display-name cleanup + iOS sort
  priority next to int8.
- `onnx-pipelines.js` load guard: treats `-w8a16` like `-int8` so a browser
  pick gives "meant for the NPU, use -fp16" instead of an ORT Web WASM crash
  (WASM EP lacks the int8 weight-dequant ops). w8a16 is an NPU/download format,
  not a browser one.
- Detail r5 deployed as `detail-ai-models/1.2` with fp32 + fp16 + int16 +
  w8a16 siblings (no int8). 1.1 kept as fallback.
