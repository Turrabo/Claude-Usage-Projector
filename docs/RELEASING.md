# Releasing

Tagged releases are how this fork distributes itself. Push a `v*` tag, GitHub Actions builds both binaries, bundles them into a single zip, and creates a Release page with the zip attached and notes auto-generated from the commit log since the previous tag.

There is no separate signing step — the binaries are unsigned. First-launch SmartScreen requires the standard "More info → Run anyway" click-through. See [`DECISIONS.md`](../DECISIONS.md) for the recorded reasoning.

## Cutting a release

There's no formal cadence. Tag when you've reached a clean milestone you want to be able to pull onto another machine without the `/verify-build` ritual.

```powershell
# From repo root, with `main` checked out and clean
git tag -a v0.5.0 -m "v0.5.0 — JSONL persistence + CSM migration"
git push origin v0.5.0
```

That's it. `release.yml` fires on the tag push, runs the full build + test pipeline on `windows-latest`, and creates the GitHub Release. Total wall-time is roughly 4–6 minutes.

## Versioning

The tag is the canonical version. `Cargo.toml` and `predictor/Program.cs` carry their own version strings but neither is read at release-build time, so they can drift without breaking the release. Update them when convenient — typically alongside the tag — to keep the diagnose log self-describing.

## Release names

Use plain semantic-ish tags: `v0.5.0`, `v0.5.1`, `v1.0.0`. The leading `v` is required (the workflow's trigger regex insists). No `-rc` or `-beta` suffixes today; if you want a pre-release, mark it that way in the GitHub Release UI after the action finishes.

## What ends up in the zip

- `claude-code-usage-monitor.exe` — the Rust host widget
- `ccum-predictor.exe` — the C# predictor sidecar (self-contained single-file)
- `README.txt` — extracted from `release.yml`, tells a new user how to launch

The two .exes must stay co-located when launched (the host's `locate_predictor()` looks in the same directory). The README spells this out.

## Re-running a release

If the workflow fails mid-flight, you can re-run from the Actions UI. Or:

```powershell
gh workflow run release.yml --ref main -f tag=v0.5.0
```

The workflow's `workflow_dispatch` trigger accepts the tag name as an input parameter.

## Manual local release (rare — only if CI is down)

```powershell
# 1. Build host
cargo build --release

# 2. Build predictor
cd predictor
dotnet publish Predictor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
cd ..

# 3. Stage and zip
New-Item -ItemType Directory -Force -Path release-stage
Copy-Item target/release/claude-code-usage-monitor.exe release-stage/
Copy-Item predictor/bin/Release/net9.0/win-x64/publish/ccum-predictor.exe release-stage/
Compress-Archive -Path "release-stage/*" -DestinationPath "ccum-windows-x64-v0.5.0.zip" -Force

# 4. Upload to a Release manually via the GitHub UI or `gh release create`.
```

Bear in mind: a local build on a machine without MSVC (e.g. the gnullvm dev path) produces a binary that compiles but doesn't launch correctly — see [`DECISIONS.md`](../DECISIONS.md) ADR-005. So local-only releases only work on a machine with the standard MSVC toolchain.
