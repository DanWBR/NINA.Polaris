#!/usr/bin/env bash
# N.I.N.A. Polaris - fetch the DSS Color HiPS survey for OFFLINE sky imagery.
#
# The Stellarium Web Engine sky map ships with catalogs only (stars, DSO
# points, constellation lines). The "real nebulae/galaxies you can see"
# background comes from the DSS Color HiPS, which upstream streams from
# CDS Strasbourg at runtime. That needs a live connection.
#
# This script downloads that HiPS pyramid into the bundled skydata dir so
# the map shows real sky imagery with NO network at use time (ASIAIR
# style). sky-bridge.js auto-detects the local bundle (probes
# surveys/dss/properties) and prefers it over the remote URL.
#
# Size scales steeply with the max HEALPix order (each order is 4x the
# previous). Pick the ceiling that fits your SBC card:
#
#   order  tiles(cumulative)   approx size   look
#   ----   -----------------   -----------   ----------------------------------
#   3      ~1020               ~30 MB        big objects recognisable, soft zoom
#   4      ~4100               ~110 MB       most DSOs recognisable (good value)
#   5      ~16400              ~400 MB       detailed, ASIAIR-like
#   6      ~65500              ~1.5 GB       very detailed (overkill for framing)
#
# Usage:
#   scripts/fetch-stellarium-dss.sh [MAX_ORDER] [PARALLEL]
#     MAX_ORDER  highest HEALPix order to fetch (default 4)
#     PARALLEL   concurrent downloads (default 8)
#
# Resumable: existing tiles are skipped, so re-running tops up to a higher
# order or recovers from an interrupted run. Source + attribution:
#   DSS Color, STScI/NASA, HEALPixed by CDS Strasbourg
#   https://alasky.cds.unistra.fr/DSS/DSSColor
# Licensed for non-commercial/research use; mirrored locally for offline
# field use, same data the upstream engine already streams.
set -euo pipefail

MAX_ORDER="${1:-4}"
PARALLEL="${2:-8}"
REMOTE="https://alasky.cds.unistra.fr/DSS/DSSColor"

# Resolve repo root from this script's location so it works from anywhere.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$SCRIPT_DIR/../src/NINA.Polaris/wwwroot/sky/data/skydata/surveys/dss"
mkdir -p "$DEST"

echo "DSS Color -> $DEST  (max order $MAX_ORDER, $PARALLEL parallel)"

# curl wrapper: retry, fail soft (a missing tile in a sparse survey is
# normal — not every HEALPix cell has imagery), skip if already present.
fetch_one() {
    local url="$1" out="$2"
    [ -s "$out" ] && return 0
    mkdir -p "$(dirname "$out")"
    curl -fsS --retry 3 --retry-delay 1 -m 60 -o "$out.part" "$url" 2>/dev/null \
        && mv "$out.part" "$out" || { rm -f "$out.part"; return 0; }
}
export -f fetch_one

# Root metadata + the moc / allsky helpers the engine reads first.
for meta in properties Moc.fits; do
    fetch_one "$REMOTE/$meta" "$DEST/$meta"
done

for ((order=0; order<=MAX_ORDER; order++)); do
    ntiles=$((12 * (4 ** order)))
    echo "  order $order: $ntiles tiles"
    # Allsky mosaic (orders 0-3 only in DSS Color).
    if [ "$order" -le 3 ]; then
        fetch_one "$REMOTE/Norder$order/Allsky.jpg" "$DEST/Norder$order/Allsky.jpg"
    fi
    # Emit "url<TAB>outpath" lines and fan out to xargs for parallelism.
    {
        for ((npix=0; npix<ntiles; npix++)); do
            dir=$(( (npix / 10000) * 10000 ))
            printf '%s\t%s\n' \
                "$REMOTE/Norder$order/Dir$dir/Npix$npix.jpg" \
                "$DEST/Norder$order/Dir$dir/Npix$npix.jpg"
        done
    } | xargs -P "$PARALLEL" -d '\n' -I{} bash -c '
        line="{}"; url="${line%%	*}"; out="${line##*	}"; fetch_one "$url" "$out"'
done

total=$(find "$DEST" -type f | wc -l)
size=$(du -sh "$DEST" | cut -f1)
echo "Done. $total files, $size in $DEST"
echo "Commit with Git LFS (already tracked in .gitattributes):"
echo "  git add $DEST && git commit -m 'skydata: bundle DSS Color HiPS (order $MAX_ORDER)'"
