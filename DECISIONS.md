# Architectural decisions

> Durable record of significant decisions for **Claude-Usage-Projector**. New decisions append at the bottom; existing entries are not edited except to add a "**Status**" line if they're later superseded. Format borrows from Michael Nygard's ADR template, deliberately terse.

---

## ADR-001: Fork CodeZeno upstream rather than rewrite the predecessor

**Date:** 2026-05-13
**Status:** Accepted

### Context

The predecessor project (Claude Session Monitor, WinUI 3/WPF + .NET 9) had a working predictor and JSONL telemetry adapter but a brittle truth source (WebView2 scraping of `claude.ai/settings/usage`), tray-icon-only UI, and a runtime dependency on the .NET 9 desktop runtime that some intended users couldn't install. CodeZeno's open-source [Claude-Code-Usage-Monitor](https://github.com/CodeZeno/Claude-Code-Usage-Monitor) already solved the truth-source problem (authenticated OAuth API at `api.anthropic.com/api/oauth/usage`) and shipped a real native-Windows taskbar widget with zero install footprint.

### Decision

Fork CodeZeno's repo as the new baseline. Port the predecessor's predictor logic into a separate sidecar process attached to it. Freeze the predecessor as a read-only archive on the developer's machine.

### Consequences

- Inherit a maintained truth source, real taskbar embedding, self-update mechanism, and 8-language i18n — for the cost of one daily upstream-sync workflow.
- Predictor logic must be re-homed in a sidecar; cannot run in-process with the upstream Rust binary.
- Must keep edits to upstream files small and sentinel-marked so future upstream merges don't conflict.
- Lose the predecessor's WPF dashboard window; replace with a Win32 GDI hover popup in Phase 4.

---

## ADR-002: Predictor sidecar process model rather than embedded library

**Date:** 2026-05-13
**Status:** Accepted

### Context

The predictor is C# (existing CSM code, well-tested). The host is Rust (CodeZeno's). Two paths to combine them: (a) compile the C# predictor as a native library and call it from Rust via FFI; (b) run the predictor as a separate process and communicate via stdin/stdout. FFI between .NET-AOT C# and Rust is supported but adds binding complexity and ABI fragility; the two-process model is well-understood and trivially testable.

### Decision

The predictor is a separate `.exe` co-located with the host, spawned at host startup, communicating via line-delimited JSON over the predictor's stdin/stdout. Versioned envelope (`{"v":1,"type":...}`) so the protocol can evolve.

### Consequences

- Build the predictor independently of the host; CI workflows are independent.
- Two binaries to ship instead of one — packaging step in Phase 6 will bundle them.
- Process crash isolation: predictor crash doesn't take down the widget, and vice versa.
- Slightly higher RAM cost than in-process (additional process overhead, ~15-20 MB for the predictor at idle).
- IPC backpressure / lifecycle handled by the sidecar wrapper in `src/csm/sidecar.rs`, not pushed onto callers.

---

## ADR-003: Self-contained single-file publish for the predictor, not NativeAOT

**Date:** 2026-05-13
**Status:** Accepted

### Context

The original plan was NativeAOT for the predictor — a small (~25 MB), fast-start native binary. NativeAOT publish on Windows requires the MSVC C++ linker (`link.exe`) from Visual Studio Build Tools. On the developer's corporate machine, five separate install attempts of `Microsoft.VisualStudio.2022.BuildTools` with the C++ workload all failed silently after downloading peripheral packages but before installing the `Microsoft.VC.Tools.*` workload payload. Most plausible cause: corporate IT policy blocks the MSVC payload from the Microsoft CDN while allowing the bootstrapper itself to run. Recovery via `InstallCleanup.exe -f` worked but subsequent installs failed identically.

### Decision

Publish the predictor as `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true`. Produces a ~35 MB exe with the .NET 9 runtime embedded — no install requirement for end users, no MSVC dependency for the developer.

### Consequences

- Predictor binary is ~35 MB instead of the ~25 MB NativeAOT estimate. Acceptable; both are well below the inflection point where users care.
- Slightly slower startup than AOT (~50–200 ms cold-start). Acceptable for a long-running sidecar.
- All AOT discipline (source-generated JSON serializer contexts, no reflection-based DI) was retained anyway, so a future migration to AOT is a one-line csproj change once MSVC becomes available.
- Removes a hard dependency on Microsoft Build Tools from this project's developer setup. Anyone with the .NET 9 SDK can build it.

---

## ADR-004: JSONL append-only files for predictor storage, not SQLite

**Date:** 2026-05-13
**Status:** Accepted — see ADR-008 for the read-only one-time-migration supplement.

### Context

The predecessor used SQLite (Microsoft.Data.Sqlite + Dapper) for signal history, prediction history, and Hawkes state. SQLite is full-featured but adds a native dependency (`e_sqlite3.dll`), complicates AOT/self-contained publish, and is overkill for what the predictor actually needs — append-only time-series with periodic state snapshots.

### Decision

Store observations and Claude Code events as append-only JSONL files; store Hawkes parameters + last prediction as a single `state.json` atomically replaced via temp-file rename. Located in `%APPDATA%\Claude-Code-Usage-Monitor\predictor\`. Rotation at 30 MB or weekly, whichever first.

### Consequences

- Zero native dependencies; AOT-friendly if we revisit ADR-003.
- Trivially inspectable with `tail` / Notepad / any JSON tool.
- No transactional guarantees across multiple files — accepted because the predictor is single-writer and tolerates losing the last few seconds of observations on a crash.
- One-time migration tool (Phase 5) extracts useful CSM SQLite tables into JSONL — easier than supporting both formats.
- Query patterns the predecessor relied on (e.g., "all predictions in the last 24h with HawkesIntensityRatio > 1.5") become line-by-line file walks — fast enough at expected data volumes (~minutes of usage * one row per poll = thousands of rows / week).

---

## ADR-005: GNU/gnullvm + LLVM-MinGW for local Rust builds; CI MSVC for runnable binaries

**Date:** 2026-05-13
**Status:** Accepted 2026-05-13 — superseded 2026-05-26 by ADR-013 (native MSVC is now the canonical local path; gnullvm machinery retired). Original libunwind static-link recipe from the retired `tools/dev-build.ps1` is preserved in Appendix A below.

### Context

The developer's machine cannot install MSVC C++ Build Tools (see ADR-003 for the corporate-block evidence). Rust's default Windows target (`x86_64-pc-windows-msvc`) needs `link.exe`. Rust's `x86_64-pc-windows-gnu` target uses GCC/binutils via rustup's bundled mingw — but rustup's bundled mingw is incomplete (missing `dlltool` deps, no `windres` for `winres` build-deps). Rust also offers `x86_64-pc-windows-gnullvm` which uses LLVM's `lld` + compiler-rt + libunwind, paired with an external LLVM-MinGW distribution.

Empirically, `gnullvm` + `winget install MartinStorsjo.LLVM-MinGW.UCRT` produces a binary that **compiles successfully** but **silently exits during the message loop** after the "tray event hook installed" diagnose line. The MSVC-built binary from CI (where `windows-latest` runners have full MSVC pre-installed) runs identically to upstream. Likely cause of the gnullvm runtime exit: an ABI mismatch in Win32 callback dispatch through statically linked `libunwind` from compiler-rt. Not pursued because the CI MSVC path solves it without further work.

### Decision

- **Local**: gnullvm + LLVM-MinGW via `tools/dev-build.ps1` and `.cargo/config.toml`. Treat as **compile-check only** — the resulting binary is not runnable.
- **Runnable binaries**: GitHub Actions `build-host` workflow on `windows-latest`. Push a branch, download the artifact.
- Document this constraint clearly so a future Claude session doesn't waste a day re-debugging the runtime exit.

### Consequences

- Local iteration loop is `cargo check`–level for the Rust side: typing, errors, lint pass — fast. To actually exercise the host binary requires a CI round-trip (~3 min).
- The C# predictor is unaffected — local `dotnet publish` produces a fully runnable predictor exe.
- If MSVC Build Tools ever become installable on this machine (e.g., IT policy change), this ADR is superseded: run `rustup override unset` in the repo directory to drop back to the default `stable-x86_64-pc-windows-msvc` toolchain, remove `tools/dev-build.ps1`, and delete or empty `.cargo/config.toml` (currently only holds the `WINRES_TOOLCHAIN` env var for the LLVM-MinGW path). Net delta is small.
- New contributors on machines with MSVC available **should not** use the gnullvm path — `cargo build --release` will just work via the default msvc toolchain.

### Appendix A (added 2026-05-26): libunwind static-link recipe from the retired `tools/dev-build.ps1`

When this ADR was active, building via gnullvm + LLVM-MinGW left the resulting `.exe` with a runtime dependency on `libunwind.dll` because LLVM-MinGW dynamically links libunwind by default. The Rust gnullvm target spec appends `-lunwind` after our `link-args`, and the linker picks the dynamic import library (`libunwind.dll.a`) ahead of the static one (`libunwind.a`) — no clean cargo or RUSTFLAGS override is available. The retired `tools/dev-build.ps1` worked around this with a rename trick:

1. Locate `<llvm-mingw>/x86_64-w64-mingw32/lib/libunwind.dll.a` (the dynamic import library).
2. Rename it to `libunwind.dll.a.devbuild-bak` so the linker only finds `libunwind.a` and falls back to static linking.
3. Set `WINRES_TOOLCHAIN` to the LLVM-MinGW root so the `winres` build-dep can find `windres`.
4. `cargo build --release`.
5. Restore `libunwind.dll.a` from backup in a `try/finally` so an interrupted build doesn't leave the toolchain broken. The recovery path also restored the import lib at startup when a previous run had died mid-build (Ctrl-C, BSOD, AV kill) and left the toolchain with the backup in place but no original file.

Result: a single self-contained `claude-code-usage-monitor.exe` whose only DLL imports are Windows system libraries — at compile time. (The runtime silent-exit bug at the `tray event hook installed` log line — the original reason this ADR was downgraded — was never resolved; if a future contributor needs gnullvm again, that bug remains an open obstacle.)

Captured here per the global doc-hygiene rule "extract lessons before deletion." Native MSVC (ADR-013) is the recommended path.

---

## ADR-006: Hover popup window for the predictor UI, not embedded in the taskbar widget

**Date:** 2026-05-13
**Status:** Accepted

### Context

CodeZeno's widget is a small embedded child of `Shell_TrayWnd` (~210 × 46 px) showing two horizontal bars and percentages. Adding the predictor's projection chart could either (a) extend the widget itself, (b) live in a separate popout window triggered on hover, or (c) live in a separate popout window triggered on right-click. (a) would conflict with every upstream layout change; (c) would be a click-to-open model with worse latency.

### Decision

Add a separate borderless `WS_EX_NOACTIVATE` popup window that appears after 200 ms of continuous mouse-hover over the widget and dismisses on mouse-leave with a 100 ms grace period. The popup is 450 × 160 px (matched to the predecessor CSM's `ChartPopover` after a minify pass — see commit `80707eb`), painted with raw GDI (Win32), and lives entirely in fork-authored code (`src/csm/popup.rs`).

### Consequences

- Zero conflict surface with upstream widget layout: the popup is a separate HWND that upstream's code doesn't know about.
- Implementation uses a background cursor-poll thread (`src/csm/hover.rs`, 100 ms interval) tracking the badge HWND's screen rect against the cursor position. Simpler than `TrackMouseEvent`/`WM_MOUSEHOVER` and avoids per-frame Win32 callbacks; the polling cost is negligible at this interval.
- Performance: 5-second repaint cadence while shown; ignored when hidden. Negligible.

---

## ADR-007: Daily upstream-sync GitHub Action that fails on conflict

**Date:** 2026-05-13
**Status:** Accepted

### Context

The fork tracks an actively-maintained upstream (multiple releases per month). Manual `git fetch upstream && git merge` is easy to forget; bundled merges accumulate conflict surface area.

### Decision

A cron-scheduled GitHub Actions workflow (`.github/workflows/upstream-sync.yml`) runs daily at 13:17 UTC, fetches upstream, attempts a fast-forward merge into our `main`, and pushes if clean. On conflict, the workflow fails visibly (red X on the Actions tab) so the developer can resolve manually from a local clone.

### Consequences

- Clean upstream changes propagate automatically; no developer action required.
- Conflicting upstream changes surface immediately, not weeks later when the conflict is bigger.
- Workflow runs on `ubuntu-latest` (no build, just git operations) — essentially free CI minutes.
- The sentinel-comment discipline (CSM EXTENSIONS BEGIN/END) is what keeps conflicts rare. Adding new touch points to upstream files raises this ADR's maintenance cost; prefer additive new files when possible.

---

## ADR-008: Microsoft.Data.Sqlite in the predictor for the one-time CSM migration

**Date:** 2026-05-15
**Status:** Accepted

### Context

ADR-004 ruled out SQLite for the predictor's storage layer and listed "zero native dependencies" as a benefit. Phase 5 (commit `3f6e26f`) needed to read the predecessor's `%LOCALAPPDATA%\ClaudeSessionMonitor\csm.sqlite` once at first run to seed `history.jsonl` with truth-source rows from the prior project. Options considered: (a) write a tiny custom SQLite parser; (b) ship a separate one-shot migration tool; (c) add `Microsoft.Data.Sqlite` to the predictor csproj and run the migration in-process on first launch.

(a) was rejected because hand-rolling a SQLite reader is fragile and unnecessarily clever for ~400 rows of read-only access. (b) was rejected because a separate tool is clumsy UX — the user would forget to run it — and the value of the seed evaporates after the first launch of the predictor on a fresh machine.

### Decision

Add `Microsoft.Data.Sqlite` to `Predictor.csproj` and run the migration inside `predictor/Persistence/CsmSqliteMigrator.cs`. The package is bundled into the self-contained single-file publish; the native `e_sqlite3.dll` ships inside `ccum-predictor.exe` and is extracted to a temp path on first launch like every other native dep in the bundle. The migration runs once, writes a `.csm-migrated` sentinel, and is skipped on every subsequent launch.

### Consequences

- ADR-004's "zero native dependencies at runtime for prediction work" still holds in spirit — the SQLite code path is only ever exercised during the first-run migration. Once the sentinel is in place, no SQLite calls happen during steady-state operation.
- Single-file exe size grew from ~35 MB to ~36 MB. Acceptable.
- Microsoft.Data.Sqlite is a soft regression of ADR-003's AOT-friendliness — its native dependency is not AOT-compatible the way pure managed code is. Phase 5 isn't on the AOT path today, but a future AOT switch would need to either drop the migrator or compile it as a separate tool. Acceptable for now.
- The migration window is hard-coded to the last 14 days (`CsmSqliteMigrator.MigrationWindowDays`). Older CSM data is left in `csm.sqlite` untouched; it's outside the popup chart's current-session window anyway, so importing it would be wasted bytes.

---

## ADR-009: Companion badge window for on-screen risk + runout, additive to the upstream widget

**Date:** 2026-05-18
**Status:** Accepted

### Context

After Phase 4 shipped the hover-popup chart, the on-screen widget surface devoted its always-visible bars to current usage% and weekly%, with risk and projected-runout one hover away. The user identified that the priorities were inverted relative to the predecessor (CSM) — its primary user value was answering "am I going to run out, and when?", and the popup-on-hover model hid those two signals from at-a-glance. Three architectural paths were evaluated:

(a) Patch upstream's render code (`src/window.rs`) to inline risk and runout into the existing widget. Rejected: any edit inside upstream's positioning + painting hot loop would conflict on every upstream layout change, compounding the maintenance cost of the sentinel discipline established in ADR-001 and ADR-007.

(b) Revive the abandoned WinUI 3 widget with upstream's OAuth + polling ported into C#. Rejected as a full rewrite that would also throw away the Phase 2–3 predictor port and abandon the upstream-sync workflow that the fork architecture depends on.

(c) An additive companion Win32 window pinned to the upstream widget, drawn entirely by our code.

### Decision

`src/csm/badge.rs` — a layered Win32 window pinned immediately to the LEFT of the upstream usage widget (preserving upstream's flush-right anchor against the system tray for the combined cluster), showing two text rows: current risk on top, projected runout local time below. Visual is a translucent rounded card with separate horizontal and vertical outer margins (`REF_CARD_MARGIN_H = 6`, `REF_CARD_MARGIN_V = 4` reference pixels), `REF_CARD_CORNER_RADIUS = 4`. The badge mirrors upstream's UpdateLayeredWindow + DIB rendering technique and uses the same Segoe UI FW_MEDIUM `sc(-12)` font so its text reads as a continuation of upstream's typography.

The hover trigger for the existing Phase 4 popup moved from the upstream widget HWND onto the badge HWND — we own that HWND directly, so `src/csm/hover.rs` no longer has to walk `FindWindowExW` under `Shell_TrayWnd` on each poll tick.

### Consequences

- Zero modifications inside upstream's render or input code. The CSM EXTENSIONS sentinel block in `src/main.rs` gained one line each for `csm::badge::init()` / `csm::badge::shutdown()`; no other upstream files were touched by this work.
- The pattern (own a sibling HWND, drive it from `prediction_store`, mirror upstream's rendering primitives) is now the reference template for any future "add X to the widget surface" feature. See `[[feedback-companion-window-over-upstream-patch]]` auto-memory.
- Drag-by-bevel is preserved on upstream's existing internal left bevel — exactly where upstream's drag handler has always lived; this ADR adds no new drag surface.
- An experiment in click-forwarding from the badge's leftmost bevel zone into upstream's drag handler (commits `75a6540` + `d712bd6`) was reverted in `424af2b` after hitting a Win32 `SetCapture` limitation: capture silently fails when the calling cursor isn't currently over the capturing window, leaving upstream's drag state mid-transition. A proper forwarding implementation would have to call `SetCapture` on the badge HWND, track the drag locally, and forward each `WM_MOUSEMOVE` + `WM_LBUTTONUP` to upstream synthetically — feasible but not worth the surface area for a polish feature.
- `SetWindowRgn` is re-applied each 1-second tick, scaled from the upstream widget's measured height. DPI changes propagate without us calling `GetDpiForWindow` explicitly — the upstream widget's rect is the authoritative scale signal.

---

## ADR-010: cargo-xwin for runnable local builds on the MSVC-blocked machine

**Date:** 2026-05-21
**Status:** Accepted 2026-05-21 — superseded 2026-05-26 by ADR-013. The body's "produces a runnable binary in ~10s" claim was incorrect: cargo-xwin binaries crashed at runtime on the maintainer's machine with the same USER32 0x35532 access violation as native MSVC builds (the bug is in the source/build, not the toolchain — diagnosed 2026-05-22). The cross-compile path was retired without ever delivering a confirmed working runtime on this machine. ADR-013 records the lesson: smoke-test the produced binary before declaring a build path "runnable."

### Context

ADR-005 set up `gnullvm` + LLVM-MinGW as the local Rust build path on a machine that can't install MSVC C++ Build Tools. The compromise: the gnullvm binary compiles successfully but silently exits ~5s after startup at runtime, so iteration requires pushing to CI and waiting ~4 min for a downloadable artifact. After ~6 weeks of using that loop, the cost became the main practical drag on development velocity.

Two paths were explored to fix it:

(a) WSL2 + cross-compile via `cargo-xwin` inside Linux. Failed: the corporate network blocks WSL2's NAT outbound traffic (DNS resolves but no HTTP/HTTPS to any external host), and `networkingMode=mirrored` left WSL unable to start. Reverted.

(b) Windows-native `cargo-xwin`. cargo-xwin downloads the MSVC SDK headers + import libraries directly from Microsoft's CDN as raw files, bypassing the installer bootstrapper that the corporate IT block targets. The CDN raw-file URLs pass through the same network controls that allow HTTPS to Microsoft from a browser. Combined with LLVM-MinGW's existing LLD installation (already there for the gnullvm path), this gives us a complete MSVC-target build pipeline without needing `link.exe`.

### Decision

`tools/dev-build-msvc.ps1` invokes `cargo xwin build --target x86_64-pc-windows-msvc --cross-compiler clang` with three pieces of glue:

- An `lld-link.exe` shim in `~/.cargo/bin` — LLVM-MinGW ships `ld.lld.exe` (the GNU/MinGW driver name) but the msvc target expects `lld-link.exe` (the MSVC driver name). Same underlying LLD binary, mode picked by program name; we just `Copy-Item` it once.
- LLVM-MinGW's `bin` on `PATH` so cargo-xwin's clang invocation finds the compiler.
- `SKIP_WINRES=1` env var that tells `build.rs` to skip the Win32 resource embed (icon + version metadata). That embed needs Microsoft's `rc.exe` which isn't part of the xwin SDK download. The resulting `.exe` still runs identically — only the embedded metadata is missing, which is irrelevant for dev iteration.

One-time admin setup populates the SDK cache: `xwin --accept-license splat --output "$env:LOCALAPPDATA\cargo-xwin\xwin"` from an elevated shell creates the cache directory's version-pointer symlink (the one symlink that needs admin even though we have admin-on-demand, because Developer Mode is also blocked on this machine). After that, all subsequent builds run non-elevated.

### Consequences

- Local iteration loop drops from ~4 min (push → CI → download) to ~10s (`cargo xwin build` incremental). For the 90% case of "tweaked a constant, want to see the result" this is the difference between an interactive workflow and a context-switch workflow.
- The CI workflows are unchanged and remain the authoritative reproducible-build path. cargo-xwin output is byte-equivalent in behaviour but not in metadata (no embedded icon/version).
- ADR-005's gnullvm path is kept as a compile-check fallback (`cargo check`/`clippy` against the gnullvm toolchain, which is faster than spinning up cargo-xwin for type-checking).
- `winres = "0.1"` doesn't support `rc.exe` overrides directly; the `SKIP_WINRES` escape hatch in `build.rs` (~6 lines) is the cleanest workaround. A future upgrade to a more flexible resource-embed crate (e.g. `embed-resource`) could remove that hatch, but the current approach is low-overhead.
- xwin caches the SDK at `%LOCALAPPDATA%\cargo-xwin\xwin\` — ~1 GB on disk. Cleanup with `Remove-Item -Recurse $env:LOCALAPPDATA\cargo-xwin` if it ever needs to be rebuilt.

---

## ADR-011: Multi-auth — account identity model and per-account state

**Date:** 2026-05-21
**Status:** Accepted (design phase; implementation in Phase 7 — see [`docs/PHASE-7-PLAN.md`](docs/PHASE-7-PLAN.md))

### Context

The maintainer uses three Claude OAuth identities — two `@instem.com` work accounts plus `turrabo@gmail.com` personal — primarily one per machine but with opportunistic switching when one hits its 5-hour limit. The current architecture is single-tenant top to bottom: credentials, polling, IPC envelopes, predictor state, persistence, and UI all assume "the one observation stream" (see the multi-auth audit, 2026-05-21). To meet the user need ("see all three accounts' usage so I know which to switch to when one runs out"), every layer needs an `account_id` dimension.

Three architectural shapes were evaluated:

- (A) **Multiple host instances**, one per account. Each host runs its own predictor sidecar. Cheapest code but the worst UX (three badges fighting for taskbar space, no possibility of aggregated views) and triples polling/memory cost.
- (B) **One host, internal multiplexing, one predictor sidecar per account.** Process isolation between accounts but 3× IPC plumbing for no real benefit — predictors don't fight each other and the host has to demultiplex per-account UI state anyway.
- (C) **One host, one predictor, account-tagged messages.** IPC bumped to `v: 2` adding an `account_id` field; predictor state keyed by account in a dictionary; persistence per-account; UI shows the *active* account on the badge and *all* accounts in the popover.

### Decision

Option (C). The full design:

- **Account identity** is derived from the OAuth credential's `sub` claim (JWT subject), hashed to a stable short string. This avoids putting email addresses in transit / on disk and is stable across token rotations for the same account. Concretely: `account_id = "acct_" + first 12 hex chars of SHA-256(jwt.sub)`.
- **Active-account-on-this-machine** is whichever account `~/.claude/credentials.json` currently points at. The predictor reads this file at startup and watches it for changes (FileSystemWatcher). When the user runs `claude login` and switches accounts, the predictor picks up the new identity within seconds and the badge re-routes to it. Inactive accounts on this machine still appear in the popover via cross-machine sync data (see ADR-012).
- **IPC** bumps to `v: 2`. Every `observe` and `prediction` message carries an `account_id` field. Older host/predictor pairs (`v: 1`) won't interoperate with newer ones; the version-handshake added in commit `08e52f2` already flags this loudly.
- **Predictor state** becomes `Dictionary<AccountId, ObservationWindow>` and `Dictionary<AccountId, TelemetryWindow>`. Tier 1/2/3 maths run per-account. Hawkes parameter fitting is per-account too — each account has its own typing rhythm.
- **Persistence** shards `history.jsonl` by account into `history-<account_id>.jsonl`. Existing single-account `history.jsonl` is migrated on first run after upgrade: the predictor reads it, tags the rows with the now-detected `account_id`, and writes them to the sharded file. Original file kept as `history.jsonl.pre-multi-auth-backup`.
- **UI** — badge shows risk + runout for the active account only (no extra taskbar real estate vs today). Popover top row gains a per-account breakdown table: account name, risk, runout, "(this machine)" or "(other)" tag based on which machine produced the most recent observation. Chart underneath remains driven by the active account.
- **`JsonlTail`** (the `~/.claude/projects/**/*.jsonl` reader feeding the Hawkes tier) cannot tell which account a session was logged in with. We accept this: Hawkes intensity for each account uses whichever sessions ran while that account was the active one on this machine, attributed via the active-account watcher's history. Cross-machine, session timing data is **not** synced (sensitivity reasons — see ADR-012).

### Consequences

- IPC schema becomes load-bearing. The `account_id` field is wire-optional but semantically required: both the predictor (`Program.cs`) and the host's `prediction_store` accept a null/absent `account_id` and route the observation/prediction to a shared `"acct_default"` sentinel bucket rather than dropping it. This is the graceful path for a v:1↔v:2 mispairing during a partial upgrade or an unreadable `credentials.json`; the version-handshake added in commit `08e52f2` still surfaces the mismatch in the diagnose log. Future protocol changes need to follow the same `v: N` bump discipline.
- Persistence migration is one-way. The old `history.jsonl` format is converted to sharded files at first launch on Phase 7. Downgrade to a pre-Phase-7 build would not see the new shards and would re-create the flat file from scratch (with whatever live observations come in). Acceptable for a single-maintainer project.
- The Hawkes model becomes per-account, which means smaller per-account training data. For accounts used heavily this is fine; for low-volume accounts the predictor may sit on default parameters longer before fitting. We accept the tradeoff because pooling across accounts conflates very different usage rhythms.
- Active-account detection ties us to Claude CLI's storage layout (`~/.claude/credentials.json`). If Anthropic restructures that file the detector breaks gracefully (predictor falls back to a single-account mode) — but we should keep an eye on Claude CLI releases.
- Pairs with ADR-012 — the multi-auth and sync features are coupled architecturally even though they're separable in code.

---

## ADR-012: Cross-machine sync via a personal Cloudflare Worker

**Date:** 2026-05-21
**Status:** Accepted (design phase; implementation in Phase 7 — see [`docs/PHASE-7-PLAN.md`](docs/PHASE-7-PLAN.md))

### Context

ADR-011 makes the badge + popover aware of three Claude accounts. The maintainer wants those views populated on *every* machine — so on the work laptop he can see how the personal account is doing too, even though the personal account's OAuth token only lives on the personal machine. Without cross-machine data flow, the badge on each machine would only know about the accounts whose tokens are stored locally, and the "see all three" goal collapses to "see whichever fraction is signed in here right now."

The multi-machine sync audit (2026-05-21) evaluated four mechanisms: cloud-sync folder (OneDrive/Dropbox), shared LAN drive, git auto-commit, and a small custom service. The user has ruled out corporate cloud storage and is also unhappy with file-sync latency (OneDrive can take minutes to propagate). He already has his own Cloudflare infrastructure — a tunnel at `daisybot.co.uk` reverse-proxying to a local OWUI Docker instance, behind Cloudflare Zero Trust with Google SSO. A read-only API token + his other agent's investigation (transcript 2026-05-21) confirmed:

- No existing Workers, Pages, KV namespaces, D1 databases, or R2 buckets on the account.
- The Cloudflare Free plan limits (100 k requests/day, 1 GB D1, 25 M D1 reads/day) are ~100× larger than our load.
- Cloudflare Access supports Service Tokens (`CF-Access-Client-Id` / `CF-Access-Client-Secret` header pair), distinct from the interactive Google SSO policy on the existing OWUI app.

### Decision

A dedicated Cloudflare Worker deployed to a new subdomain `sync.daisybot.co.uk`, backed by a new D1 database `claude-usage-sync`. Owned entirely by the maintainer; not part of the product's user-facing setup story for colleagues (sync is optional, off by default).

Concretely:

- **D1 schema** — one `observations` table with columns `(ts INTEGER, account_id TEXT, machine_id TEXT, used_pct REAL, refresh_at INTEGER)` and a composite primary key of `(account_id, machine_id, ts)` plus an index on `ts` for range queries. Schema lives at `worker/schema.sql` in this repo so future migrations are versioned alongside the code.
- **Worker code** — TypeScript, ~100 LoC, lives at `worker/src/index.ts`. Two endpoints: `POST /observations` (accepts a batch of observations, `INSERT OR IGNORE` to be idempotent against retries), `GET /observations?since=<unix>&account_id=<id>` (returns rows by time range, optionally filtered by account). No authorisation logic in the Worker itself — Cloudflare Access in front rejects anything without a valid service token.
- **Auth** — one Cloudflare Access Service Token *per machine*, configured via a fresh Access app on `sync.daisybot.co.uk`. Keeps the blast radius tight: a leaked sync token can only reach the sync API, not the existing OWUI tunnel on `daisybot.co.uk`. The Worker trusts the `Cf-Access-Authenticated-User-Email` / `Cf-Access-Client-Id` headers — Cloudflare won't pass through requests that fail Access policy, so by the time the Worker sees a request it's already authenticated.
- **Predictor integration** — the C# predictor sidecar owns sync. After each local observation lands, the predictor `POST`s it to the Worker (~one POST/minute/machine). The predictor also `GET`s for new observations since its last-known sync timestamp once every 30 seconds, deserialises them, and feeds them into the relevant `ObservationWindow`. Latency: sub-second from POST to other machines' next GET.
- **Failure handling** — if the Worker is unreachable, the predictor falls back to local-only operation and retries on the next poll. Sync is best-effort; the local widget stays useful when offline or when Cloudflare has an outage. Sync state (last-synced timestamp per account) lives in `state.json` alongside the existing Hawkes-state-pending notes in `CLAUDE.md` Conventions.
- **Configuration** — sync credentials live in `%APPDATA%\Claude-Code-Usage-Monitor\predictor\sync.env` (Windows) with fields `SYNC_URL`, `SYNC_CLIENT_ID`, `SYNC_CLIENT_SECRET`, `MACHINE_ID`. If the file is missing or unreadable, sync is silently disabled. This is the per-machine config; it's intentionally not committed to the repo.

### Consequences

- **User-owned infrastructure.** The Worker and D1 live entirely under the maintainer's Cloudflare account. No third party (other than Cloudflare itself) sees the data. Privacy threat model is exactly what the user asked for: not corporate cloud, not a shared third-party service.
- **Sync is optional and personal.** Colleagues running the widget don't get sync by default and don't need to set it up; the widget works fine without it. Any future user who *wants* sync replicates the same setup with their own Cloudflare account. We document this clearly in `worker/README.md`.
- **The Worker is in-repo.** Source under `worker/`, deployed via `wrangler deploy`. Lives alongside `predictor/` as a peer sub-project. Future contributors get the whole picture in one git clone.
- **One-time dashboard setup.** Even with the source in-repo, the maintainer (or any future user enabling sync) has to do click-through configuration in the Cloudflare Zero Trust dashboard: create the D1 database, create one service token per machine, create the Access app. `worker/README.md` documents the exact sequence.
- **D1 is single-region.** Cloudflare D1 picks one region for the primary; cross-region reads have edge latency. For two machines in the UK (Instem office + personal home) this is irrelevant. If the user ever uses the widget from a US/Asia machine the latency story may need revisiting (mitigation: move to KV with eventual consistency, or partition).
- **No conflict resolution needed.** The composite primary key `(account_id, machine_id, ts)` means concurrent writes from different machines can't collide; idempotent `INSERT OR IGNORE` handles retry storms. Observations are immutable once written; no UPDATE path is needed.
- **`JsonlTail` data stays machine-local.** The Hawkes-feeding session-timing data from `~/.claude/projects/**/*.jsonl` is more sensitive than the OAuth-derived percentages (it timestamps when you ran what session). It is intentionally NOT synced — each machine's Hawkes model uses local data only. Tier 1/2 (the chart-driving tiers) are the synced bits.
- **Cost.** Expected steady-state: ~2 requests/minute/machine × 2 machines = ~5800 requests/day, well within the Workers Free 100 k/day. D1 footprint is ~tens of KB/day; the 1 GB free quota is years away.

---

## ADR-013: Native MSVC is the canonical local build path; cargo-xwin and gnullvm retired

**Date:** 2026-05-26
**Status:** Accepted — supersedes ADR-005 (gnullvm) and ADR-010 (cargo-xwin) for steady-state local development. The 4th Context bullet and the 3rd Decision bullet (both claiming locally-built binaries crash USER32 0x35532 on this machine) were corrected by ADR-014 later the same day: a stale `rustup override` was silently routing builds through gnullvm despite this ADR's claim that MSVC was canonical. Native MSVC binaries do run cleanly; the cleanup pass missed `rustup override unset`.

### Context

ADR-005 (gnullvm + LLVM-MinGW, compile-check only) and ADR-010 (cargo-xwin + LLD, claimed runnable) were both workarounds premised on "the developer's machine cannot install MSVC C++ Build Tools" — an IT block first asserted in ADR-003 and propagated through the toolchain ADRs. A diagnostic session on 2026-05-22 disproved that premise:

- The 13 May 2026 install attempts that "failed silently" actually completed successfully. The master setup log shows `Completed install`, the VCTools workload requested and accepted, ~8 GB consumed. The reason no MSVC files appeared on disk after each attempt is that PowerShell's `Start-Process -Verb RunAs -ArgumentList @(...)` does **not** quote array elements before passing them to the elevated child process. The `--installPath "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"` argument arrived at `setup.exe` as five separate tokens; `setup.exe` accepted the first one and used `C:\Program` as the `installPath`. Each attempt installed cleanly into `C:\Program\`, which was subsequently deleted (manually or by tidy-up) as a stray top-level directory. The corporate IT block existed only in our heads.
- On 2026-05-22, a re-attempt with `--installPath C:\BuildTools` (no spaces, sidestepping the bug) installed cleanly. VS Build Tools 2022 17.14.x with the VCTools workload now lives at `C:\BuildTools\`. `cl.exe`, `link.exe`, and the Windows SDK headers are all present and reachable via `C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat`.
- Native `cargo build --release` against the default `stable-x86_64-pc-windows-msvc` toolchain takes ~27 seconds, embeds the icon + version metadata via `winres` + the Windows SDK's `rc.exe`, and produces a 1 MB binary. No SKIP_WINRES escape hatch needed, no LLD shim, no cargo-xwin.
- A separate runtime bug — the locally-built binary still crashes USER32 0x35532 within ~2 seconds on this specific machine, while CI-built binaries of byte-identical source run cleanly — is unrelated to toolchain. cargo-xwin builds crashed identically, which is what motivates the supersession: neither workaround actually delivered a confirmed working binary on this machine. The crash is captured in the auto-memory at `project_local_build_user32_crash.md` and tracked separately via the planned `dumpbin /HEADERS` and `/LOADCONFIG` diff between the CI artifact and a local build.

### Decision

- **Canonical local build path:** `cargo build --release` against the default `stable-x86_64-pc-windows-msvc` toolchain. Source `C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat` once per shell (or use the "Developer PowerShell for VS 2022" shortcut) so `cl.exe`, `link.exe`, and `rc.exe` are on `PATH` + `INCLUDE` + `LIB`. No wrapper script.
- **CI builds:** `.github/workflows/build-host.yml` continues using the default msvc toolchain on `windows-latest`. The `branches-ignore: main` trigger (which masked the USER32 regression for several days in May 2026) was removed in commit `5a08d0a` so post-merge regressions are visible.
- **Runnable on this maintainer's machine:** **still requires the CI artifact, pending USER32 crash resolution.** Locally-built binaries compile cleanly but don't run on this specific Windows 11 install — the same bug affected both cargo-xwin and native MSVC, so it's not in the build path. Investigation deferred (PE-header diff between CI exe and local exe; see `project_local_build_user32_crash.md`).
- **`tools/dev-build.ps1` (gnullvm) and `tools/dev-build-msvc.ps1` (cargo-xwin):** deleted. ADR-005's body is retained for history, with the libunwind static-link recipe lifted into Appendix A. ADR-010's body is retained for history with a corrective Status line.
- **`.cargo/config.toml`:** retained as an intentional empty placeholder for any future fork-specific cargo config; comment updated to drop references to the retired scripts.

### Consequences

- One canonical local build path instead of three (gnullvm compile-only, cargo-xwin "runnable", CI runnable). New contributors set up a single Rust toolchain + standard MSVC and `cargo build --release` works.
- Disk reclaim: 5.38 GB cargo-xwin SDK cache (`%LOCALAPPDATA%\cargo-xwin\`), 5 MB `lld-link.exe` shim in `~/.cargo/bin`, ~20 MB cargo subcommands (`cargo uninstall xwin cargo-xwin`), and the `stable-x86_64-pc-windows-gnullvm` + `stable-x86_64-pc-windows-gnu` Rust toolchains (~750 MB each) all become reclaimable. None are deleted by this ADR — see follow-up commits.
- **Lesson 1 (PowerShell elevation quoting):** `Start-Process -Verb RunAs -ArgumentList @('--installPath', 'C:\Path With Spaces')` silently splits the spaced path. For installers, future fork tooling, or any other use that passes paths with spaces through UAC elevation: build a single quoted command string and pass it as a single ArgumentList element, OR use an installPath without spaces. The cost of getting this wrong: silent install to the wrong location, no error messages, and (in our case) ~6 weeks of misdirected blame on corporate IT.
- **Lesson 2 (smoke-test before declaring "runnable"):** ADR-010's "runs identically to a CI-built one" claim stuck in the docs for ~24 hours without any contributor actually launching the produced binary. The diagnose log was never tailed, the badge never appeared on screen, and the documented confidence was unearned. For any future ADR that introduces a new build path: at minimum, launch the produced binary, verify the diagnose log records `window shown` and the first poll completes, before merging the ADR or any cross-references to it.
- The "MSVC blocked by IT" framing in ADR-003, ADR-005, and ADR-010 was a false premise from Lesson 1. Per the append-only ADR discipline (CLAUDE.md global rule 4 — "retired entities only appear in retirement context"), those ADRs keep their original bodies and get supersession Status lines; this ADR documents the disproof.
- The USER32 crash investigation becomes the next critical-path item for restoring local-runnable iteration. It does not block Phase 7b/7c/7d/7e work, but it should be resolved before Phase 7e (Worker sync integration), where local smoke-test cycles speed up iteration meaningfully.
- Future updates to VS Build Tools may re-trigger McAfee's `ElevationServiceSupport: Blocked` policy. The no-spaces `C:\BuildTools` install path captured in this ADR is the durable workaround; document machine rebuild requirements alongside it.

---

## ADR-014: USER32 crash diagnosis was a stale gnullvm rustup override; native MSVC builds run cleanly

**Date:** 2026-05-26
**Status:** Accepted — corrects the "USER32 0x35532 crash on native MSVC" claim in ADR-013's Context and Decision sections. Native MSVC remains the canonical local build path per ADR-013; the workaround paragraph that pointed to CI artifacts is no longer needed.

### Context

ADR-013 (earlier today) recorded a "separate runtime bug" — locally-built host binaries crash USER32 `0xc0000005` at offset `0x35532` within ~1-2 seconds while CI-built binaries of byte-identical source run cleanly on the same machine. The auto-memory at `project_local_build_user32_crash.md` recorded the same signature against both the `cargo-xwin` and `native MSVC` build paths, diagnosed 2026-05-22. The investigation deferred to a future session was to diff PE headers between CI and local binaries to find a linker-level difference (DLL Characteristics, CFG flags, linker version, LoadConfig table) that could explain the crash.

That investigation ran today (track C of the 2026-05-26 session). Results:

1. **No PE-level difference exists.** `dumpbin /HEADERS /LOADCONFIG /IMPORTS` on commit `76b0ab0`'s CI artifact (SHA-256 `8321907a…`) vs a fresh local `cargo build --release` of the same source (SHA-256 `85bade30…`, both 887,296 bytes) produced 40 lines of diff total — entirely timestamps, PDB GUIDs, and ~0x100-byte address shifts in `.rdata`/Debug/TLS/LoadConfig directories caused by LTO non-determinism. DLL Characteristics flags, subsystem version, linker version, `/GS=32 /guardN=32` security feature stamps, list of 265 imported DLL functions, and Control Flow Guard tables were all identical.
2. **The fresh local MSVC binary does not crash.** Smoke-tested 2026-05-26 11:34 with the production widget temporarily stopped: `target/release/claude-code-usage-monitor.exe --diagnose` launched cleanly, badge created, popup created, taskbar found, tray event hook installed, hover thread tracking the badge rect. Alive 60+ seconds with no crash, ~0.5s CPU and 17 MB RSS — normal steady-state.
3. **The 2026-05-22 "native MSVC" test was actually a gnullvm test.** `rustup override list` from `C:\Source\Claude-Usage-Projector` showed `stable-x86_64-pc-windows-gnullvm` was still set as the directory override — a leftover from ADR-005's gnullvm era that ADR-013's cleanup pass missed. The override took precedence over the global default toolchain, so every `cargo build --release` from the repo dir invoked gnullvm + LLVM-MinGW + LLD instead of the MSVC linker, even when `vcvars64.bat` had been sourced and the maintainer believed `link.exe` was driving the build. The output binary went to `target/release/claude-code-usage-monitor.exe` (the default host-target path, regardless of toolchain choice), making the gnullvm build look like an MSVC build at every layer except the actual link.
4. **The crash signature was the [gnullvm runtime bug](#adr-005-gnu-gnullvm--llvm-mingw-for-local-rust-builds-ci-msvc-for-runnable-binaries), not USER32 0x35532.** ADR-005 already records that gnullvm binaries silently exit ~5s after startup, almost certainly in the Win32 message loop. Identical crash offsets between "cargo-xwin" and "native MSVC" rows in the auto-memory should have been a red flag — two separate toolchains producing byte-different binaries do not crash at the same offset by coincidence. The probable explanation is that both rows were actually gnullvm binaries: the cargo-xwin path's `lld-link.exe` shim and the "native MSVC" path's silent gnullvm override both fed through similar code paths.

### Decision

- **Drop the rustup directory override.** `rustup override unset` from `C:\Source\Claude-Usage-Projector` is the canonical fix. `rustup show` from the repo dir now reports `stable-x86_64-pc-windows-msvc` as the active toolchain, which matches ADR-013's stated canonical path.
- **Native MSVC local builds are confirmed runnable on this machine.** ADR-013's "Runnable on this maintainer's machine: still requires the CI artifact, pending USER32 crash resolution" is replaced by "Native `cargo build --release` produces a runnable binary." The CI artifact path remains useful for clean reproducible builds and for contributors without local MSVC, but is no longer a workaround for a crash.
- **Investigation tooling preserved at `c:/tmp/track-c/`.** CI artifact, local MSVC binary, local gnullvm binary, and six `dumpbin` outputs are kept for future regression diagnosis using the same procedure.

### Consequences

- The "use CI artifacts on this machine" guidance in `docs/BUILD.md`, the auto-memory, and the various 2026-05-22 ADR commentary becomes obsolete. Local iteration loop drops from ~4 min (push → CI → download) to ~30 seconds (`cargo build --release` clean) on this machine. Incremental rebuilds drop to a few seconds.
- **Lesson 3 (clean up rustup overrides when retiring toolchains):** When an ADR retires a toolchain (ADR-013 retired gnullvm and cargo-xwin), the cleanup checklist must include `rustup override list` and `rustup override unset` in every repo where the retired toolchain was active. ADR-013 deleted the wrapper scripts and updated `.cargo/config.toml` but left the per-repo rustup override in place, which silently defeated the entire "native MSVC is now canonical" claim for any `cargo build` run from inside the repo. The override is invisible at every layer except `rustup show` and `rustup override list` from the repo dir specifically; from outside the dir it looks like the global default is active.
- **Lesson 4 (identical crash offsets across toolchains warrant suspicion):** ADR-005 and the project memory both recorded a USER32 0xc0000005 at offset 0x35532 against two different build paths (cargo-xwin and native MSVC). Two independently-linked binaries with different code layouts crashing at the same byte offset is implausible without a common root cause. The likeliest explanation — that one of the "different" paths was actually the same as the other under the hood — should have been investigated earlier. This is a generalisation of ADR-013's Lesson 2: smoke-test before declaring "runnable," and also cross-check signatures before declaring two failures independent.
- ADR-013's Lesson 1 (PowerShell elevation quoting) and Lesson 2 (smoke-test before declaring "runnable") both still stand; Lesson 4 here extends Lesson 2 to the inverse case (smoke-test before declaring "broken" too).
- The McAfee Endpoint Security alternative-hypothesis path investigated in track C is no longer needed. No IT escalation is required. The `ElevationServiceSupport: Blocked` policy noted in ADR-013 still applies to VS Build Tools updates, but does not affect the build or run path.
- Phase 7e (Worker sync integration) can now use local smoke-test cycles rather than CI round-trips. No phase work was blocked by the prior misdiagnosis (CI artifacts were a working fallback) but the speed-up materially helps iteration.

