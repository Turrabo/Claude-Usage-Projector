---
name: verify-build
description: Smoke-test a Claude-Usage-Projector CI build end-to-end on this Windows machine. Resolves the target commit, waits for CI green, kills any running widget, clears C:\Source\Claude-Usage-Projector\target\verify-build\, downloads both GitHub Actions artifacts via gh CLI, flattens them so the host can find the predictor, unblocks the .exes, and launches the host with --diagnose. Stops short of log inspection — that's the user's call.
---

# /verify-build — fetch & launch a CI build for E2E testing

User-facing trigger: `/verify-build` (no arg → current `HEAD`), `/verify-build <branch>`, `/verify-build <sha>`, `/verify-build <PR#>`.

## What this skill is for

After CI goes green on a feature branch, we want to actually run the resulting binaries on the developer's Windows machine. Manually that means: kill the widget, clear a folder, find the right two GitHub Actions artifacts, download both, flatten the per-artifact subdirs, `Unblock-File` both, launch. This skill automates all of it.

Repo: `Turrabo/Claude-Usage-Projector`. Working directory is the repo root.

## Pre-flight (one-time per machine)

Before the first run, the skill needs `gh` (GitHub CLI) installed and authenticated:

- **gh installed**: detect with `Get-Command gh` or by probing `C:\Program Files\GitHub CLI\gh.exe`. If absent: `winget install --id GitHub.cli --silent --accept-source-agreements --accept-package-agreements` and remember the install path (current session's PATH does NOT pick up the new exe — use the absolute path).
- **gh authenticated**: `& $gh auth status`. If not authenticated: run `& $gh auth login --hostname github.com --git-protocol https --web` in the background via Bash `run_in_background:true`, then Read the output file to surface the device code (`First copy your one-time code: XXXX-XXXX`) to the user, then wait for the background task to complete.

After this is done once, gh's token is stored in the Windows keyring and survives sessions.

## Steps to perform

1. **Resolve target SHA.** Map the argument to a 40-char commit SHA:
   - no arg → `git rev-parse HEAD`
   - looks like a branch name (no slashes, not 40 hex chars, not a small integer) → `git rev-parse origin/<arg>` (fetch first if it might be stale)
   - looks like a 40-hex SHA → use as-is
   - numeric and < 10000 → treat as PR number; resolve via `gh pr view <n> --json headRefOid -q '.headRefOid'`

2. **Confirm CI is green for that SHA.** Query
   `https://api.github.com/repos/Turrabo/Claude-Usage-Projector/actions/runs?head_sha=<sha>&per_page=20`
   and find the most recent `build-host` and `build-predictor` runs. Use REST (Invoke-RestMethod) — no auth needed for public repo listing.
   - If either is still `in_progress` or `queued`, arm a Monitor that polls every 25–30s. The Monitor must emit on **all** terminal states (`success`, `failure`, `cancelled`), not just success — silence-on-failure looks identical to silence-on-running. Exit when both runs are `completed`.
   - If either has `conclusion != success`, surface the failing run's `html_url` and stop. Do not proceed.
   - **Save the two run IDs** (`build-host` and `build-predictor`) — gh needs them to download.

3. **Stop any running widget.** Idempotent:
   ```powershell
   Get-Process claude-code-usage-monitor -ErrorAction SilentlyContinue | Stop-Process -Force
   ```

4. **Prepare the sandbox at `C:\Source\Claude-Usage-Projector\target\verify-build\`.** Always wipe first — stale files from a prior verify would be co-located with new ones and the host would launch the wrong predictor. Recreate empty:
   ```powershell
   $sandbox = 'C:\Source\Claude-Usage-Projector\target\verify-build'
   Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
   New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
   ```
   `target/` is gitignored, so this never pollutes the repo. The path is stable (not SHA-scoped) so the user always knows where to look.

5. **Download both artifacts via gh:**
   ```powershell
   & $gh run download <build-host-run-id> --repo Turrabo/Claude-Usage-Projector --dir $sandbox
   & $gh run download <build-predictor-run-id> --repo Turrabo/Claude-Usage-Projector --dir $sandbox
   ```
   `$gh` is the absolute path to gh.exe (typically `C:\Program Files\GitHub CLI\gh.exe`).

6. **Flatten the per-artifact subdirs.** `gh run download` extracts each artifact into its own subdirectory named after the artifact (e.g. `ccum-host-<sha>/`, `ccum-predictor-<sha>/`). The host's sidecar locator (`src/csm/sidecar.rs::locate_predictor`) only looks for `ccum-predictor.exe` in the same directory as the host exe — so we must collapse:
   ```powershell
   Get-ChildItem $sandbox -Recurse -Filter *.exe | Move-Item -Destination $sandbox -Force
   Get-ChildItem $sandbox -Directory | Remove-Item -Recurse -Force
   ```
   Verify both `claude-code-usage-monitor.exe` and `ccum-predictor.exe` now sit directly under `$sandbox`. If either is missing, stop and tell the user which one.

7. **Unblock both exes** (Mark of the Web from the download blocks execution):
   ```powershell
   Get-ChildItem $sandbox -Filter *.exe | Unblock-File
   ```

8. **Truncate the diagnose log** so the user sees only this run's output:
   ```powershell
   $log = "$env:TEMP\claude-code-usage-monitor.log"
   if (Test-Path $log) { Set-Content $log -Value '' }
   ```

9. **Launch the host** with `--diagnose`. WorkingDirectory matters — the predictor's `current_exe().parent()` lookup is relative to where the host's exe is, which is the sandbox:
   ```powershell
   Start-Process -FilePath "$sandbox\claude-code-usage-monitor.exe" -ArgumentList '--diagnose' -WorkingDirectory $sandbox
   Start-Sleep -Seconds 3
   $proc = Get-Process claude-code-usage-monitor -ErrorAction SilentlyContinue
   ```
   If `$proc` is null after 3s, SmartScreen / UAC silently blocked the start. Don't retry — tell the user to double-click `$sandbox\claude-code-usage-monitor.exe` themselves.

10. **Hand off.** Tell the user:
    - The widget should be visible in the taskbar
    - The diagnose log is at `%TEMP%\claude-code-usage-monitor.log`
    - The first poll runs ~5 seconds after launch; the predictor needs ≥2 snapshots over ≥5 minutes before WLS produces real rate values, so early `predictor[pred]` lines may show `rate=?/min p50=none`

## Notes

- This skill is read-only with respect to the repo's git state. It doesn't fetch, merge, push, or tag.
- "Both binaries co-located" is a hard requirement for the sidecar to spawn. The flatten step is not optional.
- The host appears as a small widget embedded directly in the Windows taskbar (it's a child of `Shell_TrayWnd`); it does NOT have its own window. Visual confirmation = user sees the new bars in their taskbar.
- The CSM EXTENSIONS sentinel hooks in `src/main.rs` and `src/poller.rs` are what wire predictions through. If the log shows `csm: predictor sidecar disabled — ...`, the host couldn't find the predictor exe — that's the flatten step having failed.
- gh artifact downloads use the API endpoint that returns a signed blob URL — public repo or not, this requires `actions:read` scope, which `gh auth login` grants by default.
