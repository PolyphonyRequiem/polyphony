---
name: polyphony-dogfood-recovery
description: >-
  Activate when a polyphony dogfood apex run is stuck mid-flight and the
  operator wants to abort, untangle the partial state spread across the local
  worktree, the remote, ADO PRs, and twig's work-item state, and reset
  everything to a clean re-entry-ready point. Covers killing the conductor,
  the 5-axis state inventory, the strict cleanup ordering that avoids stuck
  refs, the per-type re-entry state table, common gotchas, and the
  post-cleanup smoke-test before re-launch.

  Trigger phrases include:
  - 'clean up the apex run'
  - 'abort and reset apex'
  - 'restart from clean state'
  - 'reset work item state for re-run'
  - 'polyphony dogfood is stuck'
  - 'untangle partial workflow state'
  - 'the apex run died mid-way and I want to retry'

  Do NOT activate for:
  - Pushing a work item forward toward completion. This skill is RESET only,
    never FORWARD. Use the regular `polyphony` verbs for normal progression.
  - Individual work-item bugs unrelated to a run state — use regular
    `polyphony` verbs and `twig` operations directly.
  - Workflow-author-level fixes to prevent the bug recurring — defer to
    `polyphony-workflow-author` and `polyphony-cli-developer`.
user-invokable: false
---

# Polyphony Dogfood Recovery

Runbook for aborting a wedged polyphony apex run and resetting state across
all four surfaces (local git, remote git, ADO PRs, twig/ADO work-item state)
so the apex can be re-launched cleanly via `Invoke-PolyphonySdlc.ps1`.

This skill is **destructive in scope**. Every operation here either kills a
process, abandons a PR, deletes a branch, or rewinds a work-item state. Use
it when the run is already wedged — not as a normal SDLC tool.

Companion skills:

- **`polyphony-runtime`** — canonical invocation patterns (`polyphony` verbs
  + the `Invoke-PolyphonySdlc.ps1` launcher). The re-launch step at the
  bottom of this skill defers to it.
- **`polyphony-workflow-author`** — when recovery surfaces a workflow-level
  bug worth fixing (e.g. the re-dispatch loop in incident AB#62286666).
- **`polyphony-cli-developer`** — when the bug lives in a `polyphony` CLI verb.
- **`polyphony-bootstrap`** — only for first-time onboarding of a fresh repo;
  irrelevant once `.polyphony-config/` exists.

---

## 1 · When NOT to use this skill

- **Forward-only motion.** Do not use this runbook to "transition a work item
  toward completion." This is RESET only. Reset target is always a state
  *before* where the workflow got stuck, never after.
- **Individual work-item bugs.** If a single item is in a wrong state but no
  run is wedged, fix it directly with `polyphony validate` + `twig state`.
- **Workflow-author fixes.** If the root cause is a workflow YAML bug,
  recover here, then switch to `polyphony-workflow-author` /
  `polyphony-cli-developer` to author the fix.
- **First-time bootstrap.** If `.polyphony-config/` does not exist yet, you
  want `polyphony-bootstrap`, not this skill.

---

## 2 · Halt the run

Three layers, applied in order. Stop at the first one that succeeds.

### 2.1 · Try the conductor's `/api/kill` endpoint first

The conductor's HTTP control plane exposes a clean shutdown:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:<PINNED_PORT>/api/kill" -Method POST
```

The pinned port lives in the launcher's stdout (look for
`Listening on http://127.0.0.1:<port>`) and in
`~/.polyphony/run/<apex>/port` if the launcher wrote it.

### 2.2 · Fall back to `Stop-Process` by PID

```powershell
Get-Process conductor* | Select-Object Id, ProcessName, StartTime
Stop-Process -Id <conductor_pid> -Force
```

Never use `Stop-Process -Name conductor*` or `taskkill /IM conductor` —
those will also nuke any unrelated conductor session.

### 2.3 · Verify the port is released

```powershell
Get-NetTCPConnection -State Listen | Where-Object LocalPort -eq <port>
# expect: no output
```

### 2.4 · Sweep detached children

Long-running agent invocations spawn detached `python` / `uv` / `node` /
`dotnet` helpers. Some survive a conductor kill. Find them by start time
and `Stop-Process -Id` per PID:

```powershell
Get-Process python*, uv*, node*, dotnet* `
  | Where-Object StartTime -gt (Get-Date).AddHours(-2) `
  | Select-Object Id, ProcessName, StartTime, Path
```

---

## 3 · Inventory the state (5-axis check)

Run all of these — the cleanup ordering below depends on knowing exactly
what exists where. Substitute the apex work-item id for `<APEX_ID>` and
your project/repo for `<PROJECT>` / `<REPO>`.

```powershell
# 3.1 — Worktree HEAD branch
git branch --show-current

# 3.2 — Local branches touching the apex
git branch | Where-Object { $_ -match '<APEX_ID>' }

# 3.3 — Remote branches touching the apex
git branch -r | Where-Object { $_ -match '<APEX_ID>' }

# 3.4 — Active ADO PRs touching the apex (source OR target)
az repos pr list `
  --org https://dev.azure.com/microsoft/ `
  --project <PROJECT> --repository <REPO> --status active `
  --query "[?contains(sourceRefName, '<APEX_ID>') || contains(targetRefName, '<APEX_ID>')]" `
  -o json

# 3.5 — Completed PRs (awareness only — do not touch)
az repos pr list `
  --org https://dev.azure.com/microsoft/ `
  --project <PROJECT> --repository <REPO> --status completed `
  --query "[?contains(sourceRefName, '<APEX_ID>') || contains(targetRefName, '<APEX_ID>')]" `
  -o json

# 3.6 — Work-item state + tags (tags drive some routing)
twig show <APEX_ID> --output json `
  | ConvertFrom-Json `
  | Select-Object id, state, tags
```

Write down the active-PR ids, the local `mg/` + `impl/` branch list, and the
remote `mg/` + `impl/` branch list before doing anything destructive. The
cleanup ordering below assumes this inventory is fresh.

---

## 4 · Cleanup ordering (critical — wrong order leaves stuck refs)

Order matters. Reversing 4.1↔4.2 or 4.4↔4.5 leaves branches that ADO still
considers `sourceRefName` of an active PR, which then blocks deletion.
Reversing 4.7↔4.8 leaves twig's local cache out of step with ADO.

### 4.1 — Park the worktree on a survivor branch

```powershell
git checkout feature/<APEX_ID>
```

You are about to delete `mg/<APEX_ID>_*` and `impl/<APEX_ID>-*`. The
worktree must not be on any of them. `feature/<APEX_ID>` is the apex
integration trunk and survives the reset (see the
**`polyphony-branch-model`** skill for the canonical branch tree).

### 4.2 — Abandon every active apex-touching ADO PR

For each `<PR_ID>` from § 3.4:

```powershell
az repos pr update --id <PR_ID> `
  --org https://dev.azure.com/microsoft/ `
  --status abandoned
```

> ADO PRs can only be **abandoned**, not **deleted**. Abandoned PRs remain
> in the repo's PR list forever; that is fine and intentional.

### 4.3 — Switch the GitHub auth user to the bot

Every `git push` (including `--delete`) needs the bot identity, not the
operator's interactive identity. The shell falls back to the human user
between operations — re-run this before *every* push session:

```powershell
gh auth switch --user PolyphonyRequiem
```

### 4.4 — Delete remote branches (one at a time)

Do them one branch per command so each push's output is visible. Trying to
batch them masks per-ref failures (e.g. a PR you forgot to abandon in § 4.2):

```powershell
git push origin --delete mg/<APEX_ID>_<PG>
git push origin --delete impl/<APEX_ID>-<ITEM>
# repeat for every mg/ and impl/ branch from § 3.3
```

### 4.5 — Delete the matching local branches

```powershell
git branch -D mg/<APEX_ID>_<PG>
git branch -D impl/<APEX_ID>-<ITEM>
# repeat for every mg/ and impl/ branch from § 3.2
```

### 4.6 — KEEP the `plan/<APEX_ID>` branch

The plan PR has already merged. Deleting `plan/<APEX_ID>` neither helps the
retry nor removes any stale state. Leave it. Likewise leave the
`feature/<APEX_ID>` branch — that is the integration trunk the rerun
targets.

### 4.7 — Reset the work-item state

```powershell
twig set <APEX_ID>
twig state <RESET_STATE>   # from § 5 table
```

### 4.8 — Flush twig's cache to ADO

```powershell
twig sync
```

Without § 4.8, twig's local cache still shows the old state and a subsequent
`polyphony validate` may compute a wrong `target_state`.

---

## 5 · Re-entry-ready state table

The reset target is always **one transition before the event you want the
workflow to re-fire on**. Read it out of `.polyphony-config/process-config.yaml`
in the apex worktree — specifically the `transitions:` block.

### General principle

For event `E` and type `T`:

1. Find `transitions[T][E]: <DEST_STATE>` in `process-config.yaml`.
2. Identify which state precedes `<DEST_STATE>` in `T`'s lifecycle (the
   LHS of the transition whose RHS *is* `<DEST_STATE>`, or for `begin_*`
   events the natural prior-stage state).
3. That earlier state is your reset target.

Stated more bluntly: **to retry from event `E`, reset to the state `E`
would *consume*, never the state `E` *produces*.**

### CloudVault CMMI (`process_template: CMMI` in `process-config.yaml`)

In the cloudvault-service-api apex worktree's `process-config.yaml`,
`begin_implementation: Started` for every implementable type (Deliverable,
Task Group, Task, Bug), and `begin_planning: Committed` for plannables
(Epic, Scenario, Deliverable, Task Group). The state preceding `Started`
in the CMMI lifecycle is `Committed`; the state preceding `Committed` is
`Proposed`.

| Type        | Retry from stage                                            | Reset state  |
|-------------|-------------------------------------------------------------|--------------|
| Deliverable | Implementation (plan PR already merged)                     | `Committed`  |
| Task Group  | Implementation                                              | `Committed`  |
| Task        | Implementation                                              | `Committed`  |
| Bug         | Implementation                                              | `Committed`  |
| Deliverable | Planning (plan PR not merged, or want a fresh plan pass)    | `Proposed`   |
| Scenario    | Planning                                                    | `Proposed`   |
| Epic        | Planning                                                    | `Proposed`   |

### Other process templates — derive the same way

The table above is CMMI-specific. For a Basic-process repo
(`To Do` / `Doing` / `Done`):

- `begin_implementation: Doing` → reset target is `To Do`.
- Planning event analog (if defined) → reset to whatever LHS state feeds
  `begin_planning`.

For Agile (`New` / `Active` / `Resolved` / `Closed`):

- `begin_implementation: Active` → reset target is `New` (or `Committed`
  if you've inserted that as a between-stage).

Always read the literal state name from `process-config.yaml` — it is the
exact string `twig state` will accept (see **`polyphony-bootstrap`** § 5b
on the "three vocabularies" hazard).

### Never reset to a terminal state

`Completed` / `Closed` / `Done` / `Cut` / `Removed` are terminal. The
workflow will not re-grab a terminal item, but ADO will lie about its
disposition. If you accidentally land there, transition back with another
`twig state` call to the correct re-entry state from the table above.

---

## 6 · Common gotchas

- **`gh auth switch` slips back to the human user.** The auth user reverts
  to `dangreen_microsoft` (or whoever) between PowerShell command groups,
  conductor invocations, and sometimes within a single session if anything
  else touched `gh`. Re-run `gh auth switch --user PolyphonyRequiem` before
  *each* push.
- **`twig` rewrites `.twig/config` on every conductor invocation.** First-run
  schema migration is documented in **`polyphony-bootstrap`** § 5f. During
  recovery you may need `git checkout -- .twig/config` between launches to
  prevent dirty-worktree errors.
- **ADO PRs can only be abandoned, not deleted.** Live with the abandoned
  PR rows in the ADO UI — they are inert.
- **`polyphony:planned` tag is harmless on retry.** Leave it. The planner
  reads it as advisory only and the implement phase doesn't care.
- **`polyphony:root` tag MUST stay on the apex root.** The dispatcher uses
  it to identify which work item is the apex. Stripping it will confuse the
  re-launch.
- **Never reset to a TERMINAL state** (Completed / Closed / Done / Cut /
  Removed). See § 5.
- **Multiple conductor processes can be running.** The launcher does NOT
  kill prior conductors before starting a new one — pinned-port collisions
  surface as 404s on `/api/kill`. Find orphans by start time:

  ```powershell
  Get-Process conductor* `
    | Where-Object StartTime -gt (Get-Date).AddHours(-6) `
    | Select-Object Id, ProcessName, StartTime, Path
  ```

  Then `Stop-Process -Id <pid>` each one before re-attempting § 2.1 or § 8.

---

## 7 · Quick smoke-test after cleanup

Run all three. If any fails, fix before re-launching — a re-launch over
half-cleaned state is what got you here.

```powershell
# 7.1 — polyphony agrees the apex is ready for begin_implementation
polyphony validate --work-item <APEX_ID> --event begin_implementation `
  | ConvertFrom-Json `
  | Select-Object is_valid, target_state
# expect: is_valid = True, target_state = Started (for CMMI implementable)

# 7.2 — no stale apex-scoped remote branches remain
git ls-remote origin "refs/heads/mg/*<APEX_ID>*" "refs/heads/impl/*<APEX_ID>*"
# expect: empty

# 7.3 — no active apex-related ADO PRs remain
az repos pr list `
  --org https://dev.azure.com/microsoft/ `
  --project <PROJECT> --repository <REPO> --status active `
  --query "[?contains(sourceRefName, '<APEX_ID>') || contains(targetRefName, '<APEX_ID>')]" `
  -o json
# expect: []
```

For a planning-stage retry, swap `begin_implementation` in 7.1 for
`begin_planning` and expect the prior-stage state from § 5.

---

## 8 · Re-launch

```powershell
gh auth switch --user PolyphonyRequiem
Invoke-PolyphonySdlc.ps1 -ApexId <APEX_ID> -Intent resume
```

Watch the conductor's stdout for re-entry into `implement-merge-group`. The
heuristic for a healthy retry: **at most one `primary_router` call per item
per merge group**. Repeated `primary_router` invocations on the same item
within one MG is the re-dispatch loop signature (AB#62286666 incident — see
§ 9). Kill and re-investigate if you see it.

Defer to **`polyphony-runtime`** for full invocation patterns, env-var
overrides, and launcher flags.

---

## 9 · Reference checkpoints (for skill maintainers)

Prior dogfood incidents this runbook consolidates. Read the checkpoint
*titles* for triage context; only crack open the body when revising a
specific section here.

- **AB#62286666** (2026-05-17, this incident) — apex-root re-dispatch loop +
  manual squash recovery. Drove the strict abandon-before-delete-remote
  ordering (§ 4.2 → § 4.4) and the multiple-conductor gotcha (§ 6).
- Earlier incidents under
  `C:\Users\dangreen\.copilot\session-state\*\checkpoints\` — filter for
  titles matching `fixing`, `shipping`, `dead-end`, `abandoned`,
  `recovery`, `trunk-recovery`. Notable starting points (titles only —
  do not pull whole bodies unless a section here is being revised):
  - `046-filed-ab-3210-3211-recov*`
  - `051-trunk-recovery-ab-3181*`
  - `018-fixing-lifecycle-router*`
  - `045-cloudvault-policy-shipped*`

When a new incident teaches a new rule, fold it into the matching numbered
section here and add a one-line entry above with the AB# and date.
