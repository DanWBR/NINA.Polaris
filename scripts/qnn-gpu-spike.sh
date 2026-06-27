#!/usr/bin/env bash
# qnn-gpu-spike.sh -- QNN GPU-backend feasibility spike (Adreno 643, fp16/fp32).
#
# The Qualcomm AI Hub device matrix shows the QCS6490 (= Radxa Dragon Q6A) GPU
# does fp16/fp32, while the HTP (NPU) is int8/int16-only and rejects LayerNorm
# (V73+). So the GraXpert models the HTP CAN'T run -- denoise v3 (LayerNorm),
# deconvolution, star removal -- plus any fringing-sensitive run, have a native
# float home on the Adreno GPU via QAIRT's *libQnnGpu.so* backend. We already
# bundle the QAIRT runtime, so trying the GPU is literally swapping the backend
# .so + feeding a NON-quantized (fp32) DLC/context. This script proves (or
# disproves) that on the board BEFORE any packaging/UI work.
#
# It answers the three open questions:
#   1. Is libQnnGpu.so present (and are its OpenCL deps resolvable)?
#   2. Does it actually run the model -- incl. ops the HTP rejected (LayerNorm)?
#   3. ms/tile on the GPU vs the same model on the QNN CPU backend.
#
# Read-only except for a private temp dir (auto-removed). Touches no install.
#
# Usage:
#   bash scripts/qnn-gpu-spike.sh MODEL [--tile N] [--ch C] [--iters K] \
#        [--input file.raw] [--cpu] [--gpu-only]
#
#   MODEL        fp32 DLC (.dlc), context binary (.bin), or model lib (.so).
#                Build an fp32 DLC on x86 first -- see scripts/qnn-gpu-spike.md.
#   --tile N     tile side (default 256)            input = N*N*C fp32
#   --ch C       input channels (default 3)
#   --iters K    timed iterations (default 200; uses (tK - t1)/(K-1))
#   --input f    use this fp32 .raw as the tile (else a random tile is made)
#   --cpu        also run the same model on libQnnCpu.so for an apples-to-apples
#                same-runtime baseline (GPU is on by default)
#   --gpu-only   skip the CPU comparison even if --cpu given
#
# Example (denoise v3, fp32 DLC):
#   bash scripts/qnn-gpu-spike.sh ~/denoise_v3_fp32.dlc --cpu
#
# Copy the full output back for the go/no-go call.

set -u

# ---- pretty -----------------------------------------------------------------
ok()   { printf '  \033[32m[ OK ]\033[0m %s\n' "$*"; }
no()   { printf '  \033[31m[FAIL]\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m[ ?? ]\033[0m %s\n' "$*"; }
hdr()  { printf '\n==== %s ====\n' "$*"; }
die()  { no "$*"; exit 1; }

# ---- args -------------------------------------------------------------------
MODEL=""; TILE=256; CH=3; ITERS=200; INPUT=""; DO_CPU=0; GPU_ONLY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --tile)  TILE="$2"; shift 2;;
    --ch)    CH="$2"; shift 2;;
    --iters) ITERS="$2"; shift 2;;
    --input) INPUT="$2"; shift 2;;
    --cpu)   DO_CPU=1; shift;;
    --gpu-only) GPU_ONLY=1; shift;;
    -h|--help) sed -n '2,40p' "$0"; exit 0;;
    -*) die "unknown flag: $1";;
    *)  MODEL="$1"; shift;;
  esac
done
[ -n "$MODEL" ] || die "no MODEL given (fp32 .dlc / .bin / .so). See --help."
[ -f "$MODEL" ] || die "MODEL not found: $MODEL"
[ "$GPU_ONLY" = 1 ] && DO_CPU=0

# ---- locate QAIRT -----------------------------------------------------------
QROOT="${POLARIS_QAIRT_ROOT:-}"
if [ -z "$QROOT" ]; then
  for c in /opt/polaris/qairt "$HOME/qairt/root" "$HOME/qairt"; do
    [ -x "$c/bin/qnn-net-run" ] && { QROOT="$c"; break; }
  done
fi
[ -n "$QROOT" ] && [ -x "$QROOT/bin/qnn-net-run" ] \
  || die "QAIRT not found (set POLARIS_QAIRT_ROOT; need bin/qnn-net-run). Looked in /opt/polaris/qairt and ~/qairt."
NETRUN="$QROOT/bin/qnn-net-run"
LIBDIR="$QROOT/lib"
ok "QAIRT root: $QROOT"

hdr "Board / runtime"
[ -r /proc/device-tree/model ] && echo "  model:  $(tr -d '\0' </proc/device-tree/model)"
echo "  kernel: $(uname -r)   arch: $(uname -m)"
"$NETRUN" --version 2>/dev/null | sed 's/^/  /' | head -3 || warn "qnn-net-run --version not supported"

# ---- backend libs -----------------------------------------------------------
hdr "1. GPU backend presence + deps"
GPU_BE="$LIBDIR/libQnnGpu.so"
if [ -f "$GPU_BE" ]; then
  ok "libQnnGpu.so -> $GPU_BE"
else
  # maybe it lives elsewhere in the tree
  alt=$(find "$QROOT" -name 'libQnnGpu.so' 2>/dev/null | head -1)
  if [ -n "$alt" ]; then GPU_BE="$alt"; ok "libQnnGpu.so -> $GPU_BE (outside lib/)";
  else die "libQnnGpu.so NOT in this QAIRT bundle -- the GPU lane needs it. \
Re-extract qairt-libs incl. the GPU backend, or use the LiteRT delegate path."; fi
fi
echo "  ldd libQnnGpu.so:"; LD_LIBRARY_PATH="$LIBDIR" ldd "$GPU_BE" 2>&1 | sed 's/^/    /'
if LD_LIBRARY_PATH="$LIBDIR" ldd "$GPU_BE" 2>&1 | grep -qi 'not found'; then
  warn "some libQnnGpu.so deps are 'not found' above -- likely OpenCL (libOpenCL.so / Adreno UMD)."
  warn "install/locate the Adreno OpenCL userspace, else the GPU backend won't load."
fi
CPU_BE="$LIBDIR/libQnnCpu.so"
[ -f "$CPU_BE" ] && ok "libQnnCpu.so -> $CPU_BE (CPU baseline available)" \
  || { warn "libQnnCpu.so missing -- CPU comparison disabled"; DO_CPU=0; }

# ---- model load mode --------------------------------------------------------
# qnn-net-run loads a DLC via the libQnnModelDlc.so model + --dlc_path; a
# context binary via --retrieve_context; a compiled model lib via --model.
DLC_LIB="$LIBDIR/libQnnModelDlc.so"
MODEL_ARGS=()
case "$MODEL" in
  *.dlc)
    [ -f "$DLC_LIB" ] || die "need libQnnModelDlc.so to run a .dlc (not in $LIBDIR)"
    MODEL_ARGS=(--model "$DLC_LIB" --dlc_path "$MODEL")
    ok "model: DLC ($MODEL) via libQnnModelDlc.so" ;;
  *.bin)
    MODEL_ARGS=(--retrieve_context "$MODEL")
    ok "model: context binary ($MODEL)"
    warn "a context binary is backend-locked -- it must have been generated FOR the GPU backend, not HTP." ;;
  *.so)
    MODEL_ARGS=(--model "$MODEL")
    ok "model: compiled model lib ($MODEL)" ;;
  *) die "unrecognized MODEL type (want .dlc / .bin / .so): $MODEL" ;;
esac

# ---- stage input ------------------------------------------------------------
LEN=$(( TILE * TILE * CH ))
BYTES=$(( LEN * 4 ))
WORK="$(mktemp -d /tmp/polaris-qnn-gpu.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
TILE_RAW="$WORK/tile.raw"
if [ -n "$INPUT" ]; then
  [ -f "$INPUT" ] || die "input not found: $INPUT"
  cp "$INPUT" "$TILE_RAW"
  ok "input tile: $INPUT ($(stat -c%s "$TILE_RAW") bytes; expected $BYTES for ${TILE}x${TILE}x${CH} fp32)"
else
  # random fp32 tile in [0,1) -- enough to exercise the graph + time it.
  head -c "$BYTES" /dev/urandom > "$TILE_RAW" 2>/dev/null
  # /dev/urandom bytes are arbitrary fp32 (incl. NaN/Inf); fine for timing, not for value checks.
  ok "synthetic random fp32 tile: ${TILE}x${TILE}x${CH} = $LEN floats ($BYTES bytes)"
  warn "synthetic input is for TIMING + op-support only; it does NOT validate output values."
fi

mk_list() { # mk_list <count> <path>
  : > "$2"; for ((i=0;i<$1;i++)); do echo "$TILE_RAW" >> "$2"; done
}

# ---- timed run --------------------------------------------------------------
# (tK - t1)/(K-1): subtracts the one-time context-load so we get pure per-tile.
run_backend() { # run_backend <name> <backend.so>
  local name="$1" be="$2" out l1 lK t1 tK perf
  hdr "Run on $name ($be)"
  out="$WORK/out_$name"; rm -rf "$out"; mkdir -p "$out"
  l1="$WORK/in1.txt"; lK="$WORK/inK.txt"
  mk_list 1 "$l1"; mk_list "$ITERS" "$lK"

  # warm + correctness/op-support: a single inference. Capture stderr so a
  # missing op (e.g. LayerNorm) or a backend-load failure is visible.
  local log="$WORK/${name}.log"
  if ! LD_LIBRARY_PATH="$LIBDIR" ADSP_LIBRARY_PATH="$QROOT/dsp" \
        "$NETRUN" --backend "$be" "${MODEL_ARGS[@]}" \
        --input_list "$l1" --output_dir "$out" >"$log" 2>&1; then
    no "$name run FAILED -- tail of log:"; tail -25 "$log" | sed 's/^/      /'
    if grep -qiE 'layernorm|unsupported|not supported|reducemean|reducesumsquare' "$log"; then
      warn "looks like an OP-SUPPORT failure on $name (the model uses an op this backend can't lower)."
    fi
    return 1
  fi
  ok "$name produced output ($(find "$out" -name '*.raw' | wc -l) tensor file(s)) -- ops accepted"

  # time: 1 iter then K iters, wall clock (ns) -> ms/tile.
  rm -rf "$out"; mkdir -p "$out"
  t1=$( { /usr/bin/env bash -c "exec 3>&2; \
    s=\$(date +%s%N); LD_LIBRARY_PATH='$LIBDIR' ADSP_LIBRARY_PATH='$QROOT/dsp' \
    '$NETRUN' --backend '$be' ${MODEL_ARGS[*]} --input_list '$l1' --output_dir '$out' >/dev/null 2>&3; \
    e=\$(date +%s%N); echo \$(( (e-s)/1000000 ))"; } 2>/dev/null )
  rm -rf "$out"; mkdir -p "$out"
  tK=$( { /usr/bin/env bash -c "exec 3>&2; \
    s=\$(date +%s%N); LD_LIBRARY_PATH='$LIBDIR' ADSP_LIBRARY_PATH='$QROOT/dsp' \
    '$NETRUN' --backend '$be' ${MODEL_ARGS[*]} --input_list '$lK' --output_dir '$out' >/dev/null 2>&3; \
    e=\$(date +%s%N); echo \$(( (e-s)/1000000 ))"; } 2>/dev/null )

  if [ "$ITERS" -gt 1 ] && [ -n "${t1:-}" ] && [ -n "${tK:-}" ]; then
    perf=$(awk -v a="$t1" -v b="$tK" -v k="$ITERS" 'BEGIN{printf "%.2f", (b-a)/(k-1)}')
    printf '  \033[36m%-4s : 1 tile %s ms | %d tiles %s ms | per-tile %s ms\033[0m\n' \
      "$name" "$t1" "$ITERS" "$tK" "$perf"
    echo "$perf"   # stdout (for caller capture); the colored line goes to terminal too
  else
    warn "could not compute per-tile timing (t1=$t1 tK=$tK iters=$ITERS)"
  fi
  return 0
}

GPU_MS=""; CPU_MS=""
GPU_MS=$(run_backend gpu "$GPU_BE" | tail -1)
if [ "$DO_CPU" = 1 ]; then
  CPU_MS=$(run_backend cpu "$CPU_BE" | tail -1)
fi

# ---- verdict ----------------------------------------------------------------
hdr "VERDICT"
if [ -z "$GPU_MS" ]; then
  no "GPU backend did NOT run the model. If it was an op-support failure (LayerNorm/"
  echo "      ReduceMean), the GPU backend can't host this model -> fall back to the"
  echo "      LiteRT GPU delegate or keep the model on the client browser / CPU."
else
  ok  "GPU backend RAN the model at ~${GPU_MS} ms/tile (fp32, no quantization)."
  [ -n "$CPU_MS" ] && echo "      QNN CPU baseline ~${CPU_MS} ms/tile (same runtime, apples-to-apples)."
  echo "      Compare against the measured onnxruntime fp32 CPU baseline (~4488 ms/tile)."
  echo "      GO if GPU << CPU AND it accepts the NPU-impossible models. Then: add a"
  echo "      libQnnGpu backend option to Services/Qnn (swap the .so + non-quant model),"
  echo "      route v3/decon/star-removal to it, keep HTP for BGE + denoise v2."
fi
echo
echo "  Note: this measures qnn-net-run wall time (incl. per-process I/O), so it is a"
echo "  CONSERVATIVE upper bound -- the in-proc Services/Qnn lane batches a whole image"
echo "  per process, amortizing load far better than these single-tile-list runs."
