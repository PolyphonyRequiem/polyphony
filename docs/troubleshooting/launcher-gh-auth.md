# Troubleshooting: Launcher GitHub Authentication

The polyphony SDLC launcher runs a **gh-identity probe** at startup to
capture and validate the active GitHub CLI identity before any workflow
begins. This page explains how the probe works, what its diagnostic output
means, and how to fix each failure mode.

## When the probe runs

The probe runs when **both** conditions are true:

1. `-Platform github` is passed to `Invoke-PolyphonySdlc.ps1`.
2. `-DryRun` is **not** set.

When `-Platform ado` is used (Azure DevOps) or when `-DryRun` is active,
the probe is skipped entirely — no `gh` CLI is required.

## What the probe does

On launch, `Resolve-GhIdentity` captures whichever `gh` user is active at
that moment, validates the captured token against the GitHub API, and
returns the resolved identity. The launcher then **pins** the validated
token into `GH_TOKEN` and `GH_HOST` environment variables for the entire
conductor + polyphony subprocess tree.

### Diagnostic line

On success the launcher emits a cyan diagnostic line:

```
[polyphony-sdlc] gh identity pinned: user='X' source=Y token_len=N
```

| Field       | Meaning |
|-------------|---------|
| `user`      | The GitHub login the captured token authenticates as (resolved via `gh api user --jq '.login'`). |
| `source`    | Where the token came from. `gh-keyring` means it was freshly captured from the gh CLI auth store; `env` means `GH_TOKEN` was already set in the environment when the launcher started. |
| `token_len` | Length of the captured token. Useful for confirming the right token shape without exposing the secret. |

## Failure modes the probe prevents

### 1. Competing-worker auth slippage

Another agent in a sibling worktree can call `gh auth switch` mid-run,
flipping the active `gh` user out from under the running workflow. Without
pinning, every subsequent `gh` call inside conductor re-resolves the active
user — and if the new active user has no token cached for `github.com`, `gh`
falls through to the DPAPI keyring in a non-TTY context and hangs the
per-attempt timeout (60 s × 3 retries per call).

By capturing and pinning the token once at startup, the subprocess tree is
immune to this drift.

### 2. Stale or wrong-scope token

By validating the token once at launch, an expired or insufficient-scope
token surfaces as a clear fail-fast at startup with the exact remediation
command — instead of appearing as 60 s `gh` hangs on every PR-poll cycle
hours later.

## Symptoms and remediation

Each error below is quoted verbatim from `scripts/Resolve-GhIdentity.ps1`.

### `gh CLI not found on PATH`

**Trigger:** The `gh` executable is not installed or not on `PATH`.

**Error message:**

```
gh CLI not found on PATH.

The polyphony SDLC launcher needs the gh CLI to authenticate against GitHub.
Either install it (https://cli.github.com/) or relaunch with -Platform ado
if this run targets Azure DevOps.
```

**Remediation:**

```sh
# Install the gh CLI
# https://cli.github.com/

# Or, if this run targets Azure DevOps instead of GitHub:
Invoke-PolyphonySdlc.ps1 -Platform ado …
```

---

### `gh has no token cached for github.com`

**Trigger:** `gh auth token --hostname github.com` returned an empty result
— no token is cached for `github.com`. This commonly happens when a
corporate EMU account is active instead of the `PolyphonyRequiem` account.

**Error message:**

```
gh has no token cached for github.com.

Run: gh auth login --hostname github.com
```

If `gh auth token` exits non-zero instead, the error includes the exit code
and stderr:

```
gh auth token --hostname github.com failed (exit <code>).

Stderr: <stderr output>

Run: gh auth login --hostname github.com
```

**Remediation:**

```sh
# If the wrong account is active (e.g. an EMU account), switch:
gh auth switch --user PolyphonyRequiem

# If no token exists at all, log in:
gh auth login --hostname github.com
```

---

### `gh token validation failed`

**Trigger:** The captured token was rejected by the GitHub API (HTTP 401 or
403). Common causes: token expired, insufficient scopes (need `repo` for PR
operations), or wrong account is active.

**Error message:**

```
gh token validation failed (exit <code>, token source: <source>, token length: <len>).

The captured token was rejected by the GitHub API. Common causes:
  - token expired
  - insufficient scopes (need 'repo' for PR operations)
  - wrong account is active

Stderr: <stderr output>

Run: gh auth status
Then: gh auth refresh --hostname github.com -s repo
```

**Remediation:**

```sh
# Check which accounts are authenticated and their token status:
gh auth status

# Refresh the token with the required scope:
gh auth refresh --hostname github.com -s repo
```

---

### `gh auth token timed out`

**Trigger:** The `gh auth token` or `gh api user` call did not complete
within the per-call timeout (default 30 s). This usually indicates `gh` is
blocked waiting for interactive input in a non-TTY context, or a network
issue.

**Error message:**

```
gh auth token --hostname github.com timed out after 30s
```

Or, for the validation call:

```
gh api user --jq .login timed out after 30s
```

**Remediation:**

```sh
# Verify gh is working interactively:
gh auth status

# If the token is missing, log in:
gh auth login --hostname github.com

# If the network is unreachable, check connectivity to github.com
```
