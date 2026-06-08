#!/usr/bin/env bash
# Stamp source-file license headers for the AGPL-3.0 relicensing proposal.
#
#   MPL Exhibit A (with N.I.N.A. copyright) -> files DERIVED from N.I.N.A.
#   AGPL-3.0                                 -> files ORIGINAL to Polaris.
#
# Idempotent: files already containing "Mozilla Public License" or "GNU Affero"
# in their first lines are skipped. Generated/designer files are skipped.
#
# The DERIVED list is a DIRECTORY-LEVEL DEFAULT and a guess - VERIFY per file.
# Removing/omitting the MPL notice on N.I.N.A.-derived code is an MPL violation.
#
# Usage:  scripts/apply-license-headers.sh            # dry-run
#         scripts/apply-license-headers.sh --apply    # write headers
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
apply=0; [ "${1:-}" = "--apply" ] && apply=1

# Directories whose .cs files are (assumed) derived from N.I.N.A. -> MPL header.
derived_dirs=(
  "src/NINA.Core.Portable"
  "src/NINA.Image.Portable"
  "src/NINA.Guider.Portable"
)

mpl_header='// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging '"'"'N'"'"' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient'"'"'s option, pursuant to MPL-2.0 section 3.3.
'

agpl_header='// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.
'

is_derived() {
  local rel="$1"
  for d in "${derived_dirs[@]}"; do
    case "$rel" in "$d"/*) return 0 ;; esac
  done
  return 1
}

mpl=0; agpl=0; skipped=0
while IFS= read -r -d '' f; do
  case "$f" in */obj/*|*/bin/*) continue ;; esac
  case "$f" in *.g.cs|*.Designer.cs|*AssemblyInfo.cs|*GlobalUsings.cs) continue ;; esac
  head_txt="$(head -n 6 "$f" 2>/dev/null || true)"
  case "$head_txt" in
    *"Mozilla Public License"*|*"GNU Affero"*) skipped=$((skipped+1)); continue ;;
  esac
  rel="${f#"$root"/}"
  if is_derived "$rel"; then hdr="$mpl_header"; tag="MPL "; mpl=$((mpl+1));
  else hdr="$agpl_header"; tag="AGPL"; agpl=$((agpl+1)); fi
  echo "[$tag] $rel"
  if [ "$apply" = "1" ]; then
    printf '%s\n%s' "$hdr" "$(cat "$f")" > "$f.tmp" && mv "$f.tmp" "$f"
  fi
done < <(find "$root/src" "$root/tests" -name '*.cs' -print0)

echo ""
if [ "$apply" = "1" ]; then echo "APPLIED: MPL=$mpl AGPL=$agpl already-headed(skipped)=$skipped";
else echo "DRY-RUN: MPL=$mpl AGPL=$agpl already-headed(skipped)=$skipped"; echo "Re-run with --apply to write."; fi
