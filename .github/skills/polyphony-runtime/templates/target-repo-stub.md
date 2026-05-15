---
name: polyphony-runtime-stub
description: >-
  Activate when the user asks to invoke polyphony, run an SDLC workflow, or
  work with this repo's `.polyphony-config/` directory. This is a **stub**
  for target repos that defers to the upstream `polyphony-runtime` skill in
  the polyphony repo if available; otherwise carries inline install commands
  so a fresh operator can bootstrap without needing the polyphony repo cloned
  first. Front-matter `name` differs from the upstream (`polyphony-runtime`)
  to avoid collision when both are discoverable.
---

# Polyphony Runtime — stub

This repo is onboarded to polyphony (see `.polyphony-config/`). The full
runtime skill lives upstream:

**https://github.com/PolyphonyRequiem/polyphony/blob/main/.github/skills/polyphony-runtime/SKILL.md**

If `polyphony-runtime` is loaded from `~/.copilot/skills/polyphony-runtime/`,
prefer it over this stub. Install user-global so it auto-loads in every repo:

```powershell
git clone https://github.com/PolyphonyRequiem/polyphony.git <somewhere>
New-Item -ItemType SymbolicLink `
    -Path "$env:USERPROFILE\.copilot\skills\polyphony-runtime" `
    -Target "<somewhere>\polyphony\.github\skills\polyphony-runtime"
```

```bash
git clone https://github.com/PolyphonyRequiem/polyphony.git <somewhere>
ln -s <somewhere>/polyphony/.github/skills/polyphony-runtime ~/.copilot/skills/polyphony-runtime
```

---

## Inline install (no upstream skill needed)

Polyphony ships as self-contained single-file binaries via GitHub Releases.

### Windows (PowerShell)

```powershell
$ver = (Invoke-RestMethod 'https://api.github.com/repos/PolyphonyRequiem/polyphony/releases/latest').tag_name -replace '^v',''
$rid = 'win-x64'
$asset = "polyphony-$ver-$rid.exe"
$base = "https://github.com/PolyphonyRequiem/polyphony/releases/download/v$ver"
Invoke-WebRequest -Uri "$base/$asset" -OutFile $asset
Invoke-WebRequest -Uri "$base/$asset.sha256" -OutFile "$asset.sha256"
$expected = (Get-Content "$asset.sha256").Split(' ')[0]
$actual = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) { throw "SHA256 mismatch" }
$installDir = Join-Path $env:USERPROFILE '.twig\bin'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Move-Item $asset (Join-Path $installDir 'polyphony.exe') -Force
Remove-Item "$asset.sha256"
$env:Path = "$installDir;$env:Path"
& "$installDir\polyphony.exe" --version
```

### Linux / macOS (bash)

```bash
ver=$(curl -fsSL https://api.github.com/repos/PolyphonyRequiem/polyphony/releases/latest \
      | sed -nE 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v?([^"]+)".*/\1/p' | head -n1)
case "$(uname -s)/$(uname -m)" in
  Linux/x86_64)  rid=linux-x64 ;;
  Darwin/arm64)  rid=osx-arm64 ;;
  *) echo "Unsupported host"; exit 1 ;;
esac
asset="polyphony-${ver}-${rid}"
base="https://github.com/PolyphonyRequiem/polyphony/releases/download/v${ver}"
curl -fsSL -o "$asset" "${base}/${asset}"
curl -fsSL -o "${asset}.sha256" "${base}/${asset}.sha256"
shasum -a 256 -c "${asset}.sha256"
chmod +x "$asset"
mkdir -p ~/.twig/bin
mv "$asset" ~/.twig/bin/polyphony
rm "${asset}.sha256"
~/.twig/bin/polyphony --version
```

Make sure `~/.twig/bin` (or `$env:USERPROFILE\.twig\bin` on Windows) is on PATH.

---

## Inline invocation

```powershell
# Validate this repo's .polyphony-config/ parses cleanly:
polyphony policy load
polyphony policy validate
polyphony validate-config
polyphony health

# Kick off an SDLC run for a work item.
# The launcher derives repo context from cwd — run from the main worktree.
cd <this-repo-main-worktree>
& <polyphony-repo>\scripts\Invoke-PolyphonySdlc.ps1 `
    -ApexId <work-item-id> `
    -Intent new
    # -Platform ado          # optional; auto-detected from origin remote
```

For verbs, pre-flight checks, common pitfalls, and the worktree lifecycle,
see the upstream skill linked above.
