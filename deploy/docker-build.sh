#!/usr/bin/env bash
# Build the NINA Headless container image for both linux/amd64 and
# linux/arm64 using Docker Buildx, and push to the configured registry.
#
# Usage:
#     deploy/docker-build.sh [tag]
#
# Defaults to "latest" if no tag is given. Set REGISTRY env var to
# override the default registry prefix.

set -euo pipefail

TAG="${1:-latest}"
REGISTRY="${REGISTRY:-ghcr.io/danwbr}"
IMAGE="${REGISTRY}/nina-headless:${TAG}"

# Ensure buildx is available and a builder exists
if ! docker buildx inspect nina-builder > /dev/null 2>&1; then
    echo "Creating buildx builder 'nina-builder'..."
    docker buildx create --name nina-builder --use
else
    docker buildx use nina-builder
fi

# QEMU emulation for cross-arch builds (no-op if already installed)
docker run --privileged --rm tonistiigi/binfmt --install all > /dev/null 2>&1 || true

echo "Building ${IMAGE} for linux/amd64,linux/arm64..."
docker buildx build \
    --platform linux/amd64,linux/arm64 \
    --tag "${IMAGE}" \
    --push \
    .

echo "Done: ${IMAGE}"
