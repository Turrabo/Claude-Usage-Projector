# Phase 7 — Multi-auth + cross-machine sync (forward plan)

> Forward-direction plan only. Completed sub-milestones move to git log / `DECISIONS.md` per CLAUDE.md's doc-lifecycle rule. Retire this file once Phase 7 is shipped.

This phase turns the widget from a one-account, one-machine local view into a multi-account view fed by all of the user's machines, with sync over a personal Cloudflare Worker. The architectural decisions are written down in [`DECISIONS.md`](../DECISIONS.md) ADR-011 (multi-auth) and ADR-012 (sync). This plan covers the *order of work*; it isn't a duplicate of the design.

## Goal in one line

After Phase 7, the widget's badge on either machine shows risk + runout for the Claude account currently active on that machine, and the hover popover shows the same for all three accounts across both machines, with sub-second propagation between machines via the maintainer's Cloudflare Worker.

## Sub-milestones

Six small testable steps. Order is roughly dependency-driven; some can run in parallel.

### 7a — IPC v:2 + per-account state in the predictor

Predictor refactor only. No UI change, no sync. The widget should still look single-account afterwards because only the active account on the local machine is producing data.

Originally scoped as one sub-milestone; split during implementation into three commits for review cadence:

**7a.foundation (shipped, commit `57fedbf`)** — IPC v:2 + active-account detection.
- IPC bumped `v: 1 → v: 2` on both `predictor/Ipc/Messages.cs` and `src/csm/ipc.rs`.
- `account_id` field added to `ObserveMessage` (host→predictor) and `PredictionMessage` (predictor→host).
- New module `src/csm/account_id.rs` derives a stable opaque `account_id = "acct_" + sha256(jwt.sub).hex[:12]` from `~/.claude/credentials.json`. Reads on demand and caches by file mtime; no FileSystemWatcher (deferred — credentials are re-read every poll cycle naturally).
- `EXPECTED_PREDICTOR_VERSION` bumped 0.5.0 → 0.6.0 so the existing version-handshake catches v:1↔v:2 stale pairings (commit `08e52f2` introduced the handshake).
- `sha2` crate added to `Cargo.toml`; base64url decode is hand-rolled in `src/csm/account_id.rs` so we didn't pull in a second crate.

**7a.3 (shipped, commit `b15e011`)** — per-account state in the predictor.
- `predictor/Program.cs`: `ObservationWindow` and `Tier1WeightedBurnRate` are now both `Dictionary<AccountId, …>`, lazily populated. Each account gets its own Tier1 instance because the idle-rate cache is internal and stateful — sharing across accounts would smear one account's frozen rate onto another's prediction (caught by Checkpoint 2 reviewer A).
- `TelemetryWindow`, `JsonlActivityDetector`, `MonteCarloProjectionEngine`, `DefaultHawkesIntensityScaler` remain shared (machine-scoped per ADR-011's JsonlTail carve-out).
- Predictor emits the active `account_id` on every `PredictionMessage`. Backfill at startup goes to sentinel `acct_default`.
- Host's `format_prediction` log line now includes `acct=…` so the diagnose log shows routing.

**7a.4 (shipped, commit `ca6e285`)** — host-side `prediction_store` keyed by account.
- `src/csm/prediction_store.rs` refactored: `PredictionStore` now holds `HashMap<AccountId, AccountState>` where each `AccountState` carries its own `latest + history`. `push()` routes by `msg.account_id`, falling back to the `acct_default` sentinel (matching the predictor's own fallback) so a v:1↔v:2 mispairing or an unreadable credentials.json still deposits observations somewhere reachable.
- `snapshot()` resolves the active account via `account_id::current_account_id()` and returns just that bucket; `badge.rs` and `popup.rs` are unchanged. Multi-account UI is Phase 7c.
- A private `snapshot_for_account()` helper backs both `snapshot()` and the unit tests; Phase 7c will promote it (or a sibling `accounts()` enumerator) to feed the per-account popover table.
- `LatestPrediction` and `HistoryEntry` structs unchanged — only the container shape changed, as planned.
- `pub const DEFAULT_ACCOUNT_ID: &str = "acct_default"` added in `src/csm/account_id.rs` so the host and predictor agree on the sentinel string from a single Rust home (matched by `Program.cs`'s `DefaultAccountId` literal — cross-language drift risk noted, but the IPC contract is the canonical source).
- 7 unit tests added covering account routing, the missing-account-id fallback, cross-account isolation (sequential and interleaved), the `tier=0` backfill carve-out, the empty-store path, and per-account history-limit independence.

**Acceptance for 7a as a whole** (now testable): launch the widget, run `claude login` to switch accounts on a single machine, observe in the diagnose log that observations get attributed to the new `account_id` within a few seconds AND that the badge/popup keep showing the active account's data without seeing the older account's stale projection bleed through.

### 7b — Persistence sharding + one-time migration

**7b (shipped, commits `1ccdd5e` + `e89b6ee`)** — per-account history shards + first-observe legacy migration.

- `predictor/Persistence/HistoryJsonlWriter.cs` now takes an `accountId` constructor arg and writes to `history-<account_id>.jsonl`; rotation produces `history-<account_id>-<unix>.jsonl`. One writer instance per account, lazy-allocated in `Program.cs:HandleObserve`. Writer constructor validates the accountId against `^[A-Za-z0-9_]+$` to keep filenames inside the persistence root — a future protocol bug or hand-edited credential shipping `"../../evil"` would be rejected at the type boundary rather than silently escaping.
- `HistoryJsonlReader.LoadAllByAccount` globs `history-*.jsonl`, parses the accountId from the filename (with the row's `account_id` field as fallback), and returns a per-account dict of time-ordered snapshots. The legacy un-sharded `history.jsonl` is intentionally skipped — `LegacyHistoryMigrator` owns it.
- `PersistedSnapshot` schema bump v:1 → v:2 adds nullable `account_id`. v:1 rows still parse cleanly; the migrator stamps them with the active id at first observe and re-serialises as v:2.
- New `LegacyHistoryMigrator`: on first observe, if `history.jsonl` is present, read all rows, tag with the observe's `account_id`, append to `history-<active>.jsonl`, rename source to `history.jsonl.pre-multi-auth-backup`. The migrator validates accountId, attempts only once per process (so a write-succeeded-but-rename-failed state doesn't trigger duplicating retries), and preserves any prior backup by timestamp-suffixing the new one if the canonical path is already taken. The post-migration log line calls out that "if you used multiple Claude accounts before this version, all their prior history is now attributed to whichever account was active on this first observe."
- `CsmSqliteMigrator` no longer takes a `HistoryJsonlWriter` dep; writes directly to the legacy un-sharded path so its rows enter the multi-account world via the 7b migrator's first-observe re-shard.
- `Program.cs` rewires for per-account writers (`Dictionary<string, HistoryJsonlWriter>`), per-account backfill emission at startup, first-observe migration triggering, and Add-loop seeding (not `Seed`, which would clobber the active account's window if startup had already loaded shard rows for it — caught by reviewer-pass commit `e89b6ee`).
- 21 unit tests added across `HistoryJsonlRoundTripTests` + new `LegacyHistoryMigratorTests` + updated `CsmSqliteMigratorTests`: per-account routing (sequential and interleaved), filename regex (current shard, rotated shard, unparseable filename → row fallback), legacy migration round-trip, idempotency, append-to-pre-existing-shard, prior-backup preservation, attempted-once flag, multi-account collapse acknowledgement, accountId validation rejection. 85/85 passing.
- **Smoke test deferred**: isolating the smoke run to a non-real `%APPDATA%` would need a new `CCUM_PERSISTENCE_ROOT` env-var override because `Environment.SpecialFolder.ApplicationData` queries the Windows shell rather than the `APPDATA` env var. Implementer chose scope discipline; the integration code follows the established 7a.3 dictionary pattern and the unit-test suite covers the new failure modes.

**Acceptance** (now testable): a Phase 6 install can be upgraded in place; the popover chart continues to show the user's full prior history under the active account from ~1s after widget launch, with no gap.

### 7c — UI: popover per-account table

**7c (shipped, commits `e70e3a3` + `6c9adb6`)** — per-account table above the chart.

- `src/csm/popup.rs` POPUP_HEIGHT grew 160 → 260 to fit a new 104-px table strip above the existing chart. Each row carries an active-account marker (small filled risk-coloured square on the left), the display name, a coloured pill with the current used%, and the projected runout time (HH:MMam/pm if it lands before the session refresh; otherwise "—"). The active account is pinned at row 0; remaining accounts follow in alphabetical order. Cap is `MAX_TABLE_ROWS = 4`; when more accounts exist, the last row becomes a "+N more" footer so the count remains discoverable without the strip overflowing into the chart.
- New `src/csm/aliases.rs`: per-account display-name resolver. Reads `%APPDATA%\Claude-Code-Usage-Monitor\account-aliases.json` (host-side cross-cutting root, *not* the predictor's `predictor\` subfolder, and *not* `sync.env` — that's a separate file for Worker credentials). Map shape is `{"acct_<12hex>": "<friendly name>"}`. Missing/empty/unparseable → fall back to a short form like `acct_abc123de…`. mtime-cached so paint ticks are cheap. Char-boundary-safe truncation so a hand-edited file with non-ASCII keys doesn't panic the paint path.
- `src/csm/prediction_store.rs` gains a public `accounts()` enumerator (stable alphabetical order so the table doesn't flicker between paint ticks) and promotes `snapshot_for_account` to `pub` so the popup can pull per-row latest predictions without going through the active-account resolution.
- `acct_default` (the IPC fallback bucket) is suppressed from the visible list whenever at least one real account is present, so it doesn't read as a phantom row.
- Long aliases get `DT_END_ELLIPSIS` clipping rather than mid-glyph chops.
- Badge in `src/csm/badge.rs` continues to show only the active account's risk + runout — no UI change there, per plan.
- 9 unit tests added across `aliases` (5 — including non-ASCII robustness) and `prediction_store::accounts` (3) plus other follow-ups. 28/28 Rust tests passing.

**Deferred to 7e** (intentional per the plan): the "(this machine)" / "(other machine)" tag. Pre-sync every observation is local, so the tag would always read "(this machine)" — uninformative until cross-machine rows start arriving via the Worker. 7e adds it when it adds the sync layer.

**Acceptance** (now testable): with 3 accounts populated in `prediction_store`, the popover shows a clean 3-row table with the active account pinned at top + dim inactive rows below; the badge is unchanged.

### 7d — Cloudflare Worker deployment

This is mostly the maintainer's dashboard work, plus a `wrangler deploy`. The source code is committed in this repo at `worker/`.

- **Maintainer one-time setup** (Cloudflare dashboard, see `worker/README.md`):
  - Workers & Pages → confirm Free plan.
  - D1 → Create database `claude-usage-sync`.
  - Zero Trust → Service Auth → Service Tokens → create one per machine (`claude-usage-work-laptop`, `claude-usage-personal-pc`).
  - Zero Trust → Access → Applications → Add `sync.daisybot.co.uk` with a Service Auth policy including both tokens.
- **From this repo**:
  - `cd worker && wrangler d1 execute claude-usage-sync --remote --file=schema.sql` (apply schema).
  - `wrangler deploy` (deploys the Worker code; Custom Domain on `sync.daisybot.co.uk` provisions DNS automatically).
- Acceptance: `curl -H "CF-Access-Client-Id: …" -H "CF-Access-Client-Secret: …" https://sync.daisybot.co.uk/observations?since=0` returns `{"observations": []}` with 200; an authenticated POST writes a row that a subsequent GET retrieves.

Estimated effort: 1 day mostly user-facing.

### 7e — Predictor sync integration

- `predictor/Sync/CloudflareSync.cs` — new module. Loads `sync.env` from `%APPDATA%\Claude-Code-Usage-Monitor\predictor\sync.env`; if missing, sync stays disabled.
- `HttpClient` configured with `CF-Access-Client-Id` and `CF-Access-Client-Secret` headers per request.
- After each local observation lands and is appended to the per-account JSONL shard, fire-and-forget POST to the Worker. Buffer locally if the POST fails; flush on next tick.
- 30-second timer: GET observations since the highest seen `ts` from each non-local-machine, deserialise, feed into the relevant `ObservationWindow` (de-duped by `(account_id, machine_id, ts)`).
- Sync state (last-synced timestamp per `account_id`) persists in `predictor/Persistence/SyncState.cs` → `sync-state.json` in the same `%APPDATA%` folder.
- Failure modes:
  - 401/403 from Worker (bad token): log loudly, disable sync, surface a `predictor[warn]` line.
  - Network unreachable: silently retry on next tick; don't spam the log.
- Acceptance: with both machines running, a fresh observation on machine A appears in machine B's popover within ~30 seconds. Kill the Worker (or unplug network) and the widget keeps working with local-only data.

Estimated effort: 3–5 days.

### 7f — Polish + docs + release

- `docs/BUILD.md`: setup steps for sync (Cloudflare dashboard + `sync.env` template), labelled clearly as optional / power-user / maintainer-only.
- `CLAUDE.md` Phase 7 row marked ✅ shipped (and this `PHASE-7-PLAN.md` retired — content already lives in commits + ADRs).
- Auto-memory: update `project_fork_pivot.md` to reflect multi-auth + sync. Add a new memory if the active-account detection logic has surprises worth recording.
- Tag a release: `v0.6.0` (or whatever the next round number is).
- Acceptance: a maintainer-style clean-machine setup (per the docs) gets a working multi-account sync'd widget.

Estimated effort: 1 day.

## Dependencies summary

```
7a ──┬──> 7b
     ├──> 7c
     ├──> 7e (also depends on 7d)
     └──> 7f (last)
7d (independent, can ship anytime after the design is firm)
```

Total estimated effort: roughly 10–15 dev-days, ~3 calendar weeks at part-time.

## Open implementation questions (resolve during the work, not before)

- **What does "account name" UI mapping look like configurationally?** A sidecar file? A new IPC message from host? Default of `acct_xxxxxxxx` is ugly. Probably a small TOML/JSON in `%APPDATA%`.
- **What's the JWT-`sub` claim's stability across token refreshes?** Need to verify it persists when the Claude CLI refreshes the OAuth token automatically.
- **Should the cross-machine sync also push the Hawkes-state-pending `state.json`?** ADR-012 says no (sensitivity). Confirm by re-reading the ADR before implementing 7e — easy to drift.
- **Should the badge still cycle to a different account if the active account is `unknown` (e.g. user hasn't logged in to Claude CLI on this machine)?** Default: show "—" / "—" placeholder. Maybe make it explicit.

## What does NOT happen in Phase 7

- **No predictor learning loops.** That's Phase 8 — see CLAUDE.md. Multi-auth + sync is the prerequisite.
- **No retroactive cross-machine Hawkes.** Hawkes parameters fit per-(account, machine) pair on locally observed JSONL events only.
- **No upstream-widget changes.** The badge and popover are entirely in `src/csm/`, no new sentinel sites in upstream files.
- **No release-packaging changes for end users.** The sync `worker/` subdirectory is maintainer-only; the release zip stays host + predictor + README as today.
