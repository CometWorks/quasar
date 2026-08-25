# syntax=docker/dockerfile:1

FROM debian:bookworm-slim AS release

ARG QUASAR_VERSION

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && case "$QUASAR_VERSION" in ''|*[!0-9A-Za-z.-]*) echo "Invalid QUASAR_VERSION" >&2; exit 1;; esac \
    && release_url="https://github.com/CometWorks/quasar/releases/download/v${QUASAR_VERSION}" \
    && curl -fsSL "$release_url/quasar-web-linux-x64.tar.gz" -o /tmp/quasar-web-linux-x64.tar.gz \
    && curl -fsSL "$release_url/SHA256SUMS" -o /tmp/SHA256SUMS \
    && cd /tmp \
    && grep ' quasar-web-linux-x64.tar.gz$' SHA256SUMS | sha256sum --check --strict - \
    && mkdir /opt/quasar \
    && tar -xzf quasar-web-linux-x64.tar.gz -C /opt/quasar

FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG QUASAR_VERSION

LABEL org.opencontainers.image.title="Quasar" \
      org.opencontainers.image.description="Space Engineers dedicated server supervisor" \
      org.opencontainers.image.source="https://github.com/CometWorks/quasar" \
      org.opencontainers.image.version="$QUASAR_VERSION"

RUN dpkg --add-architecture i386 \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        lib32gcc-s1 \
        lib32stdc++6 \
        procps \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/quasar
COPY --from=release /opt/quasar/ ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    QUASAR_CONSOLE_LOGGING=true \
    QUASAR_INSTALL_DIR=/data \
    QUASAR_MODE=service \
    QUASAR_OPEN_BROWSER_ON_START=false \
    QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN=false \
    QUASAR_UPDATES_ENABLED=false

RUN mkdir -p /data

VOLUME ["/data"]
EXPOSE 8080
EXPOSE 27016/udp

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/api/health >/dev/null || exit 1

ENTRYPOINT ["/opt/quasar/Quasar"]
