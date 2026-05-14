# Claude-Usage-Projector — fork context

> This file orients a Claude Code session opening this repo for the first time. The upstream `README.md` describes the original CodeZeno app — read this for what's different in **this fork**.

## What this project is

A Windows **taskbar widget** that:

1. Shows live Claude (and optional Codex) usage in the system taskbar — inherited from upstream
2. Forwards every observation to a co-located **C# predictor sidecar** that runs a three-tier probabilistic prediction model and (in later phases) renders a hover-popup chart

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
- **Edits to upstream files are kept tiny and sentinel-marked** (`// === CSM EXTENSIONS BEGIN ===` / `// === CSM EXTENSIONS END ===`) — currently three sites: `src/main.rs` (module decl + sidecar init/shutdown calls) and `src/poller.rs` (one-line observation hook)
- **Upstream's `README.md` and `LICENSE` are not modified** — keep them as-is for clean fast-forwards from upstream

A daily GitHub Actions workflow (`.github/workflows/upstream-sync.yml`) attempts a fast-forward merge from upstream; conflicts surface as failed runs for manual resolution.

## Build & toolchain

The user's development machine has a corporate IT block that prevents installing MSVC C++ Build Tools. As a result:

- **Local dev**: Rust GNU/gnullvm + LLVM-MinGW (via `tools/dev-build.ps1`) for compile-checks only. The resulting host binary doesn't launch correctly — see the gnullvm runtime bug in auto-memory and [`docs/BUILD.md`](docs/BUILD.md).
- **Runnable binaries**: GitHub Actions on `windows-latest` (full MSVC pre-installed) — push a branch, download the `build-host` and `build-predictor` artifacts.
- **C# predictor local**: works locally with just the .NET 9 SDK; `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true` produces a ~35 MB single-file exe with no install prerequisite for end users.

If you are reading this on a machine where MSVC **is** available, the simpler path is the upstream's `cargo build --release` and you can ignore most of the gnullvm machinery.

## Phase plan (forward-looking)

| Phase | Status | Scope |
|---|---|---|
| 0 | ✅ shipped | Fork scaffold: CI workflows (`build-host`, `build-predictor`, `upstream-sync`), predictor csproj skeleton |
| 0.5 | ✅ shipped | Local gnullvm dev pipeline (`tools/dev-build.ps1`, `.cargo/config.toml`) |
| 1 | ✅ shipped | Predictor sidecar IPC plumbing: spawn/supervise, line-delimited JSON contract, observation forwarding |
| 2 | pending | Port **Tier 1** (linear burn rate) and **Tier 2** (Monte Carlo) predictor from CSM; predictor emits real `prediction` messages |
| 3 | pending | Port **JSONL tail adapter** and **Tier 3 Hawkes** burst model; predictions become rhythm-aware |
| 4 | pending | **Hover popup window** with chart, projection band, tier indicator (Win32 GDI, on `WM_MOUSEHOVER` over the widget) |
| 5 | pending | One-time **migration tool** to import historical signals from the frozen CSM SQLite DB into the predictor's JSONL storage |
| 6 | pending | Polish for colleague distribution: first-run UX, error states, code-signed release |

Completed phases live in `git log` and `DECISIONS.md`. Forward direction lives here. Do not retro-edit this table to add notes about completed work — those belong in commit messages and `DECISIONS.md`.

## What's ported from the predecessor (CSM)

The previous project at `C:\Source\Claude Session Monitor\` (frozen archive on this developer's machine, not in this repo) had a working three-tier predictor and JSONL telemetry adapter. Phases 2–5 above are explicitly about **porting that logic into this fork's predictor process**, not redesigning it. The math (Hawkes self-excitation, Monte Carlo, burn-rate weighting) is well-validated and ports verbatim; only the storage (SQLite → JSONL), the process model (single-process → sidecar over stdin/stdout), and the UI (WPF dashboard → Win32 GDI hover popup) change.

## Conventions

- **Storage**: predictor writes to `%APPDATA%\Claude-Code-Usage-Monitor\predictor\` — `history.jsonl` (observations), `events.jsonl` (Claude Code message timings, Phase 3+), `state.json` (Hawkes parameters, last prediction).
- **Logging**: host uses `src/diagnose.rs` (writes to `%TEMP%\claude-code-usage-monitor.log` when `--diagnose` is passed); predictor emits `LogMessage` over stdout which the host reader forwards to the same file. No separate predictor log file.
- **Comments in code**: don't narrate task or PR context (CLAUDE.md global rule); explain non-obvious *why*. The IPC protocol comments at the top of `predictor/Ipc/Messages.cs` and `src/csm/ipc.rs` are the load-bearing exception.
- **No new permission scopes**: the predictor reads `~/.claude/.credentials.json` only via the upstream host (it does not open files in `~/.claude/` itself yet — that lands in Phase 3 with the JSONL tail adapter).

## When in doubt

- Architecture decisions: [`DECISIONS.md`](DECISIONS.md)
- Build / run procedure: [`docs/BUILD.md`](docs/BUILD.md)
- Original upstream behaviour: upstream `README.md`
- Predecessor project (archive): `C:\Source\Claude Session Monitor\` on the developer's machine, read-only reference
