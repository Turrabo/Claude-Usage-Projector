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

**7a.4 (pending)** — host-side `prediction_store` keyed by account.
- `src/csm/prediction_store.rs` is still a single global `OnceLock<PredictionStore>` with `latest: Option<LatestPrediction>` and one `VecDeque<HistoryEntry>`. Today's predictor emits `account_id` on every message but the host discards it — `PredictionMessage`s arriving from different accounts overwrite each other's `latest` and interleave in `history`.
- For 7a.4: refactor `PredictionStore` to `HashMap<AccountId, AccountState>` where each `AccountState` holds its own `latest + history`. `badge.rs` and `popup.rs` continue to read the *active* account only (UI change comes in 7c); this commit is plumbing-only.
- Existing `LatestPrediction` and `HistoryEntry` structs are unchanged; only the container shape changes.
- Estimated effort: ~half a day. Naturally pairs with a third reviewer-checkpoint pass before push.

**Acceptance for 7a as a whole** (after 7a.4 lands): launch the widget, run `claude login` to switch accounts on a single machine, observe in the diagnose log that observations get attributed to the new `account_id` within a few seconds AND that the badge/popup keep showing the active account's data without seeing the older account's stale projection bleed through.

Estimated effort remaining: half a day for 7a.4.

### 7b — Persistence sharding + one-time migration

- Rename `predictor/Persistence/HistoryJsonlWriter.cs` paths from `history.jsonl` to `history-<account_id>.jsonl`. One writer per account, allocated lazily.
- `HistoryJsonlReader.LoadAll` globs `history-*.jsonl` on startup and merges.
- One-time migration: if a legacy `history.jsonl` exists on first Phase-7 launch, tag every row with the active `account_id` at that moment (we don't know the row-original account; best we can do), write to the new shard, rename the original to `history.jsonl.pre-multi-auth-backup`.
- Acceptance: a Phase 6 install can be upgraded in place; the popover chart continues to show the user's full prior history under the active account.

Estimated effort: 1–2 days. Runs in parallel with 7c after 7a lands.

### 7c — UI: popover per-account table

- `src/csm/popup.rs` gains a top section (above the existing chart) — a fixed-height table showing each known account's name, current risk colour pill, projected runout time, and a small marker for "(this machine)" vs "(other machine)" based on which machine produced the row's most recent observation.
- Account display name comes from a per-account-id alias map in the predictor's config (`sync.env` or a sibling), defaulting to `account_id` short-form if no alias set. Maintainer can label each `acct_xxxxxxxxxxxx` as "Work A" / "Work B" / "Personal".
- Badge in `src/csm/badge.rs` continues to show only the active account's risk + runout — no UI change there.
- Acceptance: with 3 accounts populated in the local predictor state (forced via test fixtures if needed), the popover shows a clean 3-row table; the badge shows just the active account.

Estimated effort: 2–3 days. Independent of 7d/7e; depends on 7a.

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
