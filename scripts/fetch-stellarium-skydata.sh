#!/usr/bin/env bash
#
# fetch-stellarium-skydata.sh — DEPRECATED no-op.
#
# This script used to recursive-wget a CloudFront URL that we
# (incorrectly) believed mirrored the Stellarium Web HiPS skydata
# bundle. It turned out that URL was the stellarium-web.org SPA
# hosting bucket, not a data mirror — every request returned the
# same 2.9 KB SPA index.html instead of HiPS tiles or properties
# files, so the engine silently rendered an empty sky.
#
# The right path was already sitting inside the engine submodule
# all along: external/stellarium-web-engine/apps/test-skydata/
# contains a complete ~4.6 MB test bundle (Norder 0-N tile pyramids
# for stars + DSOs, Milky Way / Moon / Sun image surveys, the
# guereins horizon landscape, IAU western constellation overlays,
# mpcorb.dat asteroid elements, CometEls.txt comet elements, and a
# satellite TLE feed).
#
# That bundle is now committed in-repo at
# src/NINA.Polaris/wwwroot/sky/data/skydata/ so the sky map works
# offline out of the box. No fetch step required.
#
# This file is kept as a stub so older deployment docs / Windows
# build wrappers that still call fetch-stellarium-skydata.ps1 →
# fetch-stellarium-skydata.sh don't break.
#
# If you ever want to *update* the bundled skydata (e.g. after
# pulling a newer submodule commit), copy the submodule directory:
#
#   cp -r external/stellarium-web-engine/apps/test-skydata/. \
#         src/NINA.Polaris/wwwroot/sky/data/skydata/
#   git add src/NINA.Polaris/wwwroot/sky/data/skydata/
#
# For a true full-fat Stellarium Web data mirror (mag 12+ Gaia stars,
# higher-Norder Milky Way tiles, etc.), point window.__skyDataBase
# in the iframe at a separately-hosted HiPS mirror — that's an
# out-of-band deployment concern, not something this script can do.

set -e
echo "ℹ fetch-stellarium-skydata.sh is a no-op; data ships in-repo."
echo "  See src/NINA.Polaris/wwwroot/sky/data/skydata/"
exit 0
