# Claude-Usage-Projector — fork context

> **⚠ RETIRED / FROZEN as of 2026-05-27, and ARCHIVED on GitHub as of 2026-09-04.** This fork is no longer the active project. Development moved back to a single-stack **C# own-widget** (the "Claude Session Monitor" rebuild). The full reasoning is in [`DECISIONS.md`](DECISIONS.md) **ADR-015**; the forward roadmap is in [`docs/REBUILD-PLAN.md`](docs/REBUILD-PLAN.md).
>
> **The repository is read-only and no workflow will run.** Changing anything here means un-archiving it first, and a first `upstream-sync` dispatch after that will fail to compile until `src/csm/` is ported to upstream's current API. **ADR-016** says why.
>
> **If you're a future Claude agent landing here:** don't build new features on this repo. It is kept as (a) a working reference implementation and (b) the source of the portable C# **predictor** (`predictor/` — the three-tier Tier 1/2/3 model, idle-freeze, and JSONL persistence port directly into the rebuild). Everything below describes the fork's *frozen* state, **not a live roadmap** — the phase table in particular is historical. It was retired because its single-active-account + OAuth-API model could not deliver simultaneous live tracking of three accounts: there's no stable per-account identity in `~/.claude/.credentials.json` (`organizationUuid` is present for only some accounts; the rest is opaque rotating tokens), refresh-token rotation makes API polling of idle accounts unsafe, and the Rust+C# split was a recurring toolchain drag. The rebuild uses per-account WebView2 cookie-sessions (no OAuth refresh code) and raw-Win32 taskbar embedding from C#. See ADR-015 for the whole story.

> This file orients a Claude Code session opening this repo for the first time. The upstream `README.md` describes the original CodeZeno app — read this for what's different in **this fork**.

## What this project is

A Windows **taskbar widget** that:

1. Shows live Claude (and optional Codex) usage in the system taskbar — inherited from upstream
2. Forwards every observation to a co-located **C# predictor sidecar** that runs a three-tier probabilistic prediction model and renders a hover-popup chart

The fork is owned by [@Turrabo](https://github.com/Turrabo); upstream is [CodeZeno/Claude-Code-Usage-Monitor](https://github.com/CodeZeno/Claude-Code-Usage-Monitor) (MIT). Upstream credit is preserved in `LICENSE`.

## Two-binary architecture

```
ccum-host.exe (Rust, upstream + minimal hooks)
    │
    │  line-delimited JSON over stdin/stdout
    │  (versioned envelope: { "v": 1, "type": "...", ... })
    ▼
ccum-predictor.exe (C#, .NET 9 self-contained single-file)
```

The host process owns the UI, polls Anthropic's authenticated OAuth usage endpoint, and spawns the predictor as a child process. The predictor is a headless console app: stdin is for observations + shutdown messages, stdout is for log + prediction messages, stderr is for unstructured diagnostic output forwarded to the host's log.

See [`DECISIONS.md`](DECISIONS.md) for *why* it's two binaries, and [`docs/BUILD.md`](docs/BUILD.md) for build paths.

## Upstream-merge discipline

The fork is designed to absorb upstream changes with minimal conflict:

- **All fork-authored code lives in new files**: `predictor/`, `src/csm/`, `tools/`, `docs/`, top-level docs
- **Edits to upstream files are kept tiny and sentinel-marked** (`// === CSM EXTENSIONS BEGIN ===` / `// === CSM EXTENSIONS END ===`) — currently four sites: `src/main.rs` (module decl; sidecar/popup/badge/hover init; sidecar/badge/popup/hover shutdown — three sentinel blocks) and `src/poller.rs` (two-line observation hook: derive active account_id, then record_observation)
- **Upstream's `README.md` and `LICENSE` are not modified** — keep them as-is for clean fast-forwards from upstream
- **One upstream file is fully replaced**: `.github/workflows/release.yml`. Upstream's submits to their WinGet package; ours bundles both binaries into a zip Release. Future upstream changes to that file will surface as a merge conflict, which is the intended divergence signal.

`.github/workflows/upstream-sync.yml` merges from upstream and compiles the result before publishing it. It ran daily until 2026-09-04 and is now **manual dispatch only**. The repository is archived, and upstream has moved far enough that `src/csm/` no longer compiles against it, so the daily run did nothing but mail a failure. [`DECISIONS.md`](DECISIONS.md) ADR-016 is the whole story; ADR-007 is the original design.

## Build & toolchain

The maintainer's machine has VS Build Tools 2022 (17.14.x) installed at `C:\BuildTools\` (no-spaces install path — ADR-013 in [`DECISIONS.md`](DECISIONS.md) explains why that matters and which workarounds were retired). The build paths are:

- **Local builds (compile, test, clippy)**: `cargo build --release` against the default `stable-x86_64-pc-windows-msvc` toolchain. Source `C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat` once per shell, or launch from "Developer PowerShell for VS 2022", so `cl.exe`, `link.exe`, and `rc.exe` are on `PATH`. Build time: ~27 seconds clean.
- **CI builds**: GitHub Actions on `windows-latest` (full MSVC pre-installed). Every push to any branch triggers `build-host`; pushes touching `predictor/**` also trigger `build-predictor`. Download `ccum-host-<sha>` and `ccum-predictor-<sha>` from the Actions tab.
- **C# predictor**: `dotnet publish predictor/Predictor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true` produces a ~36 MB single-file exe. No MSVC dependency.

See [`docs/BUILD.md`](docs/BUILD.md) for full step-by-step instructions on each path.

**Local builds on the maintainer's machine work.** `cargo build --release` produces a runnable host binary directly. The earlier "USER32 0x35532 crash" claim was a misattribution — a stale `rustup override` was routing the build through gnullvm despite ADR-013 declaring MSVC canonical. `rustup override unset` from inside the repo dropped the override; dumpbin diff between CI and local MSVC binaries showed only LTO-noise (timestamps, PDB GUIDs, ~0x100-byte address shifts) with no behaviourally-significant differences. See ADR-014 in [`DECISIONS.md`](DECISIONS.md).

## Phase plan (HISTORICAL — project frozen 2026-05-27, see ADR-015)

> This table is the frozen record of what shipped on the fork. It is **not** a live roadmap — Phase 7d/7e and Phase 8 will not be built here. Forward work is in [`docs/REBUILD-PLAN.md`](docs/REBUILD-PLAN.md).

| Phase | Status | Scope |
|---|---|---|
| 0 | ✅ shipped | Fork scaffold: CI workflows (`build-host`, `build-predictor`, `upstream-sync`), predictor csproj skeleton |
| 0.5 | ✅ shipped | Local gnullvm dev pipeline (retired 2026-05-26 in ADR-013; see ADR-005 + Appendix A for the libunwind static-link recipe if ever needed) |
| 1 | ✅ shipped | Predictor sidecar IPC plumbing: spawn/supervise, line-delimited JSON contract, observation forwarding |
| 2 | ✅ shipped | Port **Tier 1** (linear burn rate) and **Tier 2** (Monte Carlo) predictor from CSM; predictor emits real `prediction` messages |
| 3 | ✅ shipped | Port **JSONL tail adapter** and **Tier 3 Hawkes** burst model; predictions become rhythm-aware |
| 4 | ✅ shipped | **Hover popup window** with chart and risk-coloured projection (Win32 GDI, hover-poll-driven over the widget) |
| 5 | ✅ shipped | **JSONL persistence + one-time CSM SQLite migration**: predictor writes every observation to history.jsonl, imports the predecessor's csm.sqlite truth-source rows on first run |
| 6 | ✅ shipped | Distribution: tag-triggered release packaging — zip on GitHub Releases bundles both binaries plus a README explaining the Mark-of-the-Web unblock and the SmartScreen click-through. Code-signing is out of scope and not planned. |
| 7 | in progress | **Multi-auth + cross-machine sync.** Per-account state in the predictor (one `ObservationWindow` per Claude OAuth identity), active-account detection from `~/.claude/credentials.json`, badge shows the local active account, popover gains a per-account cross-machine table. Sync via a personal Cloudflare Worker (`sync.daisybot.co.uk`) + D1 + Access service tokens — maintainer-only, off by default for colleagues. See ADR-011, ADR-012, and [`docs/PHASE-7-PLAN.md`](docs/PHASE-7-PLAN.md). |
| 8 | future | **Predictor learning loops.** Tune the WLS/Hawkes/idle-freeze constants per-account from accumulated history. Requires Phase 7 cross-machine data to be flowing. |

Completed phases live in `git log` and `DECISIONS.md`. Forward direction lives here. Do not retro-edit this table to add notes about completed work — those belong in commit messages and `DECISIONS.md`.

## What's ported from the predecessor (CSM)

The previous project at `C:\Source\Claude Session Monitor\` (frozen archive on this developer's machine, not in this repo) had a working three-tier predictor and JSONL telemetry adapter. Phases 2–5 above are explicitly about **porting that logic into this fork's predictor process**, not redesigning it. The math (Hawkes self-excitation, Monte Carlo, burn-rate weighting) is well-validated and ports verbatim; only the storage (SQLite → JSONL), the process model (single-process → sidecar over stdin/stdout), and the UI (WPF dashboard → Win32 GDI hover popup) change.

## Conventions

- **Storage**: predictor writes to `%APPDATA%\Claude-Code-Usage-Monitor\predictor\` — `history.jsonl` (observations, every poll appended), `.csm-migrated` (one-time first-run sentinel). `events.jsonl` (Claude Code message timings) and `state.json` (Hawkes parameters cache) are still pending — Hawkes state currently lives only in-process.
- **Logging**: host uses `src/diagnose.rs` (writes to `%TEMP%\claude-code-usage-monitor.log` when `--diagnose` is passed); predictor emits `LogMessage` over stdout which the host reader forwards to the same file. No separate predictor log file.
- **Comments in code**: don't narrate task or PR context (CLAUDE.md global rule); explain non-obvious *why*. The IPC protocol comments at the top of `predictor/Ipc/Messages.cs` and `src/csm/ipc.rs` are the load-bearing exception.
- **File-system access**: since Phase 3 the predictor tails `~/.claude/projects/**/*.jsonl` read-only via [`predictor/Adapters/JsonlTail.cs`](predictor/Adapters/JsonlTail.cs) to harvest assistant-message timestamps for the Hawkes model. It does not read `~/.claude/.credentials.json` — auth is the upstream host's job. Phase 5 also reads `%LOCALAPPDATA%\ClaudeSessionMonitor\csm.sqlite` read-only on first run for the one-time CSM migration.

## When in doubt

- Architecture decisions: [`DECISIONS.md`](DECISIONS.md)
- Build / run procedure: [`docs/BUILD.md`](docs/BUILD.md)
- Original upstream behaviour: upstream `README.md`
- Predecessor project (archive): `C:\Source\Claude Session Monitor\` on the developer's machine, read-only reference
