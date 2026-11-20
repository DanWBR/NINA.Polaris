#!/usr/bin/env bash
# Build the NINA.Polaris.Wasm bundle and stage the Files viewer's assets in
# www/files/vendor, for the CI app builds (android.yml, ios.yml).
#
# The Files page decodes FITS files in the app with no Polaris host, through
# the browser-wasm module. The module is a build output, so a fresh checkout
# does not have it: this installs the wasm-tools workload, publishes the
# module, mirrors it to src/NINA.Polaris/wwwroot/js/wasm (where
# deploy/build-wasm.ps1 puts it on Windows) and then runs
# copy-viewer-assets.mjs, which fails if the bundle is still missing.
#
# Needs the .NET 10 SDK on PATH (actions/setup-dotnet). Run from mobile/
# before `cap add` / `cap sync`, which copy www/ into the native projects.

set -euo pipefail

mobile="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo="$(cd "$mobile/.." && pwd)"
proj="$repo/src/NINA.Polaris.Wasm/NINA.Polaris.Wasm.csproj"
bundle="$repo/src/NINA.Polaris.Wasm/bin/Release/net10.0/browser-wasm/AppBundle"
wwwroot_wasm="$repo/src/NINA.Polaris/wwwroot/js/wasm"

# The project targets net10.0. Under the .NET 10 SDK that needs the
# "wasm-tools" workload, but the GitHub runners also carry a newer SDK, and
# because the repo doesn't have a global.json, `dotnet` resolves to the
# newest one. Under SDK 11 "wasm-tools" means net11, and net10.0 needs
# "wasm-tools-net10" instead (NETSDK1147 otherwise).
sdk_major="$(dotnet --version | cut -d. -f1)"
if [[ "$sdk_major" -gt 10 ]]; then workload="wasm-tools-net10"; else workload="wasm-tools"; fi
echo "==> Installing the $workload workload (SDK $(dotnet --version))"
dotnet workload install "$workload"

echo "==> Publishing NINA.Polaris.Wasm"
dotnet publish "$proj" -c Release --nologo

if [[ ! -f "$bundle/main.js" ]]; then
    echo "ERROR: $bundle/main.js is missing after the publish" >&2
    exit 1
fi

rm -rf "$wwwroot_wasm"
mkdir -p "$wwwroot_wasm"
cp -R "$bundle/." "$wwwroot_wasm/"

cd "$mobile"
VIEWER_ASSETS_REQUIRE_WASM=1 node scripts/copy-viewer-assets.mjs
