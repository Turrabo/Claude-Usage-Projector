# Building Claude-Usage-Projector

There are two binaries: `ccum-host.exe` (Rust) and `ccum-predictor.exe` (C# / .NET 9). They are designed to live side-by-side in the same folder at runtime. The host spawns the predictor as a child process; if the predictor isn't co-located the host runs fine but the sidecar is a silent no-op.

This guide covers four workflows:

1. [Building on a machine with MSVC available](#standard-msvc-path) — the simple path
2. [Building locally without MSVC via cargo-xwin](#no-msvc-runnable-cargo-xwin-path) — runnable local binaries on a machine that can't install MSVC Build Tools (the main developer machine)
3. [Building locally without MSVC via gnullvm](#no-msvc-compile-check-gnullvm-path) — compile-check only, kept as a fallback (the binary doesn't launch — see ADR-005)
4. [Using CI-built artifacts](#ci-artifact-path) — when neither local path applies, or for clean reproducible builds

If you are not the project maintainer on the specific machine where it was set up, you almost certainly want path (1) or (4).

---

## Standard MSVC path

**Prerequisites**

- Windows 10/11
- [Rust](https://rustup.rs/) (stable, x86_64-pc-windows-msvc)
- [Visual Studio Build Tools 2022](https://visualstudio.microsoft.com/downloads/?q=build+tools) with the **"Desktop development with C++"** workload
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

**Build the host**

```powershell
cargo build --release
# Produces target/release/claude-code-usage-monitor.exe
```

**Build the predictor**

```powershell
dotnet publish predictor/Predictor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
# Produces predictor/bin/Release/net9.0/win-x64/publish/ccum-predictor.exe
```

**Run**

Copy `ccum-predictor.exe` next to the host exe:

```powershell
Copy-Item predictor/bin/Release/net9.0/win-x64/publish/ccum-predictor.exe target/release/
target/release/claude-code-usage-monitor.exe --diagnose
```

The widget should appear in your taskbar. Add `--diagnose` to log to `%TEMP%\claude-code-usage-monitor.log`.

---

## No-MSVC runnable (cargo-xwin) path

If `Microsoft.VisualStudio.2022.BuildTools` cannot be installed on your machine (corporate IT block, AV interference, or any other reason — symptoms: the bootstrapper runs and downloads dependencies but the `Microsoft.VC.Tools.*` payload never lands, `vswhere` reports no installations), use this path. It produces a **runnable** MSVC-target binary in ~10s incremental, ~30s clean — same as `cargo build --release` would on a machine with MSVC installed.

The trick: `cargo-xwin` downloads the MSVC SDK headers + libraries directly from Microsoft's CDN as raw files (bypassing the installer bootstrapper that the corporate block typically targets) and uses LLVM-MinGW's `lld` as the linker. The CDN raw-file downloads pass through the same network controls that allow normal HTTPS to Microsoft from a browser.

**Prerequisites**

- Windows 10/11 with admin-on-demand (UAC) available
- Rust + LLVM-MinGW already installed (same as the gnullvm path below)
- The Rust gnullvm toolchain with the msvc target added:
  ```powershell
  rustup target add x86_64-pc-windows-msvc --toolchain stable-x86_64-pc-windows-gnullvm
  ```
- `xwin` and `cargo-xwin` installed via the gnullvm toolchain (because installing under msvc fails without link.exe):
  ```powershell
  cargo +stable-x86_64-pc-windows-gnullvm install --locked --target x86_64-pc-windows-gnullvm xwin
  cargo +stable-x86_64-pc-windows-gnullvm install --locked --target x86_64-pc-windows-gnullvm cargo-xwin
  ```
- The MSVC SDK populated into cargo-xwin's cache. This step is **one-time** and needs admin (UAC) to create one version-pointer symlink inside the SDK directory:
  ```powershell
  # In an ELEVATED PowerShell, once per machine:
  xwin --accept-license splat --output "$env:LOCALAPPDATA\cargo-xwin\xwin"
  ```

**Build**

```powershell
./tools/dev-build-msvc.ps1
# Produces target/x86_64-pc-windows-msvc/release/claude-code-usage-monitor.exe (~0.82 MB, runnable)
```

The script handles three things the bare `cargo xwin build` invocation doesn't:
- Creates an `lld-link.exe` shim in `~/.cargo/bin` (LLVM-MinGW ships `ld.lld.exe` but Rust's msvc target expects `lld-link.exe` — same binary, different driver-mode name)
- Adds LLVM-MinGW to `PATH` so cargo-xwin's `--cross-compiler clang` finds clang
- Sets `SKIP_WINRES=1` so `build.rs` skips the icon + version-metadata embed (which needs Microsoft's `rc.exe`, not in the xwin SDK)

**Build the predictor**

Unaffected by the MSVC block. Same command as the standard path:

```powershell
dotnet publish predictor/Predictor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

**Run**

Pair the host with any recent `ccum-predictor.exe` (a CI artifact works fine if you don't want to rebuild the predictor each time) and launch with `--diagnose`. The widget should appear in the taskbar identically to a CI-built binary.

---

## No-MSVC compile-check (gnullvm) path

This is the older fallback. The binary compiles successfully but **does not launch correctly** (silently exits ~5s after startup — see [DECISIONS.md ADR-005](../DECISIONS.md) and [the gnullvm runtime bug section below](#known-limitation-gnullvm-binary-doesnt-launch)). Useful for `cargo check`, `cargo clippy`, and editor type-checking when you don't want to invoke the full cargo-xwin pipeline. For runnable binaries, use the cargo-xwin path above.

**Prerequisites**

- Windows 10/11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Install Rust:
  ```powershell
  winget install Rustlang.Rustup
  ```
- Install LLVM-MinGW (provides `windres`, `dlltool`, `gcc`, `lld`):
  ```powershell
  winget install MartinStorsjo.LLVM-MinGW.UCRT
  ```
- Install the Rust gnullvm toolchain:
  ```powershell
  rustup toolchain install stable-x86_64-pc-windows-gnullvm
  rustup override set stable-x86_64-pc-windows-gnullvm
  ```

**Build the host**

```powershell
./tools/dev-build.ps1
# Produces target/release/claude-code-usage-monitor.exe (compile-only — see limitation below)
```

The script temporarily renames LLVM-MinGW's `libunwind.dll.a` so the linker is forced to statically link `libunwind` instead — without this, the resulting binary has a `libunwind.dll` runtime dependency, and even with it, see the next section.

**Build the predictor**

Unaffected by the MSVC block. Same command as the standard path:

```powershell
dotnet publish predictor/Predictor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

### Known limitation: gnullvm binary doesn't launch

The host binary built via the gnullvm path compiles, links, runs through its startup sequence, gets to `tray event hook installed` in the diagnose log, then silently exits ~5 seconds later — before reaching `position_at_taskbar` and `ShowWindow`. The same source built with MSVC (CI artifact, or any machine with VS Build Tools) launches and runs normally. Most likely cause is an ABI mismatch in Win32 callback dispatch through statically linked compiler-rt + libunwind. Not pursued further because the CI path solves it without further work.

So the gnullvm path is useful for:

- `cargo check`, `cargo clippy`, type-checking
- Verifying compilation succeeds after edits
- Smoke-testing the predictor + IPC contract (the predictor exe works fine locally; you can pipe JSON observations into it from the command line)

Not useful for:

- Actually running the host widget locally — use a CI artifact for that.

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
[<ts>] predictor[info] ccum-predictor v0.5.0 started (pid=<n>)
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
