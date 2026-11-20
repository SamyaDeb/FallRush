#!/usr/bin/env bash
# Self-signed cert for https://localhost:8443/8444 (M1 local test only). Not installed in any trust store;
# Chrome shows one interstitial per origin. Output dir: $M1_CERT_DIR (default ./.m1-certs, git-ignored).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIR="${M1_CERT_DIR:-$ROOT/.m1-certs}"
mkdir -p "$DIR"
if [[ -s "$DIR/localhost.crt" && -s "$DIR/localhost.key" ]]; then
  echo "[m1] cert exists: $DIR/localhost.crt"; exit 0
fi
openssl req -x509 -newkey rsa:2048 -nodes -days 30 \
  -keyout "$DIR/localhost.key" -out "$DIR/localhost.crt" \
  -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" 2>/dev/null
chmod 644 "$DIR/localhost.crt" "$DIR/localhost.key"   # read by nginx inside the container; local test cert only
echo "[m1] wrote $DIR/localhost.crt"
