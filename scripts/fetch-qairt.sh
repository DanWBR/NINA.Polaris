#!/usr/bin/env bash
# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
# Licensed under the GNU Affero General Public License v3.0 or later.
#
# Assemble the Qualcomm AI Runtime (QAIRT, formerly QNN) aarch64 runtime into
# external/qairt/aarch64/{bin,lib,dsp} so the linux-arm64 publish bundles it at
# /opt/polaris/qairt for NPU-accelerated GraXpert AI on Qualcomm SBCs (Radxa
# Dragon Q6A / QCS6490, Hexagon V68). The runtime is proprietary Qualcomm vendor
# code (see licenses/QAIRT-LICENSE.txt) and is NOT committed to the repo.
#
# Unlike librknnrt.so there is no public download for the device-matched runtime:
# the only public x86 SDK is QAIRT 2.31, which is version-LOCKED against the
# device's 2.45 firmware and will NOT load on the board (1002 "Create From Binary
# failure" / 1008 "Transport layer setup failed"). The 2.45 aarch64 runtime comes
# from the board itself (the qcom apt packages already installed there) or from
# the matching 2.45 SDK. So this script COPIES from a source root you provide
# rather than downloading.
#
# Usage:
#   ./scripts/fetch-qairt.sh <QAIRT_SOURCE_ROOT>
#   QAIRT_SRC=/path/to/qairt ./scripts/fetch-qairt.sh
#
# QAIRT_SOURCE_ROOT is any directory tree that contains the device-matched
# aarch64 runtime files. Both layouts are handled (we locate files by name):
#   * a 2.45 QAIRT SDK install   (lib/aarch64-*/, lib/hexagon-v68/unsigned/, bin/aarch64-*/)
#   * the board's extracted tree (e.g. scp'd /opt/.../qairt or the qcom .debs unpacked)
#
# To pull straight from the board first, e.g.:
#   rsync -a polaris@<board>:~/qairt/ /tmp/qairt-src/    # or scp the relevant dirs
#   ./scripts/fetch-qairt.sh /tmp/qairt-src
set -euo pipefail

SRC="${1:-${QAIRT_SRC:-}}"
if [ -z "${SRC}" ]; then
    echo "ERROR: no QAIRT source root given." >&2
    echo "Usage: $0 <QAIRT_SOURCE_ROOT>   (or QAIRT_SRC=...)" >&2
    exit 1
fi
if [ ! -d "${SRC}" ]; then
    echo "ERROR: source root '${SRC}' is not a directory." >&2
    exit 1
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dest="${here}/../external/qairt/aarch64"
mkdir -p "${dest}/bin" "${dest}/lib" "${dest}/dsp"

# Host (CPU-side) libraries qnn-net-run dlopens, plus the tool itself. The HTP
# backend (libQnnHtp.so) pulls in the Stub/Prepare/System libs and the NetRun
# extensions provide the unsigned-PD config path used on this board.
TOOLS=(qnn-net-run)
LIBS=(libQnnHtp.so libQnnSystem.so libQnnHtpV68Stub.so libQnnHtpPrepare.so libQnnHtpNetRunExtensions.so)
# DSP-side skel runs ON the Hexagon (V68). The unsigned variant matches the
# unsigned-PD exec path Polaris uses.
SKELS=(libQnnHtpV68Skel.so)

# Find the first match by name under SRC, preferring an aarch64/hexagon-v68 path
# when several copies exist (the SDK ships x86 + multiple aarch64 toolchains).
find_one() {
    local name="$1" prefer="$2" hit=""
    # Prefer a path containing the hint, else any match.
    hit="$(find "${SRC}" -type f -name "${name}" 2>/dev/null | grep -i "${prefer}" | head -n1 || true)"
    if [ -z "${hit}" ]; then
        hit="$(find "${SRC}" -type f -name "${name}" 2>/dev/null | head -n1 || true)"
    fi
    printf '%s' "${hit}"
}

missing=0
copy() {  # name destsubdir preferhint
    local name="$1" sub="$2" prefer="$3"
    local src; src="$(find_one "${name}" "${prefer}")"
    if [ -z "${src}" ]; then
        echo "  MISSING: ${name} (not found under ${SRC})" >&2
        missing=1
        return
    fi
    cp -f "${src}" "${dest}/${sub}/${name}"
    echo "  ${sub}/${name}  <-  ${src}"
}

echo "==> Assembling QAIRT aarch64 runtime into ${dest}"
for t in "${TOOLS[@]}"; do copy "${t}" bin "aarch64"; done
for l in "${LIBS[@]}";  do copy "${l}" lib "aarch64"; done
for s in "${SKELS[@]}"; do copy "${s}" dsp "hexagon-v68"; done

chmod 0755 "${dest}/bin/"* 2>/dev/null || true

echo ""
if [ "${missing}" -ne 0 ]; then
    echo "WARNING: some files were not found. The NPU path needs all of them;" >&2
    echo "         point QAIRT_SOURCE_ROOT at a complete 2.45 aarch64 runtime." >&2
    exit 2
fi
echo "Done. The linux-arm64 publish will now bundle /opt/polaris/qairt."
echo "Contents:"
find "${dest}" -type f -printf '  %P\n' | sort
