#!/usr/bin/env bash
# N.I.N.A. Polaris - OpenCL capability check
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
# AGPL-3.0-or-later.
#
# Verifies the board exposes a usable OpenCL GPU so the Polaris OpenCL compute
# backend (Services/OpenCl) can offload classic image kernels. Analogous to
# scripts/fetch-librknnrt.sh for the NPU. The vendor user-space driver ships in
# the board BSP, not with Polaris:
#   - Radxa Dragon Q6A / QCS6490 .... Qualcomm Adreno driver (libOpenCL via ICD)
#   - RK3588 / RK356x ............... libmali (Mali OpenCL)
#   - Raspberry Pi (VideoCore) ...... no production OpenCL -> feature stays off
#
# Exit 0 = OpenCL GPU present; 1 = not present (Polaris will run on the CPU).

set -u

echo "== Polaris OpenCL check =="

# 1) ICD loader present?
loader=""
for cand in libOpenCL.so.1 libOpenCL.so; do
    if ldconfig -p 2>/dev/null | grep -q "$cand"; then loader="$cand"; break; fi
done
if [ -z "$loader" ]; then
    echo "FAIL: no OpenCL ICD loader (libOpenCL.so) found."
    echo "  Install one, e.g.:  sudo apt-get install ocl-icd-libopencl1"
    echo "  Then install your board's OpenCL driver (Adreno BSP / libmali)."
    exit 1
fi
echo "OK:   ICD loader present ($loader)"

# 2) Any ICD registered?
if [ -d /etc/OpenCL/vendors ] && ls /etc/OpenCL/vendors/*.icd >/dev/null 2>&1; then
    echo "OK:   ICD vendor file(s): $(ls /etc/OpenCL/vendors/*.icd | tr '\n' ' ')"
else
    echo "WARN: no /etc/OpenCL/vendors/*.icd - the GPU driver may not be registered."
fi

# 3) clinfo, if available, is the authoritative check.
if command -v clinfo >/dev/null 2>&1; then
    gpus=$(clinfo 2>/dev/null | grep -ci "Device Type.*GPU" || true)
    if [ "${gpus:-0}" -ge 1 ]; then
        name=$(clinfo 2>/dev/null | grep -m1 -i "Device Name" | sed 's/.*: *//')
        echo "OK:   OpenCL GPU device found: ${name:-unknown}"
        exit 0
    fi
    echo "FAIL: clinfo reports no GPU device."
    exit 1
fi

echo "WARN: clinfo not installed (sudo apt-get install clinfo) - cannot confirm a"
echo "      GPU device. Polaris probes at startup and falls back to CPU if absent."
exit 0
