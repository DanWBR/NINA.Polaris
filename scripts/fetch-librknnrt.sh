#!/usr/bin/env bash
# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
# Licensed under the GNU Affero General Public License v3.0 or later.
#
# Fetch the Rockchip RKNPU2 user-space runtime (librknnrt.so, aarch64) into
# external/rknpu/aarch64/ so the linux-arm64 publish bundles it next to the app
# for NPU-accelerated GraXpert AI on RK3588 boards. Run once on a checkout that
# will produce a linux-arm64 build/.deb. The library is proprietary Rockchip
# vendor code (see licenses/RKNPU-LICENSE.txt) and is NOT committed to the repo.
set -euo pipefail

VERSION="${1:-v2.3.2}"
REPO="https://github.com/airockchip/rknn-toolkit2/raw"
SRC="${REPO}/${VERSION}/rknpu2/runtime/Linux/librknn_api/aarch64/librknnrt.so"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dest_dir="${here}/../external/rknpu/aarch64"
dest="${dest_dir}/librknnrt.so"

mkdir -p "${dest_dir}"
echo "Fetching librknnrt.so (${VERSION}) -> ${dest}"
if command -v curl >/dev/null 2>&1; then
    curl -fSL "${SRC}" -o "${dest}"
else
    wget -O "${dest}" "${SRC}"
fi

ls -l "${dest}"
echo "Done. The linux-arm64 publish will now bundle librknnrt.so."
