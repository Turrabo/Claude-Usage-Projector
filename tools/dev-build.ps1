# Local dev build for the Rust host binary.
#
# Why this script exists:
#   We build with Rust's x86_64-pc-windows-gnullvm target + LLVM-MinGW, because
#   the MSVC C++ Build Tools cannot be installed on this machine. By default
#   LLVM-MinGW dynamically links libunwind, so the resulting .exe needs
#   libunwind.dll alongside it at runtime. There is no clean cargo/RUSTFLAGS
#   incantation that overrides this — the Rust gnullvm target spec adds
#   `-lunwind` after our link-args, and the linker picks up the dynamic import
#   library (libunwind.dll.a) ahead of the static one (libunwind.a).
#
# What this script does:
#   1. Renames LLVM-MinGW's libunwind.dll.a temporarily so the linker can only
#      find libunwind.a, forcing static linking.
#   2. Runs `cargo build --release`.
#   3. Restores libunwind.dll.a unconditionally (try/finally).
#
# Result: a single self-contained ccum-host.exe whose only DLL imports are
# Windows system libraries.

$ErrorActionPreference = 'Stop'

# Locate LLVM-MinGW. Override LLVM_MINGW_ROOT env var if your install differs.
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
$importLib = Join-Path $mingwRoot 'x86_64-w64-mingw32\lib\libunwind.dll.a'
$importLibBackup = $importLib + '.devbuild-bak'
$repoRoot = Split-Path -Parent $PSScriptRoot

$renamed = $false
if (Test-Path $importLib) {
    Move-Item $importLib $importLibBackup -Force
    $renamed = $true
    Write-Host ('[{0}] Temporarily moved libunwind.dll.a aside.' -f (Get-Date -Format 'HH:mm:ss'))
} else {
    Write-Warning ('libunwind.dll.a not found at {0} — proceeding without rename.' -f $importLib)
}

try {
    $newPath = $mingwBin + ';' + (Get-Item Env:PATH).Value
    Set-Item Env:PATH $newPath
    Set-Location $repoRoot
    $cargo = Join-Path (Get-Item Env:USERPROFILE).Value '.cargo\bin\cargo.exe'
    Write-Host ('[{0}] cargo build --release' -f (Get-Date -Format 'HH:mm:ss'))
    & $cargo build --release
    if ($LASTEXITCODE -ne 0) {
        throw ('cargo build failed with exit code {0}' -f $LASTEXITCODE)
    }
} finally {
    if ($renamed -and (Test-Path $importLibBackup)) {
        Move-Item $importLibBackup $importLib -Force
        Write-Host ('[{0}] Restored libunwind.dll.a.' -f (Get-Date -Format 'HH:mm:ss'))
    }
}

$exe = Join-Path $repoRoot 'target\release\claude-code-usage-monitor.exe'
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Host ''
    Write-Host ('Build complete: {0} ({1} MB)' -f $exe, $size)
}
