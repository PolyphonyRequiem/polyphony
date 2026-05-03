# M9: Limits, Retries & Checkpoints

The runtime safety net is **conservative by default and silently engaged**.
Workflows that lint clean can hit limits, retry transparently, or refuse to
resume from a checkpoint after a cosmetic edit.

## `max_iterations` defaults to **10** (`engine/limits.py:49`)

`limits.max_iterations` defaults to 10 (range: 1–500). A workflow
tick = one node execution; **a parallel group of N agents counts as N
iterations.** A non-trivial workflow with one parallel group of 5 reviewers
+ a few setup/teardown agents will hit the cap on the first run.

The default is too low for any real polyphony-style workflow. Set it
explicitly. **Important:** `runtime:` and `limits:` are **siblings** at
the workflow top level (both fields of `WorkflowDef`,
`config/schema.py:824, 833`) — `limits:` is NOT nested inside `runtime:`.
A nested `runtime.limits.max_iterations:` is silently ignored.

```yaml
# ✅ Correct — runtime and limits are siblings of entry_point
name: my-workflow
entry_point: first_step

runtime:
  provider: copilot

limits:
  max_iterations: 200       # pick a real number for your workflow
  max_depth: 5

agents:
  - name: first_step
    ...

# ❌ Wrong — silently ignored, max_iterations stays at 10
runtime:
  provider: copilot
  limits:
    max_iterations: 200
```

When the cap is hit, the run halts and (in interactive mode) prompts the
user to extend or abort. With `--no-interactive`, the run fails.

## Other limit fields (`config/schema.py: LimitsConfig`)

| Field | Default | Purpose |
|---|---|---|
| `max_iterations` | 10 | Total node executions per run |
| `max_depth` | 5 | Sub-workflow nesting depth |
| `max_session_seconds` | (none) | Wall-clock cap |
| `max_agent_iterations` | (none) | Per-agent re-entry cap |

`max_depth` matters for recursive PG-style workflows that spawn child
workflows that may themselves spawn children.

## Retry policies (`config/schema.py:351`, `RetryPolicy`)

**Important correction:** transient retry happens **by default** at the
provider layer even when an agent has no `retry:` block. Both providers
default to `max_attempts: 3` for transient errors:

- Copilot: `RetryConfig.max_attempts = 3` (`providers/copilot.py:69-91`),
  used as fallback when `agent.retry` is absent (`copilot.py:281-340`).
- Claude: `max_attempts = 3` (`providers/claude.py:90-111`),
  used as fallback when `agent.retry` is absent (`claude.py:529-555`).

So "I forgot to add `retry:`" does **not** mean "one transient blip kills
the run." Conductor already retries 3× on rate limits, timeouts, and
provider-side transient failures.

Per-agent `retry:` is for **overrides** — typically when you want:

- More than 3 attempts for a known-flaky external dependency
- A specific `retry_on:` allowlist of error classes
- A different `backoff_seconds`

```yaml
- name: flaky_call
  type: agent
  retry:
    max_attempts: 5
    backoff_seconds: 2
    retry_on: ["RateLimitError", "TimeoutError"]
```

**Underused in polyphony workflows.** Worth adding to any agent that hits
external APIs (LLMs, MCP tools, network calls).

## Checkpoints (`engine/checkpoint.py`)

Conductor automatically checkpoints workflow state to enable
`conductor resume`.

### Storage location
`$TMPDIR/conductor/checkpoints/` (`engine/checkpoint.py:100-114`).

On Windows that's typically `%TEMP%\conductor\checkpoints\`. **System
cleanup or a reboot can wipe these.** If you need durable resume across
reboots, copy the checkpoint file to a stable location and use
`conductor resume --from <path>`.

### Validity hash

Each checkpoint stores a hash of the **workflow file content**
(`engine/checkpoint.py:117-128`). Editing the workflow — even cosmetic
whitespace — invalidates all prior checkpoints.

This is intentional (workflow shape changes break replay determinism), but
it means: **don't reformat your workflow YAML mid-debug if you want to
resume from the checkpoint you just hit.**

## Sub-workflow path resolution (`engine/workflow.py:563-676`)

`type: workflow` agents resolve `workflow:` paths **relative to the parent
YAML's directory**, not the working directory. Moving the parent file to a
different folder can break sub-workflow resolution even when the child
file is still in the same place it always was.

For portable references, use:
- An absolute path (rare; usually wrong for shipping workflows).
- A registry reference (`workflow: implement-pg@polyphony`) — registry resolves regardless of caller location.
- `{{ workflow.dir }}` interpolation (where supported).

## Sub-workflow input coercion (`engine/workflow.py:629-639`)

Inputs passed to a sub-workflow via `inputs:` pass through `_maybe_parse_json`
**after rendering**. So `inputs: { count: "{{ planner.output.count }}" }`
where the planner emits the string `"5"` arrives as `int(5)`, and
`flag: "false"` arrives as `False`. See M7 for the same coercion at
workflow-scope `output:`.

## Don'ts

- ❌ Rely on the default `max_iterations: 10` — almost always too low.
- ❌ Edit a workflow YAML between hitting an error and trying to resume.
- ❌ Trust `$TMPDIR/conductor/checkpoints/` for long-term recovery.
- ❌ Claim "no `retry:` means no retry" — providers retry 3× by default.
- ❌ Move a parent YAML to a different folder without auditing sub-workflow refs.

## Dos

- ✅ Set `limits.max_iterations` explicitly per workflow (top-level sibling
  of `runtime:`, NOT nested inside it).
- ✅ Add `retry:` only when you need a **non-default** policy (more attempts,
  custom backoff, specific `retry_on:` allowlist).
- ✅ Save important checkpoints to a non-temp location if you want durable resume.
- ✅ Use registry references (`@polyphony`) for sub-workflows where possible.

## Validation gap (idea for upstream)

`conductor validate` could warn when:
- `max_iterations` is left at the default and the workflow contains a
  parallel/for-each block.
- A sub-workflow path is relative to a folder that doesn't appear stable
  (e.g. uses `..`).
- An agent that calls external APIs needs **stronger-than-default** retry
  (>3 attempts or a specific `retry_on:` allowlist) but has no `retry:` block.

## Discovery

Surfaced during conductor-fix research while looking for runtime safety
nets the dogfood was silently bumping into. The default of 10 iterations
is buried — it's in `engine/limits.py`, not in the user-facing schema docs.
