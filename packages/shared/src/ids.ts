/**
 * Chain IDs and endpoints (doc 07 §2). Values were read from the SDK 0.16.2 source
 * (`ephemeral-vrf-sdk 0.4.1/src/consts.rs`) and the magicblock-examples `scripts/local-env.sh`.
 */
export const FALL_RUSH_PROGRAM_ID = "FRuSHPLACEHOLDER"; // replaced by `anchor keys sync` output (see scripts)

export const DELEGATION_PROGRAM_ID = "DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh";
export const MAGIC_PROGRAM_ID = "Magic11111111111111111111111111111111111111";
export const MAGIC_CONTEXT_ID = "MagicContext1111111111111111111111111111111";
export const VRF_PROGRAM_ID = "Vrf1RNUjXmQGjmQrQLvJHs9SNkvDJEsRVFPkfSQUwGz";

export const NETWORKS = {
  localnet: {
    solanaRpc: "http://localhost:8899",
    solanaWs: "ws://localhost:8900",
    erRpc: "http://localhost:7799",
    erWs: "ws://localhost:7800",
    validator: "mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev",
    vrfQueue: "Sc9MJUngNbQXSXGP3F67KvKwVnhaYn6kcioxXNVowYT", // DEFAULT_EPHEMERAL_TEST_QUEUE
  },
  devnet: {
    solanaRpc: "https://api.devnet.solana.com",
    solanaWs: "wss://api.devnet.solana.com",
    erRpc: "https://devnet-as.magicblock.app",
    erWs: "wss://devnet-as.magicblock.app",
    validator: "MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57",
    vrfQueue: "5hBR571xnXppuCPveTrctfTU7tJLSN94nq7kv7FRK5Tc", // DEFAULT_EPHEMERAL_QUEUE
  },
} as const;
