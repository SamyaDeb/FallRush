# 09 — PostgreSQL Schema (Rush Relay)

**Status:** v2 (2026-11-18). This is a trimmed version of v1.

**Rules:**
- Postgres is the operational read model and the outbox. It is **not** the authority for on-chain results or ownership.
- Migrations live in `server/migrations/NNN_name.sql`. They are forward-only, and a rollback script ships with each one.
- All timestamps are UTC `timestamptz`.

```sql
CREATE TABLE users (
  id            uuid PRIMARY KEY,
  display_name  text NOT NULL,                       -- generated "BoltyCapsule42", editable, profanity-filtered
  cosmetic_preset smallint NOT NULL DEFAULT 0,
  xp            integer NOT NULL DEFAULT 0,
  created_at    timestamptz NOT NULL DEFAULT now(),
  status        text NOT NULL DEFAULT 'active' CHECK (status IN ('active','suspended'))
);

CREATE TABLE wallet_links (
  user_id        uuid PRIMARY KEY REFERENCES users(id),
  wallet_address text NOT NULL UNIQUE,                -- base58, validated
  verified_at    timestamptz NOT NULL
);

CREATE TABLE auth_nonces (
  nonce_hash  text PRIMARY KEY,                       -- sha256(nonce)
  user_id     uuid NOT NULL REFERENCES users(id),
  expires_at  timestamptz NOT NULL,
  consumed_at timestamptz
);

CREATE TABLE game_servers (
  id text PRIMARY KEY, ws_url text NOT NULL, state text NOT NULL,
  humans smallint NOT NULL DEFAULT 0, capacity smallint NOT NULL, last_seen timestamptz NOT NULL
);

CREATE TABLE matches (
  id               uuid PRIMARY KEY,                  -- = on-chain match_id bytes
  server_id        text NOT NULL,
  status           text NOT NULL,                     -- canonical phase (doc 05 §4)
  ranked           boolean NOT NULL,
  ruleset_version  smallint NOT NULL,
  map_pool_version smallint NOT NULL,
  roster_hash      bytea NOT NULL,
  match_pda        text NOT NULL,
  vrf_seed         bytea,
  deal             jsonb,                             -- {maps, variants, modifiers}
  champion_slot    smallint,
  transcript_hash  bytea,                             -- server-computed; compared to on-chain
  chain_state      text NOT NULL DEFAULT 'none',      -- none|delegated|dealing|live|committing|final|degraded|aborted
  final_signature  text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  completed_at     timestamptz
);
CREATE INDEX ON matches (created_at DESC);

CREATE TABLE match_players (
  match_id uuid REFERENCES matches(id), slot smallint NOT NULL,
  user_id uuid REFERENCES users(id),                  -- null for bots
  is_bot boolean NOT NULL, wallet_address text,
  final_state text, round_reached smallint, placement smallint, reason smallint,
  PRIMARY KEY (match_id, slot)
);
CREATE INDEX ON match_players (user_id);

CREATE TABLE match_events (                           -- the outbox (append-only)
  match_id uuid REFERENCES matches(id), seq integer NOT NULL,
  type text NOT NULL, round smallint, slot smallint, payload jsonb NOT NULL,
  server_transcript bytea NOT NULL,
  chain_status text NOT NULL DEFAULT 'pending',       -- pending|submitted|confirmed|committed|failed|skipped
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (match_id, seq)
);
CREATE INDEX ON match_events (chain_status) WHERE chain_status IN ('pending','submitted');

CREATE TABLE chain_txs (
  signature text PRIMARY KEY, match_id uuid REFERENCES matches(id), seq integer,
  layer text NOT NULL CHECK (layer IN ('solana','er')), kind text NOT NULL,
  status text NOT NULL, slot bigint, error text,
  submitted_at timestamptz NOT NULL DEFAULT now(), confirmed_at timestamptz
);

CREATE TABLE fan_picks (                              -- guest picks (wallet picks live on-chain, mirrored here)
  match_id uuid REFERENCES matches(id), round smallint, user_id uuid REFERENCES users(id),
  slot smallint NOT NULL, onchain_signature text, correct boolean,
  PRIMARY KEY (match_id, round, user_id)
);

CREATE TABLE trophies (
  match_id uuid PRIMARY KEY REFERENCES matches(id), user_id uuid REFERENCES users(id),
  wallet_address text NOT NULL, asset_address text, mint_signature text, status text NOT NULL
);
```

**Not stored:**
- private keys
- seed phrases
- raw IPs beyond transient request logs
- emails

Wallet signatures are only verified, never persisted.
