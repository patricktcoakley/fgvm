# syntax=docker/dockerfile:1.7

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.300-noble-aot@sha256:761c281a91d9d46b1e7c8c0ff5d22d251a175b245d1970e37833d3cf08092a3c
ARG DOTNET_RUNTIME_DEPS_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0.8-noble@sha256:02cfde646311bfe8a6332681091b323215451cc1beec65afe5e6d1dbb05117c8

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

FROM ${DOTNET_RUNTIME_DEPS_IMAGE} AS runtime

LABEL org.opencontainers.image.title="fgvm" \
      org.opencontainers.image.description="Godot version manager." \
      org.opencontainers.image.source="https://github.com/patricktcoakley/fgvm" \
      org.opencontainers.image.licenses="MIT"

ENV FGVM_HOME=/fgvm

COPY --from=build /app/bin/ /usr/local/bin/

RUN mkdir -p /fgvm /workspace

WORKDIR /workspace
ENTRYPOINT ["fgvm"]
