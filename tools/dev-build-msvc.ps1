# Local dev build for the Rust host binary, MSVC target via cargo-xwin.
#
# Why this script exists:
#   ADR-005 documents the original `dev-build.ps1` flow that builds the host
#   against the gnullvm target — compile-checks only, the binary doesn't
#   launch correctly at runtime. This script is the runnable alternative:
#   `cargo xwin build` against the msvc target using a downloaded MSVC SDK
#   and LLVM-MinGW's LLD as the linker. The resulting .exe behaves identically
#   to a CI-built one. Iteration time: ~10s incremental, ~30s clean.
#
# One-time setup (already done if cargo-xwin and lld-link.exe exist):
#   1. cargo install --locked --target x86_64-pc-windows-gnullvm xwin
#   2. cargo install --locked --target x86_64-pc-windows-gnullvm cargo-xwin
#   3. rustup target add x86_64-pc-windows-msvc \
#          --toolchain stable-x86_64-pc-windows-gnullvm
#   4. Run `xwin --accept-license splat --output $env:LOCALAPPDATA\cargo-xwin\xwin`
#      once via an elevated shell to populate the SDK cache. The first splat
#      needs admin to create the SDK's version-pointer link; subsequent
#      builds don't.
#
# This script ensures the lld-link.exe shim exists and then runs the build.

# Note: we deliberately don't set `$ErrorActionPreference = 'Stop'` here.
# Cargo writes progress lines ("Compiling ...") to stderr, and PowerShell 5.1
# wraps each stderr line as a NativeCommandError which, under `Stop`, aborts
# the script mid-build. We rely on `$LASTEXITCODE` checks instead.

# Locate LLVM-MinGW (same logic as tools/dev-build.ps1).
$mingwRoot = $null
if (Test-Path Env:LLVM_MINGW_ROOT) {
    $mingwRoot = (Get-Item Env:LLVM_MINGW_ROOT).Value
}
if (-not $mingwRoot) {
    $localAppData = (Get-Item Env:LOCALAPPDATA).Value
    $candidate = Get-ChildItem (Join-Path $localAppData 'Microsoft\WinGet\Packages') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'MartinStorsjo.LLVM-MinGW.UCRT*' } |
        Select-Object -First 1
    if (-not $candidate) {
        throw 'LLVM-MinGW not found. Install: winget install MartinStorsjo.LLVM-MinGW.UCRT'
    }
    $mingwRoot = Get-ChildItem $candidate.FullName -Directory |
        Where-Object { $_.Name -like 'llvm-mingw-*' } |
        Select-Object -First 1 -ExpandProperty FullName
}
$mingwBin = Join-Path $mingwRoot 'bin'

# Ensure lld-link.exe exists on PATH. LLVM-MinGW ships ld.lld.exe (the GNU/MinGW
# driver name) but Rust's msvc target expects lld-link.exe (the MSVC driver
# name). It's the same underlying binary — LLD picks its mode by program name.
# We copy it into ~/.cargo/bin which is already on PATH.
$lldLink = Join-Path (Get-Item Env:USERPROFILE).Value '.cargo\bin\lld-link.exe'
if (-not (Test-Path $lldLink)) {
    $src = Join-Path $mingwBin 'ld.lld.exe'
    if (-not (Test-Path $src)) {
        throw "ld.lld.exe not found at $src"
    }
    Copy-Item $src $lldLink -Force
    Write-Host ('[{0}] Created lld-link.exe shim at {1}' -f (Get-Date -Format 'HH:mm:ss'), $lldLink)
}

# Build env. SKIP_WINRES tells build.rs to skip the icon + version metadata
# embed — that path requires Microsoft's rc.exe which isn't part of the xwin
# SDK download. The resulting .exe still runs identically.
$env:PATH = $mingwBin + ';' + (Get-Item Env:PATH).Value
$env:SKIP_WINRES = '1'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host ('[{0}] cargo xwin build --release --target x86_64-pc-windows-msvc --cross-compiler clang' -f (Get-Date -Format 'HH:mm:ss'))
$cargo = Join-Path (Get-Item Env:USERPROFILE).Value '.cargo\bin\cargo.exe'
& $cargo xwin build --release --target x86_64-pc-windows-msvc --cross-compiler clang
if ($LASTEXITCODE -ne 0) {
    throw ('cargo xwin build failed with exit code {0}' -f $LASTEXITCODE)
}

$exe = Join-Path $repoRoot 'target\x86_64-pc-windows-msvc\release\claude-code-usage-monitor.exe'
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Host ''
    Write-Host ('Build complete: {0} ({1} MB)' -f $exe, $size)
    Write-Host 'To smoke-test, pair with ccum-predictor.exe (any recent CI build) and launch with --diagnose.'
}
