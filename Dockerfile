# syntax=docker/dockerfile:1.7

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.302-noble-aot@sha256:977c117b98abe301eeb4c2ed3f8deebf8b8f11e65549ee7886581d2b41641adb
ARG RUNTIME_BASE_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:01bd3f3d617a16705f896962ce26f5c1e1588ea57c71c98908cadb7bdcd0cd4d
ARG CHISEL_BUILD_IMAGE=ubuntu:noble@sha256:4fbb8e6a8395de5a7550b33509421a2bafbc0aab6c06ba2cef9ebffbc7092d90
ARG GODOT_RUNTIME_BASE_IMAGE=ubuntu:noble@sha256:4fbb8e6a8395de5a7550b33509421a2bafbc0aab6c06ba2cef9ebffbc7092d90
ARG UBUNTU_RELEASE=24.04
ARG CHISEL_VERSION=v1.4.1

FROM ${DOTNET_SDK_IMAGE} AS build

ARG SOURCE_DATE_EPOCH=0

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_XMLDOC_MODE=skip \
    SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH}

WORKDIR /src

COPY . .

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Fgvm.Cli/Fgvm.Cli.csproj -c Release -o /app/bin

RUN mkdir -p /app/runtime-root/fgvm /app/runtime-root/workspace \
 && chown -R 1654:1654 /app/runtime-root/fgvm /app/runtime-root/workspace

FROM ${RUNTIME_BASE_IMAGE} AS runtime-base

# Build the slim rootfs used by the default image.
FROM --platform=$BUILDPLATFORM ${CHISEL_BUILD_IMAGE} AS chisel-runtime

ARG BUILDARCH
ARG TARGETARCH
ARG UBUNTU_RELEASE
ARG CHISEL_VERSION

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/*

ADD "https://github.com/canonical/chisel/releases/download/${CHISEL_VERSION}/chisel_${CHISEL_VERSION}_linux_${BUILDARCH}.tar.gz" \
    chisel.tar.gz
RUN case "${BUILDARCH}" in \
      amd64) chisel_sha384=e3f8c1a8461848d6c2f051f1edd07a9446349f69d1cce40a5a58bb4d7b58eaf10e2ae94de320254a2fca55095d9753db ;; \
      arm64) chisel_sha384=e2f09056be917965530ad750c263211578ef4d48fe2561325f788774ae253a66b29c31a9ea1bd93a024639f57c263c12 ;; \
      *) echo "unsupported BUILDARCH: ${BUILDARCH}" >&2; exit 1 ;; \
    esac \
 && echo "${chisel_sha384}  chisel.tar.gz" | sha384sum -c - \
 && tar -xzf chisel.tar.gz -C /usr/bin/ chisel \
 && rm chisel.tar.gz

COPY --from=runtime-base / /rootfs/

# Keep Chisel metadata with the sliced rootfs.
RUN chisel cut --release "ubuntu-${UBUNTU_RELEASE}" --arch "${TARGETARCH}" --root /rootfs \
      base-files_chisel \
      dash_bins \
      libfontconfig1_libs \
      fontconfig-config_config

# Default slim runtime image.
FROM scratch AS runtime

LABEL org.opencontainers.image.title="fgvm" \
      org.opencontainers.image.description="A friendly Godot version manager." \
      org.opencontainers.image.source="https://github.com/patricktcoakley/fgvm" \
      org.opencontainers.image.licenses="MIT"

ENV FGVM_HOME=/fgvm

COPY --link --from=chisel-runtime /rootfs /
COPY --link --from=build /app/bin/fgvm /usr/local/bin/fgvm
COPY --link --from=build --chown=1654:1654 /app/runtime-root/fgvm /fgvm
COPY --link --from=build --chown=1654:1654 /app/runtime-root/workspace /workspace

USER 1654:1654

WORKDIR /workspace
ENTRYPOINT ["fgvm"]

# Runtime image with Godot's Linux dependencies.
FROM ${GODOT_RUNTIME_BASE_IMAGE} AS runtime-godot

LABEL org.opencontainers.image.title="fgvm-godot" \
      org.opencontainers.image.description="A friendly Godot version manager with Godot Linux runtime dependencies." \
      org.opencontainers.image.source="https://github.com/patricktcoakley/fgvm" \
      org.opencontainers.image.licenses="MIT"

ENV FGVM_HOME=/fgvm \
    HOME=/workspace \
    XDG_CONFIG_HOME=/workspace/.config \
    XDG_DATA_HOME=/workspace/.local/share \
    DEBIAN_FRONTEND=noninteractive

# Unpinned: each apt pocket indexes only the newest version, so `pkg=<exact>` stops resolving as soon
# as a revision lands. Use a snapshot.ubuntu.com timestamp if reproducible versions are ever needed.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      libssl3t64 \
      libfontconfig1 \
      libx11-6 \
      libxcursor1 \
      libxext6 \
      libxi6 \
      libxinerama1 \
      libxrandr2 \
      libxrender1 \
      libwayland-client0 \
      libwayland-cursor0 \
      libwayland-egl1 \
      libdecor-0-0 \
      libxkbcommon0 \
      libgl1 \
      libegl1 \
      libvulkan1 \
      mesa-vulkan-drivers \
      libasound2t64 \
      libpulse0 \
      libudev1 \
      libdbus-1-3 \
      xvfb \
      xauth \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/bin/fgvm /usr/local/bin/fgvm

RUN mkdir -p /fgvm /workspace \
 && chown -R 1654:1654 /fgvm /workspace

USER 1654:1654

WORKDIR /workspace
ENTRYPOINT ["fgvm"]
