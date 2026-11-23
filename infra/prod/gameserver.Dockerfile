# Fall Rush game server, P5: Build.LinuxDevServer (Linux x86_64 Player subtarget + FALLRUSH_DEV_SERVER, run with
# -fallrushServer). Based on the M3 image (ubuntu:22.04 + ca-certificates + user fallrush, infra/gameserver.Dockerfile):
# no apt step on the VPS (an apt-get in the build hung there, 2026-11-23). Build context = the server build folder.
FROM fallrush-gameserver:m3
USER root
WORKDIR /opt/fallrush
RUN rm -rf /opt/fallrush/FallRushServer_Data /opt/fallrush/FallRushServer.x86_64 /opt/fallrush/UnityPlayer.so
COPY --chown=fallrush:fallrush FallRushServer.x86_64 UnityPlayer.so libdecor-0.so.0 libdecor-cairo.so ./
COPY --chown=fallrush:fallrush FallRushServer_Data ./FallRushServer_Data
USER fallrush
ENV GAMESERVER_PORT=7777 GAMESERVER_ID=gs
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/${GAMESERVER_PORT}' || exit 1
ENTRYPOINT ["./FallRushServer.x86_64", "-batchmode", "-nographics", "-fallrushServer", "-logFile", "-"]
