# Sync Worker

Cloudflare Worker that backs cross-machine observation sync for the Claude-Usage-Projector widget. **Maintainer-only — sync is optional and disabled by default for normal users of the widget.** If you're a colleague trying to run the widget, ignore this directory entirely.

Design rationale: [`../DECISIONS.md`](../DECISIONS.md) ADR-012.
Implementation plan: [`../docs/PHASE-7-PLAN.md`](../docs/PHASE-7-PLAN.md).

## What this is

A ~100-line TypeScript Worker exposing two endpoints behind Cloudflare Access:

```
POST /observations  { observations: [{ ts, account_id, machine_id, used_pct, refresh_at }, ...] }
                    -> { inserted: N, skipped: M }
GET  /observations?since=<unix_seconds>&account_id=<id>&limit=<n>
                    -> { observations: [...], count: N }
GET  /healthz       -> { ok: true }
```

Backed by a Cloudflare D1 database with one `observations` table (see [`schema.sql`](schema.sql)). One row per (account, machine, second).

## One-time setup

You need a Cloudflare account with Workers enabled (Free plan is sufficient). All the dashboard work happens at <https://dash.cloudflare.com> and <https://one.dash.cloudflare.com> (Zero Trust).

### 1. Pick a subdomain

Substitute `sync.your-domain.example` throughout the steps below for whatever subdomain you want the Worker exposed on. The maintainer uses `sync.daisybot.co.uk`.

### 2. Install wrangler locally

```powershell
cd worker
npm install
npx wrangler login
```

### 3. Create the D1 database

```powershell
npx wrangler d1 create claude-usage-sync
```

Wrangler prints a `database_id`. Copy `wrangler.toml.example` to `wrangler.toml` and paste the id where it says `REPLACE_WITH_YOUR_D1_DATABASE_ID`. Also fix the `pattern` route to your subdomain.

### 4. Apply the schema

```powershell
npm run schema:apply
```

### 5. Create Cloudflare Access service tokens

In the **Zero Trust dashboard** → **Access** → **Service Auth** → **Service Tokens** → **Create Service Token**:

- Create one token per machine you want to sync. Use clear names: `claude-usage-work-laptop`, `claude-usage-personal-pc`.
- Default duration (1 year) is fine.
- **Copy the Client ID and Client Secret into a password manager *immediately*** — the secret is shown once and is unrecoverable. You'll need both values for each machine's `sync.env` (see below).

### 6. Create the Access app guarding the sync subdomain

In **Zero Trust** → **Access** → **Applications** → **Add an application** → **Self-hosted**:

- Application domain: `sync.your-domain.example`.
- Identity providers: any (won't be used by service tokens).
- **Policies**: add a single **Service Auth** policy. Action: *Service Auth*. Selector: *Service Token*. Include each service token from step 5.
- Save.

### 7. Deploy the Worker

```powershell
npm run deploy
```

The `custom_domain = true` route binding causes Cloudflare to provision the DNS record under your zone automatically (assuming your domain is on Cloudflare DNS). If your domain is on another DNS host, add a CNAME for the subdomain pointing at your Worker's `*.workers.dev` URL.

### 8. Smoke test

```powershell
# Health check (still requires Access — the service token covers this):
curl -H "CF-Access-Client-Id: <id>.access" -H "CF-Access-Client-Secret: <secret>" https://sync.your-domain.example/healthz

# Empty query should return { observations: [], count: 0 }:
curl -H "CF-Access-Client-Id: <id>.access" -H "CF-Access-Client-Secret: <secret>" "https://sync.your-domain.example/observations?since=0"
```

## Per-machine widget configuration

On each machine running the Claude-Usage-Projector widget, create `%APPDATA%\Claude-Code-Usage-Monitor\predictor\sync.env`:

```
SYNC_URL=https://sync.your-domain.example
SYNC_CLIENT_ID=<service-token-client-id>.access
SYNC_CLIENT_SECRET=<service-token-client-secret>
MACHINE_ID=work-laptop
```

If this file is missing or unreadable, the widget silently disables sync and operates local-only. This is the intentional default for colleagues running the widget.

## Maintenance

```powershell
# Inspect row count + most recent observation:
npx wrangler d1 execute claude-usage-sync --remote --command "SELECT COUNT(*), MAX(ts) FROM observations"

# Per-account row counts:
npx wrangler d1 execute claude-usage-sync --remote --command "SELECT account_id, COUNT(*) FROM observations GROUP BY account_id"

# Export for backup:
npx wrangler d1 export claude-usage-sync --remote --output backup-$(Get-Date -Format yyyyMMdd).sql
```

## What's NOT in here

- No secrets. `wrangler.toml` is gitignored; `wrangler.toml.example` has placeholders. Service token secrets live in your password manager and in each machine's `sync.env` (never committed).
- No JWT validation in the Worker. Cloudflare Access does it in front; we trust headers it passes through.
- No per-account session-timing data. That stays machine-local for sensitivity reasons (see ADR-012).
