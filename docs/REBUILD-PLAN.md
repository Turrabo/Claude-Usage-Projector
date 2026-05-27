# Rebuild plan — single-stack C# own-widget

> Forward-direction plan only. Sets the roadmap for retiring the CodeZeno fork
> and rebuilding as a single-stack C# / .NET widget. The *why* is in
> [`DECISIONS.md`](../DECISIONS.md) ADR-015; this doc is the *order of work*.
> Supersedes [`PHASE-7-PLAN.md`](PHASE-7-PLAN.md) as the active plan — Phase 7
> work on the fork is frozen (the fork remains a working reference and the
> source of the portable predictor).

## Preamble — how we got here

We started by building our own widget (Claude Session Monitor: WinUI 3 + WebView2
scraping of `claude.ai`). In May 2026 we pivoted to forking
CodeZeno/Claude-Code-Usage-Monitor (ADR-001) for three reasons: a clean
taskbar-embedded widget, the undocumented OAuth usage API, and zero-install
polish. We built seven phases on the fork — a Rust-host ↔ C#-predictor sidecar,
the three-tier prediction model, a hover popover, a companion badge, and
multi-auth scaffolding.

The fork stopped fitting once the goal crystallised into **three accounts, live,
all the time + runout prediction on the active one + one-click switching**. The
blockers (full detail in ADR-015):

- The fork's whole model is single-active-account — it reads the one credential
  the Claude CLI maintains. Multi-account has no home in that design.
- There's no stable per-account identity in the credentials file. `organizationUuid`
  is present for only one of the three accounts; the rest is opaque rotating tokens.
- Refresh tokens rotate. A widget polling non-active accounts via the OAuth API
  would have to refresh their tokens itself — which invalidates the Claude CLI's
  stored sessions and logs the user out. Unwanted and unsafe.
- The Rust+C# split cost weeks of toolchain debugging.

Re-weighed, the fork's only durable unique value is taskbar embedding — and that's
reproducible in raw Win32 from C# (the WinUI-3-era "impossible" was a framework
limit, not a fundamental one). The OAuth endpoint isn't a moat; i18n and
self-update aren't valued.

## Direction

A single-stack **C# / .NET** widget that:

- Holds **one persistent web session per account** (cookie-based, via WebView2),
  sidestepping OAuth token rotation entirely — the browser owns the auth
  lifecycle, we never write refresh-token code.
- Polls each account's usage (~30s cadence for idle accounts), preferring to
  **intercept the usage network call** the web app makes over scraping the DOM.
- **Embeds in the taskbar via raw Win32** (P/Invoke), not WinUI 3.
- Reuses the **three-tier predictor** ported from the fork.
- Knows each account's identity **by construction** (it owns the labelled
  sessions), so the alias/identity problem disappears.
- Offers **one-click account switching**.

## Phase 0 — De-risk spikes (gate the rebuild)

Do these before committing to the full build. Both are throwaway proofs.

### Spike A — raw-Win32 taskbar embedding from C#

- Build a minimal C# app that `CreateWindowEx`-es a child window, parents it under
  `Shell_TrayWnd` (or the appropriate tray host), and positions it next to the
  clock/tray like CodeZeno does.
- **Success:** a small docked window that stays correctly positioned across a DPI
  change and an `explorer.exe` restart.
- **If it fights us:** fall back to keeping CodeZeno's Rust host as a *thin
  renderer* driven by the C# data layer over IPC, rather than a full rebuild.

### Spike B — concurrent multi-session WebView2 polling

- Two `WebView2` instances, each with its own user-data folder (separate cookie
  containers), each logged into a different Claude account.
- Read each account's current usage — ideally by hooking the `WebResourceResponseReceived`
  (or equivalent) event to capture the usage API call the web app makes, rather
  than DOM scraping.
- **Success:** both accounts' live usage read concurrently from one process,
  surviving an access-token refresh without us writing any refresh code (the web
  session handles it).
- **Watch:** memory (two Chromium instances ≈ 200-400 MB), and how re-auth
  surfaces when a session lapses (should be a login prompt, not a silent failure).

## Phase 1 — Widget shell

Raw-Win32 taskbar-embedded window in C#; basic layered-window rendering (the badge
visuals can port conceptually from the fork's `badge.rs`). Survives explorer
restarts and DPI changes.

## Phase 2 — Multi-session data layer

N persistent WebView2 sessions, one per configured account. Per-account usage
extraction (network-intercept preferred). ~30s poll cadence. Graceful handling of
a lapsed session (surface a re-login affordance).

## Phase 3 — Port the predictor

Lift the three-tier model (Tier 1 weighted burn-rate, Tier 2 Monte Carlo, Tier 3
Hawkes), idle-freeze, and JSONL persistence from the fork's `predictor/`. It's
already C#; the main change is feeding it from the new data layer instead of the
IPC observe messages.

## Phase 4 — Per-account UI

- Badge: runout + risk for the **active** account (the one in VS Code).
- Popover: the per-account table for **all** accounts (ports conceptually from the
  fork's Phase 7c `popup.rs` table).

## Phase 5 — One-click account switcher

Selecting an account in the popover makes it active for `claude` / the VS Code
extension. Mechanism TBD — see open questions.

## Phase 6 — Persistence, projects, polish, packaging

Per-account history persistence, project-level usage tracking, single-file
self-contained publish, Mark-of-the-Web/SmartScreen handling for distribution.

## Open questions (resolve during the work)

- **New repo or in-place restructure?** Leaning new repo that imports the
  predictor as a library/source copy; keeps the frozen fork clean as reference.
- **What does the usage web-call actually return**, and is it the same data the
  OAuth endpoint gave (5h/7d buckets + reset times)? Confirm in Spike B.
- **Account-switch mechanism.** Does it overwrite `~/.claude/.credentials.json`
  with the chosen account's stored credentials? Trigger `claude login`? Signal the
  VS Code extension to reload? Needs investigation — the widget's web sessions are
  cookie-based, but the CLI/extension reads `.credentials.json` (OAuth tokens), so
  bridging "the widget's notion of active account" to "the CLI's active account"
  is non-trivial and may itself run into the rotation problem we're trying to dodge.
- **WebView2 memory ceiling** with three sessions — acceptable, or do we need a
  lighter headless-auth approach?
- **Scraping robustness** — network-intercept vs DOM; how often does the web app's
  internal shape change?
- **Does the predictor's JSONL/Hawkes telemetry** (currently fed by tailing
  `~/.claude/projects/**/*.jsonl`) still make sense, and is it per-account or
  machine-scoped in the new model?

## What carries over from the fork (not wasted)

- The entire `predictor/` C# project — Tiers, Hawkes, Monte Carlo, persistence,
  CSM SQLite migrator.
- The prediction UX design — badge + popover table, risk colours, runout framing.
- The hard-won knowledge of *what data the usage signal contains* and how to
  project runout from it.
- Lessons in ADRs 011–015 about multi-account identity, persistence sharding, and
  why the single-account model couldn't stretch.
