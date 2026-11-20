# Fall Rush dedicated game server (Unity 6000.3 Linux x86_64, Dedicated Server subtarget, Mono).
# Build context: client-unity/Builds/Server  (output of `pnpm m1:build:server`)
#   docker build --platform linux/amd64 -f infra/gameserver.Dockerfile -t fallrush-gameserver:m1 client-unity/Builds/Server
FROM --platform=linux/amd64 ubuntu:22.04

# Runtime deps found necessary for the Unity 6000.3 Linux server player: see docs/proof/M1-transport.md.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --create-home --uid 10001 --shell /usr/sbin/nologin fallrush

WORKDIR /opt/fallrush
# Explicit copies: never ship "<Product>_BurstDebugInformation_DoNotShip".
COPY --chown=fallrush:fallrush FallRushServer.x86_64 UnityPlayer.so ./
COPY --chown=fallrush:fallrush FallRushServer_Data ./FallRushServer_Data

USER fallrush
ENV GAMESERVER_PORT=7777 \
    GAMESERVER_ID=gs-local
EXPOSE 7777

# Cheap TCP probe (bash /dev/tcp): the UTP WebSocket listener accepts the socket; no handshake is sent.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/${GAMESERVER_PORT}' || exit 1

ENTRYPOINT ["./FallRushServer.x86_64", "-batchmode", "-nographics", "-logFile", "-"]
