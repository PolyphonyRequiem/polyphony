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

## Prerequisites

Before installing polyphony, the operator's box needs:

| Dep | Install |
|---|---|
| `git` | OS package manager |
| `pwsh` (PowerShell 7+) | https://github.com/PowerShell/PowerShell |
| `conductor` | `pip install "git+https://github.com/microsoft/conductor.git@main"` |

Verify with `git --version`, `pwsh --version`, `conductor --version`.

`twig` (ADO writes) is required for ADO-tracked SDLC; install separately
from [`PolyphonyRequiem/twig`](https://github.com/PolyphonyRequiem/twig).

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

# Launcher scripts (until next polyphony release bundles them as assets,
# fetch from main HEAD).
$launcherBase = 'https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/scripts'
foreach ($name in 'Invoke-PolyphonySdlc.ps1', 'Resolve-GhIdentity.ps1', 'Migrate-ToBareRepo.ps1') {
    $dest = Join-Path $installDir $name
    Invoke-WebRequest -Uri "$launcherBase/$name" -OutFile $dest
    Unblock-File -Path $dest
}

$env:Path = "$installDir;$env:Path"
& "$installDir\polyphony.exe" --version

# Install both polyphony skills as user-globals so future copilot
# sessions in any repo auto-discover them (no per-repo stub required).
$skillsDir = Join-Path $env:USERPROFILE '.copilot\skills'
New-Item -ItemType Directory -Force -Path $skillsDir | Out-Null
$skillBase = 'https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/.github/skills'
foreach ($skill in 'polyphony-runtime', 'polyphony-bootstrap') {
    $d = Join-Path $skillsDir $skill
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    Invoke-WebRequest -Uri "$skillBase/$skill/SKILL.md" -OutFile (Join-Path $d 'SKILL.md')
}
$tmplDir = Join-Path $skillsDir 'polyphony-runtime\templates'
New-Item -ItemType Directory -Force -Path $tmplDir | Out-Null
Invoke-WebRequest -Uri "$skillBase/polyphony-runtime/templates/target-repo-stub.md" -OutFile (Join-Path $tmplDir 'target-repo-stub.md')
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

# Launcher scripts (cross-platform PowerShell — requires pwsh).
launcher_base='https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/scripts'
for s in Invoke-PolyphonySdlc.ps1 Resolve-GhIdentity.ps1 Migrate-ToBareRepo.ps1; do
    curl -fsSL -o "$HOME/.twig/bin/$s" "$launcher_base/$s"
done

~/.twig/bin/polyphony --version

# Install both polyphony skills as user-globals so future copilot
# sessions in any repo auto-discover them (no per-repo stub required).
skills_dir="$HOME/.copilot/skills"
mkdir -p "$skills_dir"
skill_base='https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/.github/skills'
for skill in polyphony-runtime polyphony-bootstrap; do
    mkdir -p "$skills_dir/$skill"
    curl -fsSL -o "$skills_dir/$skill/SKILL.md" "$skill_base/$skill/SKILL.md"
done
mkdir -p "$skills_dir/polyphony-runtime/templates"
curl -fsSL -o "$skills_dir/polyphony-runtime/templates/target-repo-stub.md" \
    "$skill_base/polyphony-runtime/templates/target-repo-stub.md"
```

Make sure `~/.twig/bin` (or `$env:USERPROFILE\.twig\bin` on Windows) is on PATH.

---

## Register polyphony with conductor (one-time per machine)

The launcher invokes `apex-driver@polyphony` — conductor only resolves
that workflow ID if polyphony is registered as a workflow source:

```bash
conductor registry add polyphony PolyphonyRequiem/polyphony
conductor registry list polyphony   # verify
```

Without this, `conductor run apex-driver@polyphony` fails with `workflow
not found`. Register once per machine, reuse across every onboarded repo.
To pull updated workflows: `conductor registry update polyphony`.

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
& "$env:USERPROFILE\.twig\bin\Invoke-PolyphonySdlc.ps1" `
    -ApexId <work-item-id> `
    -Intent new
    # -Platform ado          # optional; auto-detected from origin remote
```

```bash
# Linux / macOS — pwsh required
cd <this-repo-main-worktree>
pwsh ~/.twig/bin/Invoke-PolyphonySdlc.ps1 -ApexId <work-item-id> -Intent new
```

For verbs, pre-flight checks, common pitfalls, and the worktree lifecycle,
see the upstream skill linked above.
