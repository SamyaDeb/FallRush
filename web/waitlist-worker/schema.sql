-- Fall Rush waitlist (Cloudflare D1 / SQLite)
CREATE TABLE IF NOT EXISTS waitlist (
  seq          INTEGER PRIMARY KEY AUTOINCREMENT,   -- join order
  code         TEXT    NOT NULL UNIQUE,             -- public invite code, e.g. BOLT-7K2Q
  email        TEXT    NOT NULL UNIQUE,             -- never returned by the API
  rushling     TEXT    NOT NULL,                    -- Bolt | Splash | Sunny | Minty | Grape
  referred_by  TEXT,                                -- code of the friend who invited them
  referrals    INTEGER NOT NULL DEFAULT 0,          -- friends who joined with this code
  token_hash   TEXT    NOT NULL,                    -- sha-256 of the poster upload token
  has_poster   INTEGER NOT NULL DEFAULT 0,
  created_at   TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_waitlist_referred_by ON waitlist(referred_by);
