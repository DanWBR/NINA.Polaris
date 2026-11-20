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

# The project targets net10.0 and publishes with the .NET 10 SDK. The GitHub
# runners also carry a newer SDK, and because the repo doesn't have a
# global.json, `dotnet` resolves to the newest one, where a net10.0
# browser-wasm publish fails (NETSDK1147 for the workload, then NETSDK1102 on
# trimming). So, while this script runs, a temporary global.json at the repo
# root pins SDK 10; it is removed on exit and never written over a real one.
global_json="$repo/global.json"
if [[ "$(dotnet --version | cut -d. -f1)" -gt 10 && ! -e "$global_json" ]]; then
    printf '{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }\n' > "$global_json"
    trap 'rm -f "$global_json"' EXIT
fi
cd "$repo"
echo "==> Installing the wasm-tools workload (SDK $(dotnet --version))"
dotnet workload install wasm-tools

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
