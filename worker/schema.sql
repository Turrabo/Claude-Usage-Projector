-- D1 schema for the claude-usage-sync database.
-- Apply via `wrangler d1 execute claude-usage-sync --remote --file=schema.sql`.

-- Append-only observation log, partitioned implicitly by (account, machine).
-- Composite primary key lets concurrent writers from different machines
-- coexist without collisions; INSERT OR IGNORE makes retries idempotent.
CREATE TABLE IF NOT EXISTS observations (
    ts         INTEGER NOT NULL,            -- Unix seconds
    account_id TEXT    NOT NULL,            -- "acct_" + first 12 hex of SHA-256(jwt.sub)
    machine_id TEXT    NOT NULL,            -- stable per-machine identifier (e.g. "work-laptop")
    used_pct   REAL,                        -- 5-hour bucket % at this observation
    refresh_at INTEGER,                     -- Unix seconds when the 5-hour bucket resets
    PRIMARY KEY (account_id, machine_id, ts)
);

-- Index for time-range queries across all accounts (the popover's "what's
-- happening on other machines lately" query).
CREATE INDEX IF NOT EXISTS idx_observations_ts
    ON observations(ts);

-- Index for per-account time-range queries (the chart's per-account
-- history query).
CREATE INDEX IF NOT EXISTS idx_observations_account_ts
    ON observations(account_id, ts);
