# 16 — Official References and Research Notes

Research snapshot: 18 November 2026. Links and SDK APIs can change; re-check before implementation.

## MagicBlock / V10

- Solana Blitz V10 event and theme: https://luma.com/hedv5sgh
- MagicBlock Build portal, Blitz eligibility and criteria: https://build.magicblock.app/?stage=blitz
- MagicBlock documentation: https://docs.magicblock.gg/
- Ephemeral Rollups SDK (Rust + TypeScript packages, compatibility notes): https://github.com/magicblock-labs/ephemeral-rollups-sdk
- Delegation program and lifecycle instructions: https://github.com/magicblock-labs/delegation-program
- MagicBlock Engine examples: https://github.com/magicblock-labs/magicblock-engine-examples
- Solana VRF implementation, callback flow, queues, security notes: https://github.com/magicblock-labs/solana-vrf

## Solana / Anchor

- Solana core concepts: https://solana.com/docs/core
- Solana transactions and limits: https://solana.com/docs/core/transactions
- Solana RPC overview and commitment: https://solana.com/docs/rpc
- Solana RPC providers: https://solana.com/docs/rpc/providers
- Solana transaction landing / RPC infrastructure: https://solana.com/docs/tools/transaction-landing
- Solana security: https://solana.com/docs/tools/security
- Anchor program structure: https://www.anchor-lang.com/docs/basics/program-structure
- Anchor PDA basics: https://www.anchor-lang.com/docs/basics/pda
- Anchor account constraints: https://www.anchor-lang.com/docs/references/account-constraints

## Unity

- Unity multiplayer overview: https://docs.unity.com/en-us/multiplayer
- Unity Netcode package comparison: https://docs.unity.com/en-us/multiplayer/netcode/netcode
- Competitive multiplayer quickstart (server-authoritative): https://docs.unity.com/en-us/multiplayer/quickstarts/competitive-quickstart

## Assets

- Metaplex Core overview: https://www.metaplex.com/docs/smart-contracts/core
- Metaplex Core create asset: https://www.metaplex.com/docs/smart-contracts/core/create-asset

## Research conclusions

1. V10 is Entertainment Finance: trade, collect, compete, win assets owned by players; examples named by event are BananaZone, Slimecoin and Jungle Fun.
2. MagicBlock Build portal states every Blitz submission must integrate an Ephemeral Rollup or VRF; general selection criteria are creativity, technical depth, and compelling Solana demonstration. V10 page does not publish a detailed numeric scoring rubric in the event text reviewed.
3. MagicBlock ER SDK provides Rust and TypeScript tools for account delegation, ER execution, and committing state back to Solana. Delegated and ephemeral-only accounts have different lifecycles.
4. MagicBlock Solana VRF uses request + oracle proof + on-chain verification + callback; invalid proof is rejected. It works on Solana and ER with different queue constants. Validate callback signer.
5. Unity's current competitive quickstart uses a dedicated authoritative server; clients send gameplay inputs, server computes gameplay-relevant movement and transmits it back.
6. Metaplex Core is a suitable candidate for cosmetic NFTs; confirm current program/SDK and royalty semantics before use.
7. Solana public RPC endpoints are shared and not intended for production traffic; use private RPC with tested fallback.

## Important qualification

This package is an engineering blueprint, not a substitute for reading current docs, legal advice, a smart-contract audit, or performance testing. Package versions, program IDs, MagicBlock endpoints, queue addresses, event deadlines and eligibility conditions must be verified from current official sources before use. Do not treat illustrative code structures or proposed schemas as already deployed interfaces.
