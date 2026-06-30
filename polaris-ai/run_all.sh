#!/usr/bin/env bash
# Orchestrate the polaris-ai pipeline on Linux (e.g. a RunPod GPU pod).
# Mirrors run_all.ps1: per task -> train fp32 -> train --qat -> export fp16
# -> calib -> int16 PTQ -> int8 PTQ -> eval.
#
#   ./run_all.sh --prep                       # generate all datasets once
#   ./run_all.sh --prep bge                    # one dataset
#   ./run_all.sh denoise bge decon upscale halo
#   ./run_all.sh --gpu 0 --no-qat --batch 24 decon
#   ./run_all.sh --gpu 0 --qat-only --batch 16 denoise   # reuse fp32, run QAT only
#
# Options: --prep  --no-qat  --qat-only  --gpu N  --batch N  --base N  --blocks N  --models DIR
set -euo pipefail
cd "$(dirname "$0")"
export PYTHONIOENCODING=utf-8

PREP=0; NOQAT=0; QATONLY=0; BATCH=0; BASE=0; BLOCKS=0; GPU=""; MODELS=models
TASKS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --prep)     PREP=1 ;;
    --no-qat)   NOQAT=1 ;;
    --qat-only) QATONLY=1 ;;   # skip fp32 train, resume existing checkpoints/$T/best.pt
    --gpu)      GPU="$2"; shift ;;
    --batch)    BATCH="$2"; shift ;;
    --base)     BASE="$2"; shift ;;
    --blocks)   BLOCKS="$2"; shift ;;
    --models)   MODELS="$2"; shift ;;
    -h|--help)  sed -n '2,13p' "$0"; exit 0 ;;
    -*)         echo "unknown option $1" >&2; exit 1 ;;
    *)          TASKS+=("$1") ;;
  esac
  shift
done
# --qat-only implies QAT runs (can't combine with --no-qat).
if [[ $QATONLY -eq 1 ]]; then NOQAT=0; fi

if [[ -n "$GPU" ]]; then export CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES="$GPU"; fi
if [[ ${#TASKS[@]} -eq 0 ]]; then TASKS=(denoise bge decon upscale halo); fi

# Per-task config -> sets KIND DATA VAL B K FP Q SZ SCALE
cfg() {
  SCALE=""
  case "$1" in
    decon)   KIND=tiles; DATA=data/own/decon_tiles;   VAL=data/own/decon_tiles_val; B=96; K=3; FP=60;  Q=20; SZ=256 ;;
    denoise) KIND=pairs; DATA=data/own/denoise_tiles; VAL=data/own/denoise_val;     B=96; K=3; FP=80;  Q=20; SZ=256 ;;
    bge)     KIND=pairs; DATA=data/own/bge_tiles;     VAL=data/own/bge_val;         B=96; K=3; FP=120; Q=25; SZ=256 ;;
    upscale) KIND=pairs; DATA=data/own/upscale_tiles; VAL=data/own/upscale_val;     B=64; K=2; FP=100; Q=20; SZ=128; SCALE=2 ;;
    halo)    KIND=pairs; DATA=data/own/halo_tiles;    VAL=data/own/halo_val;        B=96; K=3; FP=90;  Q=20; SZ=256 ;;
    *) echo "unknown task '$1' (bge|denoise|decon|upscale|halo)" >&2; exit 1 ;;
  esac
  # NOTE: use if/fi, not `cond && assign`. Under `set -e` a `[[ ]] && x` whose
  # test is false returns 1; as the LAST statement of a function that makes the
  # function return 1, which aborts the whole script. That bug exited run_all
  # right after the "device:" line whenever BASE/BLOCKS were left at 0.
  if [[ $BASE   -gt 0 ]]; then B=$BASE; fi
  if [[ $BLOCKS -gt 0 ]]; then K=$BLOCKS; fi
}

data_args() {  # train data flags (with val)
  if [[ $KIND == tiles ]]; then printf -- "--tiles %s --val-tiles %s" "$DATA" "$VAL"
  else printf -- "--pairs %s --val-pairs %s" "$DATA" "$VAL"; fi
  if [[ -n $SCALE ]]; then printf -- " --scale %s" "$SCALE"; fi
}

if [[ $PREP -eq 1 ]]; then
  for T in "${TASKS[@]}"; do
    case "$T" in
      decon)   python data_prep/make_distortions.py --previews 3 ;;
      denoise) python data_prep/make_noise.py --per-image 3 ;;
      bge)     python data_prep/make_gradients.py --per-image 40 ;;
      upscale) python data_prep/make_upscale.py --scale 2 --hr-dir denoised ;;
      halo)    python data_prep/make_halos.py --per-image 4 --clean-dir denoised ;;
      *) echo "unknown task '$T'" >&2; exit 1 ;;
    esac
  done
  echo "prep done"; exit 0
fi

FPB=8;  if [[ $BATCH -gt 0 ]]; then FPB=$BATCH; fi
QB=6;   if [[ $BATCH -gt 0 ]]; then QB=$BATCH; fi
echo "device: $(python -c 'import torch;print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else "CPU")')"

for T in "${TASKS[@]}"; do
  cfg "$T"
  [[ -d $DATA ]] || { echo "missing $DATA -- run: ./run_all.sh --prep $T" >&2; exit 1; }

  if [[ $QATONLY -eq 1 ]]; then
    [[ -f "checkpoints/$T/best.pt" ]] || {
      echo "missing checkpoints/$T/best.pt -- run fp32 first (drop --qat-only)" >&2; exit 1; }
    echo "==> (qat-only) skipping fp32, reusing checkpoints/$T/best.pt"
  else
    echo "==> train $T fp32 (${FP}ep, base=$B blocks=$K)"
    python train_task.py --task "$T" --epochs $FP --batch $FPB --workers 4 \
      --base $B --blocks $K --out "checkpoints/$T" $(data_args)
  fi
  CK="checkpoints/$T/best.pt"

  if [[ $NOQAT -eq 0 ]]; then
    echo "==> train $T qat (${Q}ep)"
    python train_task.py --task "$T" --qat --resume "checkpoints/$T/best.pt" \
      --lr 5e-5 --epochs $Q --batch $QB --workers 4 --base $B --blocks $K \
      --out "checkpoints/${T}_qat" $(data_args)
    CK="checkpoints/${T}_qat/best.pt"
  fi

  echo "==> export $T"
  EX=(export.py --task "$T" --ckpt "$CK" --base $B --blocks $K --size $SZ --out "$MODELS")
  if [[ -n $SCALE ]]; then EX+=(--scale $SCALE); fi
  python "${EX[@]}"

  echo "==> calib $T"
  if [[ $KIND == tiles ]]; then python quantize.py calib --task "$T" --tiles "$DATA" --out "$MODELS/calib_$T"
  else python quantize.py calib --task "$T" --pairs "$DATA" --out "$MODELS/calib_$T"; fi

  FP32="$MODELS/${T}_fp32_${SZ}.onnx"
  echo "==> int16/int8 $T"
  python quantize.py int16 --onnx "$FP32" --calib "$MODELS/calib_$T" --out "$MODELS/${T}_int16_${SZ}.onnx"
  python quantize.py int8  --onnx "$FP32" --calib "$MODELS/calib_$T" --out "$MODELS/${T}_int8_${SZ}.onnx"

  echo "==> eval $T"
  EV=(eval_models.py --task "$T" --models "$MODELS" --size $SZ)
  if [[ $KIND == tiles ]]; then EV+=(--tiles-val "$VAL"); else EV+=(--val-pairs "$VAL"); fi
  python "${EV[@]}"
  echo "DONE: $T -> $MODELS/${T}_{fp16,int16,int8}_${SZ}.onnx"
done
echo "all done"
