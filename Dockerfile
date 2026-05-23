# Multi-stage build for N.I.N.A. Polaris.
#
# Builds inside a .NET 10 SDK image, runs on the slim ASP.NET Core runtime.
# Targets both linux/amd64 (mini-PCs, x86 SBCs) and linux/arm64 (Raspberry
# Pi 4/5, Rock Pi, Orange Pi) when invoked with `docker buildx build
# --platform linux/amd64,linux/arm64`.
#
# The mDNS announcer needs raw multicast traffic, which a Docker bridge
# network blocks. For mDNS discovery to work, run the container with
# `--network host` (the default in our docker-compose.yml).

ARG TARGETPLATFORM
ARG BUILDPLATFORM

# ----- Build stage -----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Cache restore: copy only csproj / sln first so subsequent code changes
# don't bust the NuGet cache.
COPY NINA.Polaris.sln ./
COPY src/NINA.Core.Portable/NINA.Core.Portable.csproj src/NINA.Core.Portable/
COPY src/NINA.Image.Portable/NINA.Image.Portable.csproj src/NINA.Image.Portable/
COPY src/NINA.INDI/NINA.INDI.csproj src/NINA.INDI/
COPY src/NINA.Polaris/NINA.Polaris.csproj src/NINA.Polaris/
COPY tests/NINA.Polaris.Test/NINA.Polaris.Test.csproj tests/NINA.Polaris.Test/
RUN dotnet restore src/NINA.Polaris/NINA.Polaris.csproj

# Bring in the rest of the source and publish a self-contained binary.
COPY src/ src/

ARG TARGETARCH
RUN case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        arm)   RID=linux-arm  ;; \
        *)     RID=linux-x64  ;; \
    esac && \
    echo "Publishing for $RID" && \
    dotnet publish src/NINA.Polaris/NINA.Polaris.csproj \
        -c Release \
        -r $RID \
        --self-contained false \
        -o /app/publish

# ----- Runtime stage -----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root user so the container honours least-privilege defaults.
RUN useradd --create-home --shell /bin/bash --uid 1000 nina

COPY --from=build --chown=nina:nina /app/publish ./

# Workstation GC: smaller heap is better for RPi-class machines than the
# server GC's per-CPU heap fragmentation.
ENV DOTNET_gcServer=0 \
    ASPNETCORE_URLS=http://0.0.0.0:5000

# Bind-mounts for persistence — profiles live here, image archive lives here.
RUN mkdir -p /data /images && chown -R nina:nina /data /images

USER nina

EXPOSE 5000

# Health check: ASP.NET Core's default endpoint returns 404 with no body,
# but a successful TCP+HTTP response means the host is up. The image
# stats endpoint is the cheapest "real" route to probe.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s \
    CMD wget -q --spider http://localhost:5000/api/system/status || exit 1

ENTRYPOINT ["./NINA.Polaris"]
