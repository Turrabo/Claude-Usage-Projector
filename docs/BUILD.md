# Building Claude-Usage-Projector

There are two binaries: `ccum-host.exe` (Rust) and `ccum-predictor.exe` (C# / .NET 9). They are designed to live side-by-side in the same folder at runtime. The host spawns the predictor as a child process; if the predictor isn't co-located the host runs fine but the sidecar is a silent no-op.

Two supported workflows:

1. [Building locally with MSVC](#standard-msvc-path) — the canonical path
2. [Using CI-built artifacts](#ci-artifact-path) — for clean reproducible builds or machines without MSVC installed

Earlier revisions of this guide also described a cargo-xwin path and a gnullvm path; both were retired on 2026-05-26 when VS Build Tools turned out to install fine after all (the prior "MSVC blocked by IT" diagnosis was a PowerShell argument-quoting bug; see ADR-013). The history of those workarounds is preserved in ADR-005, ADR-010, and ADR-013 in [`../DECISIONS.md`](../DECISIONS.md).

---

## Standard MSVC path

**Prerequisites**

- Windows 10/11
- [Rust](https://rustup.rs/) (stable, x86_64-pc-windows-msvc — rustup's default on Windows)
- [Visual Studio Build Tools 2022](https://visualstudio.microsoft.com/downloads/?q=build+tools) with the **"Desktop development with C++"** workload. **Install to a path without spaces** if you're invoking `setup.exe` via `Start-Process -Verb RunAs` from a script — PowerShell's elevation arg handling silently truncates spaced paths at the first space. The maintainer's machine uses `C:\BuildTools\`. See ADR-013 for the rabbit hole this caused.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

**Build the host**

Source the MSVC environment first so `cl.exe`, `link.exe`, `rc.exe` are on `PATH` and `INCLUDE`/`LIB` are set. Adjust the path for your install location (the maintainer's is `C:\BuildTools\`):

```powershell
& 'C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
# ...or launch directly from the "Developer PowerShell for VS 2022" shortcut.

cargo build --release
# Produces target/release/claude-code-usage-monitor.exe (~1 MB, with icon and version metadata)
```

**Build the predictor**

```powershell
dotnet publish predictor/Predictor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
# Produces predictor/bin/Release/net9.0/win-x64/publish/ccum-predictor.exe (~36 MB)
```

**Run**

Copy `ccum-predictor.exe` next to the host exe:

```powershell
Copy-Item predictor/bin/Release/net9.0/win-x64/publish/ccum-predictor.exe target/release/
target/release/claude-code-usage-monitor.exe --diagnose
```

The widget should appear in your taskbar. Add `--diagnose` to log to `%TEMP%\claude-code-usage-monitor.log`.

### Note on rustup directory overrides

If `rustup show` from inside the repo lists a non-MSVC toolchain (`gnullvm`, `gnu`) as active "because: directory override for 'C:\\Source\\Claude-Usage-Projector'", that's a leftover from the retired gnullvm/cargo-xwin paths. Run `rustup override unset` from inside the repo dir to drop it, then `cargo clean` and rebuild. The override is invisible from outside the repo (`rustup show` elsewhere reports the global default) but silently routes every `cargo build` through the wrong toolchain. ADR-014 in [`../DECISIONS.md`](../DECISIONS.md) records the day this caught us out.

---

## CI artifact path

The fork's GitHub Actions workflows build both binaries on every push to a non-`main` branch. The runners use MSVC, so the binaries are runnable on any modern Windows machine.

**Trigger a build**

Push any commit on a feature branch:

```powershell
git checkout -b some-branch
git commit --allow-empty -m "trigger ci"
git push -u origin some-branch
```

**Download artifacts**

1. Open the [Actions tab](https://github.com/Turrabo/Claude-Usage-Projector/actions) on GitHub
2. Click the latest `build-host` run → scroll to "Artifacts" → download `ccum-host-<sha>`
3. Click the latest `build-predictor` run → download `ccum-predictor-<sha>`

Each artifact is a zip containing one .exe.

**Run**

1. Unzip both into the same folder (e.g. `C:\dev\claude-usage-projector\`)
2. The downloaded exes are flagged with the Mark of the Web — unblock them:
   ```powershell
   Get-ChildItem C:\dev\claude-usage-projector\*.exe | Unblock-File
   ```
3. Double-click `claude-code-usage-monitor.exe` (or run from a command prompt to pass `--diagnose`)

If Windows SmartScreen objects on first launch ("Windows protected your PC"), click **More info** → **Run anyway**. The binaries are deliberately unsigned — see [`RELEASING.md`](RELEASING.md) and the Phase 6 row in [`../CLAUDE.md`](../CLAUDE.md).

---

## Verifying a build works end-to-end

After the host launches, the diagnose log at `%TEMP%\claude-code-usage-monitor.log` should contain lines similar to:

```
[<ts>] csm: spawning predictor at C:\...\ccum-predictor.exe
[<ts>] csm: predictor sidecar started
[<ts>] window shown
[<ts>] initial poll thread started
[<ts>] predictor[info] ccum-predictor v0.6.0 started (pid=<n>)
[<ts>] predictor[info] observed @ <iso8601>  cc 5h=<x>% 7d=<y>%  cx=<z|none>
[<ts>] predictor[pred] tier=2 risk=... used=...% rate=...%/min p50=... pE=... stale=... act=...
```

The last three lines prove the full IPC + prediction pipeline is working — the predictor logged its own startup, acknowledged an observation, and emitted a prediction the host re-formatted onto the diagnose log.

---

## Running tests

The predictor has an xUnit test project at [`predictor/Predictor.Tests/`](../predictor/Predictor.Tests/) wired into [`predictor/predictor.sln`](../predictor/predictor.sln). CI runs them automatically; locally:

```powershell
dotnet test predictor/predictor.sln -c Release
```

Tests cover the Hawkes math, the Monte Carlo projection engine, the JSONL adapter, the tier 1 predictor with idle-freeze, and the persistence + CSM migration paths. ~60 tests, sub-second runtime.
