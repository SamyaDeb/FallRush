# shellcheck shell=sh
# Local MagicBlock cluster endpoints (copied from magicblock-examples@101e8fd scripts/local-env.sh).
export PROVIDER_ENDPOINT=http://localhost:8899
export WS_ENDPOINT=ws://localhost:8900
export EPHEMERAL_PROVIDER_ENDPOINT=http://localhost:7799
export EPHEMERAL_WS_ENDPOINT=ws://localhost:7800
export QFS_ENDPOINT=http://localhost:6699
export QFS_WS_ENDPOINT=ws://localhost:6700
export ANCHOR_PROVIDER_URL=$PROVIDER_ENDPOINT
export ANCHOR_WALLET="${HOME}/.config/solana/id.json"
export VALIDATOR=mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev
export VRF_BASE_QUEUE="GKE6d7iv8kCBrsxr78W3xVdjGLLLJnxsGiuzrsZCGEvb"
export VRF_EPHEMERAL_QUEUE="Sc9MJUngNbQXSXGP3F67KvKwVnhaYn6kcioxXNVowYT"
