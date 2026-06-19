#!/usr/bin/env bash
# qnn-probe.sh -- QNN-0 spike tool (read-only).
#
# Inspects a Qualcomm SBC (e.g. Radxa Dragon Q6A / QCS6490 on Armbian) to
# decide whether the Hexagon NPU (HTP) is usable for the planned QNN-NPU
# backend (see PLAN.md "QNN-NPU"). Touches nothing -- it only reads files,
# devices, and library paths and prints a go/no-go summary.
#
# Three things must all be present for the QNN/HTP path:
#   1. cDSP firmware blobs        (usually shipped in linux-firmware)
#   2. FastRPC bridge             (/dev/fastrpc-cdsp + libcdsprpc.so)
#   3. QNN SDK user-space libs    (libQnnHtp*.so -- usually a SEPARATE
#                                  Qualcomm AI Engine Direct download)
# The GPU/OpenCL path (our existing backend) is checked too, since that
# also depends on Qualcomm/Mesa userspace on a community distro.
#
# Usage:  bash scripts/qnn-probe.sh
# Copy the full output back for the go/no-go call.

set -u
ok()   { printf '  \033[32m[ OK ]\033[0m %s\n' "$*"; }
no()   { printf '  \033[31m[MISS]\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m[ ?? ]\033[0m %s\n' "$*"; }
hdr()  { printf '\n==== %s ====\n' "$*"; }

# Search roots for vendor .so libraries (covers Debian/Armbian multiarch
# + common Qualcomm vendor drop locations).
LIBDIRS="/usr/lib /usr/lib/aarch64-linux-gnu /lib/aarch64-linux-gnu \
/usr/local/lib /opt /vendor/lib64 /usr/lib/rfsa/adsp /dsp"

findlib() { # findlib <glob> ; echoes first match or empty
    find $LIBDIRS -name "$1" -print 2>/dev/null | head -1
}

hdr "Board / SoC / kernel"
[ -r /proc/device-tree/model ] && echo "  model:  $(tr -d '\0' </proc/device-tree/model)"
[ -r /proc/device-tree/compatible ] && echo "  compat: $(tr '\0' ' ' </proc/device-tree/compatible)"
echo "  kernel: $(uname -r)   arch: $(uname -m)"
command -v lsb_release >/dev/null && echo "  distro: $(lsb_release -ds 2>/dev/null)"

hdr "1. cDSP firmware + remoteproc"
fw=$(find /lib/firmware /vendor/firmware -iname '*cdsp*' 2>/dev/null | head -5)
if [ -n "$fw" ]; then ok "cDSP firmware blob(s):"; echo "$fw" | sed 's/^/        /'
else no "no *cdsp* firmware under /lib/firmware (NPU can't boot without it)"; fi
rp=$(grep -il 'cdsp\|q6\|turing' /sys/class/remoteproc/remoteproc*/name 2>/dev/null)
if [ -n "$rp" ]; then
    for n in $rp; do d=$(dirname "$n");
        echo "        $(cat "$n") -> state=$(cat "$d/state" 2>/dev/null)"; done
    ok "cDSP remoteproc node present (want state=running)"
else warn "no cdsp remoteproc node (may appear only once firmware loads)"; fi
pgrep -x rmtfs >/dev/null && ok "rmtfs running" || warn "rmtfs not running (some images need it for DSP)"
pgrep -x pd-mapper >/dev/null && ok "pd-mapper running" || warn "pd-mapper not running"

hdr "2. FastRPC bridge"
fr=$(ls /dev/fastrpc-cdsp /dev/fastrpc-cdsp-secure /dev/cdsp* 2>/dev/null)
if [ -n "$fr" ]; then ok "FastRPC device(s): $fr"; else no "no /dev/fastrpc-cdsp (FastRPC kernel driver/node missing)"; fi
l=$(findlib 'libcdsprpc.so*'); [ -n "$l" ] && ok "libcdsprpc.so: $l" || no "libcdsprpc.so missing (FastRPC userspace)"

hdr "3. QNN / SNPE SDK user-space libs"
got_qnn=0
for g in 'libQnnHtp.so*' 'libQnnHtpV*Stub.so*' 'libQnnSystem.so*' 'libQnnCpu.so*' 'libQnnGpu.so*' 'libSNPE.so*'; do
    l=$(findlib "$g"); if [ -n "$l" ]; then ok "$g -> $l"; got_qnn=1; else no "$g not found"; fi
done
# HTP skel runs ON the DSP, lives under the rfsa adsp dir
sk=$(find /usr/lib/rfsa /dsp /vendor -iname 'libQnnHtpV*Skel.so*' 2>/dev/null | head -3)
[ -n "$sk" ] && { ok "HTP skel(s):"; echo "$sk" | sed 's/^/        /'; } || warn "no libQnnHtpV*Skel.so (DSP-side stub; needed at runtime)"
[ "$got_qnn" = 0 ] && warn "QNN libs absent -> likely a separate Qualcomm AI Engine Direct (QNN SDK) download; not shipped by stock Armbian"

hdr "4. ONNX Runtime (QNN EP host side)"
python3 - <<'PY' 2>/dev/null || warn "python3/onnxruntime not importable (only needed if we drive QNN EP from Python in the spike)"
import onnxruntime as ort
print("        onnxruntime", ort.__version__)
print("        providers:", ort.get_available_providers())
print("        QNN EP present:", "QNNExecutionProvider" in ort.get_available_providers())
PY

hdr "GPU / OpenCL (existing backend sanity)"
if command -v clinfo >/dev/null; then
    clinfo -l 2>/dev/null | sed 's/^/  /' || warn "clinfo ran but listed no platforms"
else warn "clinfo not installed (apt install clinfo) -- can't confirm OpenCL platforms"; fi
for g in 'libOpenCL.so*' 'libGLES*.so*' 'libgbm.so*'; do l=$(findlib "$g"); [ -n "$l" ] && ok "$g -> $l"; done
ls /etc/OpenCL/vendors/*.icd >/dev/null 2>&1 && { ok "OpenCL ICDs:"; ls /etc/OpenCL/vendors/*.icd | sed 's/^/        /'; } || warn "no /etc/OpenCL/vendors/*.icd"

hdr "VERDICT"
echo "  NPU usable iff all of: cDSP firmware (1) + FastRPC (2) + QNN libs (3)."
echo "  If (1)/(2) present but (3) missing: grab the Qualcomm QNN SDK and"
echo "  drop libQnnHtp*.so (+ matching Skel on the DSP path); then re-run."
echo "  If (1) missing: wrong/incomplete BSP image -- NPU won't boot at all."
echo "  Next: time ONE GraXpert denoise tile via ORT QNN EP (HTP) vs the"
echo "  Adreno OpenCL path vs CPU, and record ms/tile (QNN-0 deliverable)."
