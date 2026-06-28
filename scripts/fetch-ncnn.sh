#!/usr/bin/env bash
# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
# Licensed under the GNU Affero General Public License v3.0 or later.
#
# Stage the ncnn shared runtime (libncnn.so, aarch64, built with NCNN_C_API=ON +
# NCNN_VULKAN=ON) into external/ncnn/aarch64/ so the linux-arm64 publish bundles
# it next to the app for the open Vulkan-GPU GraXpert AI lane (BGE + denoise v2).
# ncnn is Tencent BSD-3 (see licenses/NCNN-LICENSE.txt); the lib is NOT committed.
#
# The Vulkan loader (libvulkan.so.1) is NOT bundled — install it on the device:
#   sudo apt install libvulkan1 mesa-vulkan-drivers vulkan-tools
#
# Usage:
#   NCNN_SO=/path/to/libncnn.so scripts/fetch-ncnn.sh     # copy a lib you built
#   scripts/fetch-ncnn.sh <url-to-libncnn.so>             # download one
# ncnn does not publish a generic linux-arm64 *shared* + Vulkan release, so the
# usual path is to build it once on the board (or cross-compile):
#   git clone --depth 1 https://github.com/Tencent/ncnn && cd ncnn
#   git submodule update --init
#   cmake -B build -DNCNN_VULKAN=ON -DNCNN_SHARED_LIB=ON -DNCNN_C_API=ON \
#         -DNCNN_BUILD_TOOLS=OFF -DNCNN_BUILD_EXAMPLES=OFF -DCMAKE_BUILD_TYPE=Release
#   cmake --build build -j && cp build/src/libncnn.so* <here>/external/ncnn/aarch64/
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dest_dir="${here}/../external/ncnn/aarch64"
mkdir -p "${dest_dir}"

if [[ -n "${NCNN_SO:-}" ]]; then
    echo "Copying ${NCNN_SO} -> ${dest_dir}/"
    cp -av "${NCNN_SO}"* "${dest_dir}/" 2>/dev/null || cp -av "${NCNN_SO}" "${dest_dir}/"
elif [[ $# -ge 1 ]]; then
    echo "Fetching ${1} -> ${dest_dir}/libncnn.so"
    if command -v curl >/dev/null 2>&1; then curl -fSL "${1}" -o "${dest_dir}/libncnn.so";
    else wget -O "${dest_dir}/libncnn.so" "${1}"; fi
else
    echo "No NCNN_SO env or URL arg given. Build libncnn.so (Vulkan + C API) and"
    echo "place it in ${dest_dir}/ — see the header of this script for the cmake."
    exit 1
fi

ls -l "${dest_dir}"
echo "Done. The linux-arm64 publish will now bundle libncnn.so."
