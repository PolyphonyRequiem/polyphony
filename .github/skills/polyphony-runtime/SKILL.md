---
name: polyphony-runtime
description: >-
  Activate when the user asks to invoke the polyphony CLI, run an SDLC workflow,
  or work with a repo that has a `.polyphony-config/` directory. Verifies the
  binary is on PATH, installs it from GitHub Releases if missing, detects whether
  the cwd is a polyphony-onboarded repo, and documents the canonical invocation
  patterns (verbs + the `Invoke-PolyphonySdlc.ps1` launcher). For first-time
  config setup of a fresh repo, this skill defers to `polyphony-bootstrap`.

  Trigger phrases include:
  - 'run polyphony', 'invoke polyphony', 'install polyphony'
  - 'kick off an SDLC run', 'dispatch the apex', 'run the SDLC pipeline'
  - 'polyphony policy load', 'polyphony state next-ready', other verb names
  - any cwd contains `.polyphony-config/` and the user asks to do polyphony work

  Do NOT activate for:
  - First-time config authoring of a fresh repo (use `polyphony-bootstrap`).
  - Editing polyphony's own source code (use `polyphony-cli-developer`).
  - Editing workflow YAMLs (use `polyphony-workflow-author`).
---

# Polyphony Runtime

Operator-facing skill for **invoking** polyphony — verifying install, installing
if missing, and running the SDLC orchestration against an onboarded repo.

This skill is the runtime companion to `polyphony-bootstrap` (which covers
first-time config setup). If the cwd has no `.polyphony-config/` directory,
hand off to `polyphony-bootstrap` instead.

---

## Quick decision tree

```
Is `polyphony` on PATH?
├── No  → install (see § Install)
└── Yes → polyphony --version

Is `.polyphony-config/` in the repo root?
├── No  → repo not onboarded; activate `polyphony-bootstrap`
└── Yes → ready to run; see § Invocation
```

---

## Prerequisites

Polyphony has three runtime dependencies that must be on PATH before the
launcher will work:

| Dep | Purpose | Install |
|---|---|---|
| `git` | All branch/worktree operations | OS package manager |
| `pwsh` (PowerShell 7+) | Required by the launcher (cross-platform PowerShell, even on Linux/macOS) | https://github.com/PowerShell/PowerShell |
| `conductor` | Multi-agent workflow orchestrator that runs polyphony's YAML workflow suite | `pip install "git+https://github.com/microsoft/conductor.git@main"` |

`conductor` is **not** on PyPI yet; install from the GitHub source. Verify
with `conductor --version`.

`twig` (the polyphony write-side companion for ADO operations) is required
when the SDLC drives an ADO-tracked work item. Install separately from
[`PolyphonyRequiem/twig`](https://github.com/PolyphonyRequiem/twig); it
shares the same `~/.twig/bin/` location as polyphony.

---

## Install

Polyphony ships as **self-contained single-file binaries** via GitHub Releases.
No .NET runtime required on the operator's box.

**Source of truth:** `https://github.com/PolyphonyRequiem/polyphony/releases/latest`

The release workflow (`.github/workflows/release.yml`) builds for `win-x64`,
`linux-x64`, and `osx-arm64`, computes SHA256, and publishes a GitHub Release
with assets named `polyphony-{ver}-{rid}[.exe]` plus a sibling `.sha256`.

### One-liner (recommended)

For operators who just want polyphony installed and ready, including both
copilot skills as user-globals:

**Windows**
```powershell
iex (irm https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/install.ps1)
```

**Linux / macOS**
```bash
curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/install.sh | bash
```

The blocks below show the same install steps inline, for operators who
prefer to read what they're running before they run it, or who need to
adapt the install to a non-default location.

### Windows (PowerShell)

```powershell
# Resolve latest release tag (or pin to a specific vX.Y.Z)
$ver = (Invoke-RestMethod 'https://api.github.com/repos/PolyphonyRequiem/polyphony/releases/latest').tag_name -replace '^v',''
$rid = 'win-x64'
$asset = "polyphony-$ver-$rid.exe"
$base = "https://github.com/PolyphonyRequiem/polyphony/releases/download/v$ver"

# Download binary + checksum (asset name preserved on disk for shasum compat)
Invoke-WebRequest -Uri "$base/$asset" -OutFile $asset
Invoke-WebRequest -Uri "$base/$asset.sha256" -OutFile "$asset.sha256"

# Verify (sha256 file format: '<hash> *<filename>')
$expected = (Get-Content "$asset.sha256").Split(' ')[0]
$actual = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) { throw "SHA256 mismatch — refusing to install" }

# Install to ~/.twig/bin (canonical location)
$installDir = Join-Path $env:USERPROFILE '.twig\bin'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Move-Item $asset (Join-Path $installDir 'polyphony.exe') -Force
Remove-Item "$asset.sha256"

# Operator-facing launcher scripts.
#
# THE GAP: GitHub Releases up to and including v1.0.1 ship only the
# binary — the launcher (Invoke-PolyphonySdlc.ps1) lives in the polyphony
# repo's scripts/ directory and isn't bundled. PolyphonyRequiem/polyphony
# release.yml has been updated to include the launcher pair as release
# assets going forward; until the first release with that change ships,
# we fetch the launcher pair from `main` at HEAD. This drifts vs the
# binary version — acceptable trade-off until the next release tag.
#
# After the next launcher-bundled release ships, replace the $launcherBase
# line below with:
#   $launcherBase = "$base"   # release-pinned, matches binary version
$launcherBase = 'https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/scripts'
foreach ($name in 'Invoke-PolyphonySdlc.ps1', 'Resolve-GhIdentity.ps1', 'Migrate-ToBareRepo.ps1') {
    $dest = Join-Path $installDir $name
    Invoke-WebRequest -Uri "$launcherBase/$name" -OutFile $dest
    # Clear Mark-Of-The-Web so the script isn't blocked at first invocation
    # under default execution policy.
    Unblock-File -Path $dest
}

# Ensure PATH (idempotent — User scope persists across shells)
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable('Path', "$installDir;$userPath", 'User')
    $env:Path = "$installDir;$env:Path"  # current shell too
}

# Verify resolution lands on the just-installed binary
$resolved = (Get-Command polyphony -ErrorAction Stop).Source
$expectedPath = Join-Path $installDir 'polyphony.exe'
if ($resolved -ne $expectedPath) {
    throw "polyphony resolves to $resolved, not $expectedPath. Check PATH ordering with ``Get-Command polyphony -All``."
}
polyphony --version
& (Join-Path $installDir 'Invoke-PolyphonySdlc.ps1') -? | Select-Object -First 3
```

### Linux / macOS (bash)

```bash
# Resolve latest tag (no jq dependency — sed extraction)
ver=$(curl -fsSL https://api.github.com/repos/PolyphonyRequiem/polyphony/releases/latest \
      | sed -nE 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v?([^"]+)".*/\1/p' | head -n1)

# Pick RID for the host
case "$(uname -s)/$(uname -m)" in
  Linux/x86_64)  rid=linux-x64 ;;
  Darwin/arm64)  rid=osx-arm64 ;;
  *) echo "Unsupported host: $(uname -s)/$(uname -m)"; exit 1 ;;
esac

asset="polyphony-${ver}-${rid}"
base="https://github.com/PolyphonyRequiem/polyphony/releases/download/v${ver}"

# Download with original asset filenames (shasum -c needs the recorded name)
curl -fsSL -o "$asset" "${base}/${asset}"
curl -fsSL -o "${asset}.sha256" "${base}/${asset}.sha256"

# Verify (the sha256 file references $asset by name)
shasum -a 256 -c "${asset}.sha256"

# Install
chmod +x "$asset"
mkdir -p ~/.twig/bin
mv "$asset" ~/.twig/bin/polyphony
rm "${asset}.sha256"

# Operator-facing launcher scripts (cross-platform PowerShell — requires
# pwsh on the host). See the Windows section's GAP comment: until the
# next release ships with launcher assets bundled, fetch from main HEAD.
launcher_base='https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/scripts'
for s in Invoke-PolyphonySdlc.ps1 Resolve-GhIdentity.ps1 Migrate-ToBareRepo.ps1; do
    curl -fsSL -o "$HOME/.twig/bin/$s" "$launcher_base/$s"
done

# Add to PATH if needed (zsh shown; adapt to shell)
if [[ ":$PATH:" != *":$HOME/.twig/bin:"* ]]; then
    echo 'export PATH="$HOME/.twig/bin:$PATH"' >> ~/.zshrc
    export PATH="$HOME/.twig/bin:$PATH"
fi

# Verify
resolved=$(command -v polyphony)
if [[ "$resolved" != "$HOME/.twig/bin/polyphony" ]]; then
    echo "ERROR: polyphony resolves to $resolved, not ~/.twig/bin/polyphony"; exit 1
fi
polyphony --version
command -v pwsh >/dev/null 2>&1 || { echo "WARN: pwsh not found — install PowerShell to run the launcher"; }
```

### Build from source (rare — only when you need an unreleased commit)

```powershell
git clone https://github.com/PolyphonyRequiem/polyphony.git
cd polyphony
pwsh ./publish-local.ps1
# Installs to ~/.twig/bin and verifies the timestamp matches the just-built artifact.
```

Requires .NET 11 SDK (per `Directory.Build.props`). `publish-local.ps1` handles
the publish + install + staleness verification.

---

## Register the workflow suite with conductor

The launcher invokes `apex-driver@polyphony` — a workflow ID conductor only
resolves if polyphony is registered as a workflow source. **One-time
per-machine setup** after installing conductor:

```bash
# Point conductor at the polyphony github repo (registry source = main HEAD)
conductor registry add polyphony PolyphonyRequiem/polyphony

# Verify registration
conductor registry list polyphony
# Expected: a list of workflows including apex-driver, plan-level,
# implement-merge-group, feature-pr, ado-pr, github-pr, ...
```

Without registration, `conductor run apex-driver@polyphony` fails fast with
`workflow not found` and the launcher's preflight surfaces the same error
before any worktree is created. Registration is **per-machine, not
per-repo** — register once, reuse across every onboarded repo on this box.

To pull updated workflows after the upstream repo changes:

```bash
conductor registry update polyphony
```

---

## Invocation

### The launcher (most common entry point)

`Invoke-PolyphonySdlc.ps1` dispatches a full SDLC run for one apex work
item. **The launcher derives repo context from the current working
directory** — there is no `-RepoRoot` flag.

After installing per the section above, the launcher lives at
`~/.twig/bin/Invoke-PolyphonySdlc.ps1` (Windows: `$env:USERPROFILE\.twig\bin\`).
On Linux/macOS it requires `pwsh` on PATH.

```powershell
# Run from the target repo's main worktree
cd <target-repo-main-worktree>     # e.g. C:\Users\dangreen\projects\cloudvault-service-api\main
& "$env:USERPROFILE\.twig\bin\Invoke-PolyphonySdlc.ps1" `
    -ApexId <work-item-id> `
    -Intent new                    # or resume / replan
    # -Platform ado                # optional; auto-detected from `git remote get-url origin`
```

```bash
# Linux / macOS — pwsh required
cd <target-repo-main-worktree>
pwsh ~/.twig/bin/Invoke-PolyphonySdlc.ps1 -ApexId <work-item-id> -Intent new
```

Key parameters (run `Get-Help ~/.twig/bin/Invoke-PolyphonySdlc.ps1 -Full` for the rest):

- `-ApexId` (mandatory) — work item id (the root of the run).
- `-Intent` — `new` (default) | `resume` | `replan`.
- `-Platform` — `ado` or `github`. **PR platform**, not tracker. Default: auto-detected from `git remote get-url origin`.
- `-WorktreeRoot` — override the auto-derived per-apex worktree path. Default: `~/projects/<repo-name>-runs/apex-<ApexId>/feature-<ApexId>/`.
- `-NoDetach` — keep conductor in the foreground (debug). Default: detached + dashboard at `127.0.0.1:<port>`.
- `-DryRun` — print the resolved command + JSON envelope; create nothing.
- `-PolicyPath` — alternate policy YAML (e.g. `.polyphony-config/policy-fasttrack.yaml`).

The launcher refuses to dispatch if:
- The target work item is in a terminal state (override with `-SkipStateCheck` or use `-Intent resume`).
- The repo is not on the bare-repo + worktree layout (override with `-SkipLayoutCheck` — discouraged).
- (Per AB#3085) The derived `WorktreeRoot` resolves inside the main worktree.

### Common verbs

```powershell
# Validate config — this repo's .polyphony-config/* parses cleanly:
polyphony policy load                       # parse policy.yaml + return effective snapshot
polyphony policy validate                   # schema-validate policy.yaml WITHOUT applying defaults
polyphony validate-config                   # validate process-config.yaml against all rules
polyphony health                            # CLI/runtime health check

# State machine — what's the next-ready requirement under a work item?
polyphony state next-ready --work-item <id>
polyphony state preflight --work-item <id>      # full root-SDLC preflight
polyphony state preflight-lite --work-item <id> # planning sub-workflow (3 checks)

# PR lifecycle (ADO)
polyphony pr poll-status-ado --organization <org> --project <proj> --repository-id <repo> --pr-number <id>
polyphony pr post-comment-ado --organization <org> --project <proj> --repository-id <repo> --pr-number <id> --comment <text>
polyphony pr vote-ado         --organization <org> --project <proj> --repository-id <repo> --pr-number <id> --vote approve

# Worktree lifecycle (per AB#3085 model)
polyphony worktree init-apex --apex-id <id>     # create per-apex feature worktree
polyphony worktree list
polyphony worktree assert-clean
polyphony worktree gc
```

Run `polyphony --help` (or `polyphony <verb> --help`) for the full catalogue.
Verbs are routing-style: all parameters as flags (`--work-item 3085`, not
positional).

---

## Pre-flight checks

Before invoking the launcher, verify:

1. **Binary present and current:**
   ```powershell
   (Get-Command polyphony).Source
   polyphony --version
   ```
2. **Target repo onboarded:**
   ```powershell
   Test-Path '.polyphony-config/policy.yaml'
   polyphony policy load     # exits 0; "used_defaults":false confirms config consumed
   ```
   If the file is missing → run `polyphony-bootstrap` walkthrough first.
3. **Bare-repo + worktree layout:**
   ```powershell
   git --git-dir <bare-root>/.git rev-parse --is-bare-repository    # → true
   git worktree list                                                # → main + per-apex worktrees
   ```
   On Daniel's box, `safe.bareRepository=explicit` is set — see the `polyphony-branch-model` skill for layout invariants and the `--git-dir` requirement.
4. **Twig configured for the workspace** (the launcher uses twig for ADO calls):
   ```powershell
   twig list-workspaces
   ```
5. **ADO auth** (azcli token or PAT):
   ```powershell
   az account show          # azcli mode
   $env:AZURE_DEVOPS_PAT    # PAT mode
   ```

---

## Common pitfalls

- **Stale binary**: `(Get-Item ~/.twig/bin/polyphony.exe).LastWriteTime` predates a recent release → reinstall (or `publish-local.ps1` if you built locally). Use `Get-Command polyphony -All` to spot competing copies on PATH.
- **Force-push lockout** between operations: `gh auth switch --user PolyphonyRequiem` (GitHub) or re-`az login` (ADO) — auth contexts slip back between commands.
- **`safe.bareRepository=explicit`** is set on Daniel's box: bare repos must be addressed with `--git-dir=<path>` or operations must run from inside a worktree, not the bare root.
- **`$PID` is read-only** in PowerShell — never use `$pid` as a loop variable.
- **`-Platform`** controls PR platform (where to open PRs), not tracker. Tracker (ADO work items via twig) is configured separately via `.twig/config`.

---

## When NOT to use this skill

- **First-time config setup** for an unconfigured repo → `polyphony-bootstrap`.
- **Editing polyphony's own source** (verbs, agents, workflows) → `polyphony-cli-developer`, `polyphony-workflow-author`.
- **Designing or debugging workflow YAML routes** → `conductor-mechanics`, `conductor-design`.
- **Diagnosing harness/test failures** → `polyphony-harness`.

---

## Related skills (in this repo)

- `polyphony-bootstrap` — first-time config authoring for a fresh repo.
- `polyphony-cli-developer` — adding/editing CLI verbs in `src/Polyphony/Commands/`.
- `polyphony-workflow-author` — editing workflow YAMLs in `.conductor/registry/workflows/`.
- `polyphony-branch-model` — bare-repo + worktree invariants.
- `polyphony-sdlc` — vocabulary and sub-workflow library.
- `polyphony-harness` — path-coverage harness for testing workflow changes.
- `polyphony-actionable` — the actionable.yaml workflow specifically.

---

## For target repos (cloudvault, etc.)

This skill lives in the polyphony repo but is intended to be **discoverable from
any onboarded target repo**. Two distribution paths:

1. **User-global** (preferred): symlink `~/.copilot/skills/polyphony-runtime/`
   to this directory in the polyphony clone. The skill auto-loads in any cwd.
   ```powershell
   New-Item -ItemType SymbolicLink `
       -Path "$env:USERPROFILE\.copilot\skills\polyphony-runtime" `
       -Target "<polyphony-repo>\.github\skills\polyphony-runtime"
   ```
   ```bash
   ln -s <polyphony-repo>/.github/skills/polyphony-runtime ~/.copilot/skills/polyphony-runtime
   ```

2. **Stub in target repo**: bootstrap PRs (like cloudvault's) drop a small stub
   at `<target-repo>/.github/skills/polyphony-runtime/SKILL.md` that points
   here and carries inline install + invocation. The stub uses
   `name: polyphony-runtime-stub` to avoid colliding with the user-global skill.
   The stub template lives at `templates/target-repo-stub.md`.
