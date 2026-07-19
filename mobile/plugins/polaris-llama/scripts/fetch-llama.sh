#!/usr/bin/env bash
# Stage the llama.cpp Android arm64 release into the plugin's jniLibs so the
# app packages an executable llama-server (+ its .so deps). Run before
# `npx cap sync android` / the Android build; the binaries are never committed.
#
#   ./fetch-llama.sh [tag]          # default tag = the eval-validated build
#
# The eval (canopus-eval/MOBILE.md) validated b10058. Bump the tag deliberately.
set -euo pipefail

TAG="${1:-b10058}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../android/src/main/jniLibs/arm64-v8a"
ASSET="llama-$TAG-bin-android-arm64.zip"
URL="https://github.com/ggml-org/llama.cpp/releases/download/$TAG/$ASSET"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Downloading $URL"
curl -fL "$URL" -o "$TMP/$ASSET"
mkdir -p "$TMP/ex" "$OUT"
unzip -q "$TMP/$ASSET" -d "$TMP/ex"

# All shared libs ship as-is; the server executable is renamed lib*.so so
# Android puts it in nativeLibraryDir (the only exec-allowed place, W^X).
find "$TMP/ex" -name '*.so' -exec cp -f {} "$OUT/" \;
SERVER="$(find "$TMP/ex" -name 'llama-server' -type f | head -1)"
[ -n "$SERVER" ] || { echo "ERROR: llama-server not found in $ASSET" >&2; exit 1; }
cp -f "$SERVER" "$OUT/libllamaserver.so"

echo "Staged into $OUT:"
ls -1 "$OUT"
