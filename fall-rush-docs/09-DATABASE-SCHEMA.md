# 09 — PostgreSQL Database Schema

PostgreSQL is the operational read model, not the authority for on-chain ownership. Use migrations and foreign keys. Store timestamps in UTC. Store wallet public keys as base58 strings with validation or byte arrays; do not store secrets.

## 1. Tables

### users
- `id UUID PK`
- `created_at TIMESTAMPTZ`
- `status TEXT` (active, suspended, deleted)
- `display_name TEXT UNIQUE NULL`
- `region TEXT NULL`

### wallet_links
- `id UUID PK`
- `user_id UUID FK users`
- `wallet_address TEXT UNIQUE`
- `cluster TEXT`
- `verified_at TIMESTAMPTZ`
- `created_at TIMESTAMPTZ`

### auth_nonces
- `nonce_hash TEXT PK`
- `user_id UUID FK users`
- `expires_at TIMESTAMPTZ`
- `consumed_at TIMESTAMPTZ NULL`

### lobbies
- `id UUID PK`
- `code TEXT UNIQUE`
- `host_user_id UUID FK users`
- `status TEXT`
- `region TEXT`
- `capacity SMALLINT`
- `created_at`, `updated_at TIMESTAMPTZ`

### lobby_members
- `lobby_id UUID FK lobbies`
- `user_id UUID FK users`
- `ready BOOLEAN`
- `joined_at TIMESTAMPTZ`
- `PRIMARY KEY(lobby_id,user_id)`

### matches
- `id UUID PK`
- `chain_match_id BYTEA UNIQUE`
- `status TEXT`
- `ruleset_version INTEGER`
- `map_pool_version INTEGER`
- `roster_hash BYTEA`
- `map_sequence JSONB`
- `vrf_request_id TEXT NULL`
- `vrf_status TEXT`
- `server_region TEXT`
- `server_instance_id TEXT`
- `chain_cluster TEXT`
- `match_account TEXT NULL`
- `er_status TEXT`
- `final_signature TEXT NULL`
- `transcript_hash BYTEA NULL`
- `champion_user_id UUID NULL FK users`
- `started_at`, `completed_at`, `created_at TIMESTAMPTZ`

### match_players
- `match_id UUID FK matches`
- `user_id UUID FK users`
- `wallet_address TEXT NULL`
- `join_status TEXT`
- `placement SMALLINT NULL`
- `round_reached SMALLINT`
- `qualified BOOLEAN`
- `eliminated BOOLEAN`
- `disconnect_count SMALLINT DEFAULT 0`
- `PRIMARY KEY(match_id,user_id)`

### rounds
- `id UUID PK`
- `match_id UUID FK matches`
- `round_number SMALLINT`
- `map_id TEXT`
- `mode TEXT`
- `status TEXT`
- `started_at`, `ended_at TIMESTAMPTZ`
- `qualified_count SMALLINT`
- `UNIQUE(match_id,round_number)`

### match_events (append-only)
- `id UUID PK`
- `match_id UUID FK matches`
- `sequence BIGINT`
- `event_type TEXT`
- `user_id UUID NULL FK users`
- `round_number SMALLINT NULL`
- `payload JSONB`
- `payload_hash BYTEA`
- `server_signature TEXT NULL`
- `chain_signature TEXT NULL`
- `chain_status TEXT`
- `created_at TIMESTAMPTZ`
- `UNIQUE(match_id,sequence)`

### chain_transactions
- `id UUID PK`
- `match_id UUID FK matches`
- `event_id UUID NULL FK match_events`
- `cluster TEXT`
- `program_id TEXT`
- `signature TEXT UNIQUE`
- `kind TEXT`
- `status TEXT`
- `slot BIGINT NULL`
- `error_code TEXT NULL`
- `submitted_at`, `confirmed_at`, `finalized_at TIMESTAMPTZ NULL`

### cosmetics
- `id TEXT PK`
- `name TEXT`
- `asset_standard TEXT`
- `collection_address TEXT NULL`
- `metadata_uri TEXT`
- `rarity TEXT`
- `enabled BOOLEAN`

### user_cosmetics
- `user_id UUID FK users`
- `cosmetic_id TEXT FK cosmetics`
- `asset_address TEXT NULL`
- `source TEXT` (earned, minted, admin_grant)
- `acquired_at TIMESTAMPTZ`
- `PRIMARY KEY(user_id,cosmetic_id,asset_address)`

## 2. Indexes

- `matches(status, created_at DESC)`
- `match_players(user_id, match_id)`
- `match_events(match_id, sequence)` unique
- `chain_transactions(status, submitted_at)`
- `lobbies(status, region, created_at)`
- `wallet_links(wallet_address)` unique

## 3. Data handling

Do not store private keys, seed phrases, raw wallet signatures longer than needed, precise location, or unnecessary personal data. Define retention periods for IP/network diagnostics and support requests. Use parameterized SQL/ORM, least-privilege DB roles, encrypted backups, and migration rollback procedures.
