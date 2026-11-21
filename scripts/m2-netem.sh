#!/usr/bin/env bash
# LOCAL MEASUREMENT ONLY (M2 gate condition c): a TCP relay with Linux tc-netem in a Docker container, between the
# client and the Mac dev server. WebSocket is TCP, so this is where latency, jitter and loss have to be injected
# (Unity's network simulator is a UTP pipeline stage and does nothing for WebSockets).
#
#   client ── ws://127.0.0.1:7778 ──▶ [container: netem on eth0 egress + socat] ──▶ host.docker.internal:7777 (Mac dev server)
#
# netem applies to the container's egress, i.e. once per direction: client→server data leaves toward the server,
# server→client data leaves toward the client. "delay 75ms" therefore adds ~150 ms RTT; "loss 2%" drops 2 % of TCP
# segments in each direction, which TCP retransmits — the application sees head-of-line stalls, never a lost message.
#
# usage: scripts/m2-netem.sh start [delayMs=75] [jitterMs=15] [lossPct=2]   |   stop   |   status
set -euo pipefail
NAME=fallrush-m2-netem
IMAGE=alpine:3.20.3@sha256:1e42bbe2508154c9126d48c2b8a75420c3544343bf86fd041fb7527e017a4b4a
case "${1:-status}" in
  start)
    D="${2:-75}"; J="${3:-15}"; L="${4:-2}"
    JIT=""; [[ "$J" != "0" ]] && JIT="${J}ms distribution normal"  # netem rejects a distribution with zero jitter
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    docker run -d --name "$NAME" --cap-add NET_ADMIN -p 127.0.0.1:7778:7778 --cpus 0.5 --memory 128m "$IMAGE" sh -c "
        for i in 1 2 3 4 5; do apk add --no-cache iproute2 socat >/dev/null && break; sleep 3; done && command -v socat >/dev/null &&
        tc qdisc add dev eth0 root netem delay ${D}ms ${JIT} loss ${L}% &&
        tc qdisc show dev eth0 &&
        exec socat ${SOCAT_DEBUG:-} TCP-LISTEN:7778,fork,reuseaddr,nodelay TCP:host.docker.internal:${NETEM_UPSTREAM_PORT:-7777},nodelay" >/dev/null
    for _ in $(seq 1 30); do docker logs "$NAME" 2>&1 | grep -q netem && break; sleep 1; done
    sleep 2 # socat starts right after the qdisc line
    docker logs "$NAME" 2>&1 | tail -2
    echo "netem relay ws://127.0.0.1:7778 → host.docker.internal:${NETEM_UPSTREAM_PORT:-7777} (delay ${D}±${J} ms per direction, loss ${L}%)" ;;
  stop) docker rm -f "$NAME" >/dev/null 2>&1 && echo stopped || echo "not running" ;;
  status) docker ps --filter "name=$NAME" --format '{{.Names}} {{.Status}} {{.Image}}' ;;
esac
