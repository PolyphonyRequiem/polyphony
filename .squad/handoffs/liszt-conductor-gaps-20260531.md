# Liszt's Conductor Gaps — Script-Author's View
**Date:** 2026-05-31  
**Author:** Liszt (PowerShell Expert)  
**For:** Daniel Green — offline review  
**Context:** Written after shipping `Poll-PrStateDelta.ps1` + patch series (eab39cb). Companions: Mahler (engine view), Wagner (workflow-author view).

---

## Framing

Every `script:` node in polyphony's workflows is a process boundary. Conductor launches a child process, reads its stdout as JSON, and uses the exit code and JSON fields for routing. Writing `Poll-PrStateDelta.ps1` — a **long-running, stateful, infrastructure-facing** script — stress-tested every seam of that boundary. Five gaps showed up. They are listed here in order of impact.

---

## Gap 1 — `on_error:` is Missing from Routes

**Impact rank: 1 | Cost-to-build: medium (RFC Phase 2 work)**

### Today

Both `github-pr.yaml` and `ado-pr.yaml` carry this comment above `poll_pr_state_delta`:

```yaml
# TODO(AB#3257): add on_error: to: poll_error_gate once conductor RFC
#   Phase 2 (on_error: in routes) ships. Until then, infrastructure
#   failures exit non-zero and surface as conductor step errors.
```

`Poll-PrStateDelta.ps1` exits **0** for all domain outcomes (including timeout) and exits **2/3/4/5** only for infrastructure failures (bad args, auth lapse, PR not found, network). That exit-code taxonomy is correct and well-documented in the script's `.SYNOPSIS`. But conductor has no `on_error:` key in a step's `routes:` block. The result:

- If `gh` auth has expired (exit 3), conductor raises an unhandled `ExecutionError` and the entire run stalls at a conductor-level error — no `poll_error_gate` fires, no operator notification, no clean recovery path.
- Wagner had to design around this: for `poll_status` (inline pwsh), the error is caught _inside_ the script and surfaced as `{ error: "...", route: "none" }` with exit 0, then a regular `when: "{{ poll_status.output.error is defined }}"` route handles it. This only works because `poll_status` is a 10-second one-shot. It does NOT work for `Poll-PrStateDelta.ps1` because the script must exit non-zero on infrastructure failures so callers that miss the `CONDUCTOR_ERROR_OUT` env var (see Gap 4) still know something went wrong.

### Why it hurts

Every long-running or network-dependent `script:` node needs a recovery path for auth lapses and transient failures. Without `on_error:`, the author's only options are: (a) absorb all errors into exit 0 and encode them in the JSON envelope — losing the ability to distinguish "domain timeout" from "network down" at the caller — or (b) exit non-zero and accept that the run dies unrecoverably. Both are bad.

**Lines of workaround:** Every routing script has an extra ~15-line `$source / $policy_error` pattern (see `resolve-pr-policy.ps1`, `resolve-unattended-cap-mode.ps1`) just to downgrade errors to `mode: manual` so they surface at the next human gate rather than crashing the run. That's the workaround for _policy-resolution_ scripts. For `Poll-PrStateDelta.ps1`, no equivalent downgrade is possible; it's a blocking poller, not a one-shot policy reader.

### What "fixed" looks like

```yaml
- name: poll_pr_state_delta
  type: script
  command: pwsh
  args: [...]
  routes:
    - to: poll_pr_state_delta
      when: "{{ poll_pr_state_delta.output.reaction_kind == 'initial_observation' }}"
    # ... domain routes ...
  on_error:           # ← new key
    - to: poll_error_gate
      when: "{{ script.exit_code == 3 }}"   # auth
    - to: poll_error_gate
      when: "{{ script.exit_code == 5 }}"   # network
    - to: poll_error_gate                    # catch-all
```

Conductor exposes `script.exit_code` and `script.stderr` as template variables inside `on_error:` conditions. The script can still exit non-zero with meaningful codes; the workflow handles the recovery. This also lets `CONDUCTOR_ERROR_OUT` be _optional_ rather than mandatory.

---

## Gap 2 — No Conductor Primitive for Between-Invocation State (Watermarks)

**Impact rank: 2 | Cost-to-build: medium (new node type or output-persistence contract)**

### Today

`Poll-PrStateDelta.ps1` needs to remember "what was the PR's state last time it polled" so it can detect _deltas_ (new commit, new review, etc.) rather than absolute state snapshots. Conductor has no storage primitive. The solution invented:

1. **File-based watermark:** The script writes a JSON snapshot to a filesystem path derived from the PR coordinates and reads it at startup.  
2. **Auto-derived path:** When `WatermarkPath` is not explicitly supplied, the script derives it from `[System.IO.Path]::GetTempPath()` + PR coords:

```powershell
# Poll-PrStateDelta.ps1 line 573
$WatermarkPath = Join-Path ([System.IO.Path]::GetTempPath()) "conductor-pr-delta-$pathKey.json"
```

3. **Atomic write pattern:**

```powershell
function Write-WatermarkAtomic {
    $tmpPath = $absPath + '.tmp.' + [System.IO.Path]::GetRandomFileName().Replace('.', '')
    [System.IO.File]::WriteAllText($tmpPath, $json, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmpPath -Destination $absPath -Force
}
```

4. **Initial-observation re-entry loop:** On first invocation (no watermark file), the script records current state and returns `reaction_kind: "initial_observation"` with exit 0. The YAML routes this back to `poll_pr_state_delta` so the next invocation polls for changes from the recorded baseline.

```yaml
routes:
  - to: poll_pr_state_delta
    when: "{{ poll_pr_state_delta.output.reaction_kind == 'initial_observation' }}"
```

### Why it hurts

The temp-dir approach is fragile in several ways:
- **Machine boundary:** In a distributed conductor setup where each step may run on a different agent/machine, the watermark file at `$TEMP/conductor-pr-delta-...json` on machine A doesn't exist on machine B. The script silently treats this as "first observation," resets the watermark, and emits `initial_observation` — losing all prior delta state.
- **Reboot:** Temp files are cleared on reboot. After an OS restart mid-poll, the watermark is gone; same silent-reset behavior.
- **Same root, multiple PRs:** If two PRs share the same root run and both invoke `poll_pr_state_delta` in the same wave, they each get their own watermark keyed by PR coords — fine for isolation, but the paths are undiscoverable without running the script and reading its stderr.
- **Debuggability:** Watermark location is opaque. Operators diagnosing a stuck poll have to grep `$TEMP` to find the state file.
- **No watermark TTL:** A stale watermark from a cancelled run is never cleaned up; the next run picks it up and sees no delta against 6-month-old state.

The **initial-observation loop** (a full extra conductor iteration per poller startup) also exists solely because there's no "if first run, do X" primitive in conductor.

### What "fixed" looks like

**Option A — Step-scoped key-value store:** Conductor provides a `type: store` step type and `{{ store.<key> }}` in Jinja:

```yaml
- name: load_watermark
  type: store
  op: get
  key: "pr_delta_watermark_{{ workflow.input.pr_number }}"
  default: null

- name: poll_pr_state_delta
  type: script
  args:
    - "--WatermarkJson"
    - "{{ load_watermark.output.value | tojson }}"
  # ... script receives the prior watermark on stdin or as an arg; no file I/O needed
```

**Option B — Workflow-scoped `state:` section:** Conductor tracks named fields across step re-entries. The YAML declares `state: { watermark: null }` and the script reads/writes via environment variables (`CONDUCTOR_STATE_watermark`). The script never touches the filesystem for persistence.

Either option eliminates the temp-file path, the machine-boundary problem, the initial-observation loop, and the TTL issue.

---

## Gap 3 — `script:` Node Contract is Underspecified (stdin / stdout / stderr / cwd)

**Impact rank: 3 | Cost-to-build: low (documentation + reference script template)**

### Today

The `script:` node spec defines:
- `command:` — executable name
- `args:` — argument list (Jinja-expanded)
- `routes:` — routing conditions on `{{ step_name.output.<field> }}`

What it **does not** define:
1. **stdin:** Is stdin `/dev/null` or does conductor write anything? The script must not read stdin; there's no spec saying so. `Poll-PrStateDelta.ps1` uses `ProcessStartInfo` with `RedirectStandardOutput/Error` but does not redirect stdin — a `WaitForExit` deadlock is possible if conductor writes to stdin and the buffer fills.
2. **stdout contract:** All scripts output JSON. Conductor reads the last JSON object? All stdout? Does it accumulate over the whole run (including progress lines)? `Poll-PrStateDelta.ps1` writes **all** progress to stderr and only outputs a single JSON object at the very end — but that convention was invented here, not specified by conductor. A script that writes a JSON object per poll interval would break the consumer.
3. **cwd:** What is the working directory when conductor launches the script? The YAML uses `{{ workflow.dir }}/../scripts/Poll-PrStateDelta.ps1` to get an absolute path to the script — but the cwd of the process is undocumented. Scripts that do `ConvertFrom-Json` on relative paths (or use `[Environment]::CurrentDirectory`) are silently broken if conductor changes directory before launching.
4. **Environment variables:** `CONDUCTOR_ERROR_OUT` (Gap 4) is an undocumented convention. Other env vars (`GH_TOKEN`, `AZURE_DEVOPS_PAT`) are assumed to be inherited, but whether conductor inherits the full parent environment is not documented.
5. **Process group / job object:** Can a script spawn child processes and expect them to survive? Can it use `Start-Job`? When conductor kills the script (timeout/abort), does it send SIGTERM to the process group or just the immediate process?

### Why it hurts

Every new script author has to read existing scripts to figure out the conventions. The `Write-Stderr` helper in `Poll-PrStateDelta.ps1`:

```powershell
function Write-Stderr {
    param([string]$Message)
    [Console]::Error.WriteLine($Message)
}
```

...was invented specifically to keep progress output out of the JSON stdout channel. The `Invoke-CliCaptured` async-read pattern (using `ReadToEndAsync()` for both stdout and stderr simultaneously) was invented to avoid a deadlock risk inherent in sequential stdout-then-stderr reads. Neither convention is documented by conductor.

The `workflow.dir` path variable is critical — every external `script:` node uses it to locate scripts relative to the workflow YAML. But `workflow.dir` is undocumented; it was discovered by reading existing YAML, not from conductor's docs.

### What "fixed" looks like

A one-page "Script Node Contract" in conductor's docs covering:
- **stdin:** Closed (no data) before the process starts.
- **stdout:** Conductor collects all bytes written to stdout; on exit 0, the last complete JSON object is parsed as `step.output`. (Or: the full stdout buffer is the JSON.)
- **stderr:** Forwarded to conductor's step log. Not parsed by conductor.
- **cwd:** The repo root (or workflow's working directory — whichever, but it must be stated).
- **Environment:** Inherits parent process env. `CONDUCTOR_ERROR_OUT` is officially supported and documented.
- **Kill semantics:** On timeout/abort, conductor sends SIGTERM to the process group, waits 5 seconds, then SIGKILL. Scripts can clean up in a `finally` block.

A reference script template in `.conductor/registry/reference/` would also help — there is one (`reference/task-router.ps1`) but it doesn't cover all these edge cases.

---

## Gap 4 — `CONDUCTOR_ERROR_OUT` is an Undocumented Convention

**Impact rank: 4 | Cost-to-build: low (official support + doc)**

### Today

When `Poll-PrStateDelta.ps1` hits an infrastructure error (auth lapse, network failure), it must communicate structured error details back to any routing system. But because `on_error:` doesn't exist yet (Gap 1), the structured error has nowhere to go in conductor's route table. The workaround: `CONDUCTOR_ERROR_OUT`.

```powershell
# Poll-PrStateDelta.ps1 lines 526-535
$errOut = $env:CONDUCTOR_ERROR_OUT
if ($errOut) {
    $envelope = [ordered]@{
        kind    = $kind        # 'auth_failure' | 'network_failure' | ...
        message = $Message
        details = @{ exit_code = $Code }
    }
    $envelope | ConvertTo-Json -Compress |
        Set-Content -LiteralPath $errOut -Encoding utf8NoBOM
}
```

The convention: conductor sets `CONDUCTOR_ERROR_OUT` to a temp file path; the script writes a structured JSON error envelope there; conductor reads it for display in the step-error UI. This convention exists in `Poll-PrStateDelta.ps1` but is checked first by looking at existing scripts to see if it was used elsewhere — it isn't. It was invented from scratch.

### Why it hurts

- Any script that doesn't check `$env:CONDUCTOR_ERROR_OUT` loses structured error context on failure. The error in the conductor UI is just the raw stderr dump.
- Since conductor doesn't officially set this variable, the `if ($errOut)` block is dead code on every actual conductor run today — it only fires if a wrapper or test explicitly sets the env var.
- There's no shared helper for this pattern; each script that wants structured errors re-invents the envelope schema.

### What "fixed" looks like

Conductor officially defines `CONDUCTOR_ERROR_OUT` as a well-known env var (or uses an equivalent mechanism like a side-channel file at a predictable path). It reads the file on non-zero exit and populates `script.error` in the step result, making it accessible in `on_error:` conditions:

```yaml
on_error:
  - to: poll_error_gate
    when: "{{ script.error.kind == 'auth_failure' }}"
```

Until `on_error:` ships, the value in making this official is primarily diagnostic — structured error bodies in the conductor UI rather than raw stderr paste.

---

## Gap 5 — Timeout / Cancellation Semantics for Blocking Scripts

**Impact rank: 5 | Cost-to-build: medium (conductor engine + script-side cleanup protocol)**

### Today

`Poll-PrStateDelta.ps1` implements its own polling loop with `Start-Sleep`. The script can block for up to `TimeoutSeconds` (default 86400 = 24 hours). Conductor has no `timeout:` key on a `script:` node. The behavior when conductor's global run timeout fires or the operator aborts mid-poll is undocumented.

The best guess from reading the abort pattern (`abort-run.ps1` which POSTs to `/api/stop` via HTTP) and the comment in `github-pr.yaml`:

> the engine raises InterruptError at every nested between-agent check, unwinding the entire run cleanly (no process-kill, no zombies)

...is that conductor doesn't kill the script's process on abort. It may just stop routing after the step finishes. But if the script is sleeping for 30 seconds, the abort doesn't take effect until after the next poll cycle returns. A 24-hour poll timeout means the operator-abort UX is: click Abort, wait up to 30 seconds, see the run stop.

If conductor DOES kill the process, the atomic watermark write (tmp-file + Move-Item) means the watermark is safe — we won't get a half-written file. But there's no cleanup for `$proc.Kill($true)` inside `Invoke-CliCaptured` if the _outer_ process gets killed while an inner `gh` API call is in-flight.

```powershell
# Invoke-CliCaptured: $proc.WaitForExit($TimeoutSeconds * 1000) is a
# per-API-call inner timeout, NOT a conductor-level abort signal.
# If the outer pwsh process is killed, $proc is orphaned.
```

### Why it hurts

- **Orphaned child processes:** If conductor kills `pwsh` mid-`Invoke-CliCaptured`, the spawned `gh` or `az` process continues running until its own network timeout (~60s). Not a serious problem but observable in process monitors.
- **No graceful cleanup:** Temp files, in-flight atomic writes, and any locks held by `gh` or `az` are abandoned without a cleanup pass.
- **No cooperative cancellation:** The script can't check "has the conductor asked me to stop?" between polls. The only mechanism is an OS kill signal. Under Windows, `CTRL_C_EVENT` / `CTRL_BREAK_EVENT` can be sent to a process group — but conductor doesn't document whether it does this or just uses `TerminateProcess`.
- **No step-level timeout:** A hung `gh` call (network partition, GitHub outage) can cause `WaitForExit` to block for up to 60s per API call. In the worst case, this multiplies: if GitHub's API hangs on every call for a full PR lifecycle review, the script could block for O(hours) inside a single poll cycle before the inner timeout fires.

### What "fixed" looks like

**Minimum viable fix:** Conductor supports `timeout_seconds:` on `script:` nodes. When the timeout fires, conductor sends a well-known signal (e.g. sets an env var `CONDUCTOR_CANCEL_REQUESTED=1`, then waits 5s before SIGTERM). Scripts can check `$env:CONDUCTOR_CANCEL_REQUESTED` at each poll iteration:

```powershell
while ($true) {
    if ($env:CONDUCTOR_CANCEL_REQUESTED -eq '1') {
        Write-Stderr "[Poll-PrStateDelta] Conductor cancel requested — exiting cleanly."
        # Write final watermark, emit timeout envelope, exit 0.
        exit $EXIT_SUCCESS
    }
    # ... poll logic ...
    Start-Sleep -Milliseconds $sleepMs
}
```

**Better fix:** Conductor supports `type: timer` nodes that fire after a duration and inject a route, so long-running polls are implemented as a conductor-native timer loop (one short-running script invocation per tick) rather than a long-running blocking script. This is architecturally cleaner but requires conductor to own the state-between-invocations problem (see Gap 2).

---

## Bonus: The Shell-Out Idiom — Refined

After writing `Poll-PrStateDelta.ps1`, here is the refined decision matrix for when a node should use a polyphony verb directly, inline pwsh, or a helper script:

### Decision Matrix

| Trigger | Form |
|---|---|
| Output IS the verb's JSON, no transformation | `command: polyphony args: [verb, flags]` |
| ≤15 lines, single-purpose, no persistent state, no tests needed | Inline pwsh (`-Command '...'`) |
| Needs Pester tests | Helper script (`-File scripts/Foo.ps1`) |
| Persists state between conductor invocations | Helper script |
| Blocks/sleeps for > a few seconds | Helper script |
| Used by more than one workflow node or workflow file | Helper script |
| Needs sophisticated exit-code / error-kind classification | Helper script |

### Examples

```yaml
# Polyphony verb directly — output IS the verb's JSON, no transform.
- name: guidance_loader
  type: script
  command: polyphony
  args: ["guidance", "load", "--work-item", "{{ workflow.input.work_item_id }}"]

# Inline pwsh — 10 lines, self-contained, no state, never tested separately.
- name: poll_status
  type: script
  command: pwsh
  args:
    - "-Command"
    - |
      $json = polyphony pr poll-status --pr-url $prUrl 2>&1 | Out-String
      if ($LASTEXITCODE -ne 0) {
        @{ error = $json; route = 'none' } | ConvertTo-Json -Compress; exit 0
      }
      $json.Trim()

# Helper script — blocking poller, stateful watermark, Pester tests,
# complex error taxonomy, used by both github-pr.yaml and ado-pr.yaml.
- name: poll_pr_state_delta
  type: script
  command: pwsh
  args: ["-File", "{{ workflow.dir }}/../scripts/Poll-PrStateDelta.ps1", ...]
```

### One-Paragraph Statement (for skill)

> Use a polyphony verb directly when the conductor step's output IS the verb's raw JSON and no transformation is needed. Use inline pwsh (`-Command '...'`) for self-contained logic under ~15 lines that doesn't need its own Pester test suite — short URL-construction, pass-through error detection, or single-field extraction. Escalate to a named helper script under `scripts/*.ps1` whenever: (a) the logic needs Pester tests, (b) state must persist between conductor invocations (watermarks, sentinel flags, counters), (c) the operation blocks for more than a few seconds (polling loops, network retries), (d) the error classification is non-trivial (distinct exit codes mapping to distinct recovery paths), or (e) the same logic is called from more than one workflow YAML. Helper scripts take params not template-expanded args, which keeps the Jinja surface small and the test harness clean.

---

## Summary of Gaps by Priority

| Rank | Gap | Workaround cost | Fix complexity |
|---|---|---|---|
| 1 | `on_error:` missing from routes | ~15 lines per policy-router script; poll errors crash runs | Medium (RFC Phase 2) |
| 2 | No between-invocation state primitive | File-based watermarks, temp-dir fragility, initial-obs loop | Medium (new node type or state contract) |
| 3 | `script:` contract underspecified | Convention-by-example, invented `Write-Stderr` / async-read patterns | Low (docs + reference template) |
| 4 | `CONDUCTOR_ERROR_OUT` undocumented | Dead code in every script; raw stderr on failure | Low (official support + doc) |
| 5 | No timeout/cancellation for blocking scripts | Scripts implement own budgets; no cooperative cancel | Medium (timer node type or cancel signal) |

---

## Files Examined

- `scripts/Poll-PrStateDelta.ps1` — primary artifact
- `.conductor/registry/workflows/github-pr.yaml` (lines 1073–1121) — YAML caller of the script
- `.conductor/registry/workflows/ado-pr.yaml` (lines 1188–1207) — ADO equivalent
- `.conductor/registry/scripts/resolve-pr-policy.ps1` — routing-style envelope pattern
- `.conductor/registry/scripts/resolve-unattended-cap-mode.ps1` — error-downgrade pattern
- `.conductor/registry/scripts/lifecycle-router.ps1` — always-exit-0 / routing envelope
- `.conductor/registry/scripts/batch-dispatch-guard.ps1` — file-based sentinel (state-between-invocations pattern)
