# M10: Iterate-Until-Stable Loop Pattern

> **Closes:** issue [#222](https://github.com/PolyphonyRequiem/polyphony/issues/222)
>
> **Cited:** [`apex-driver.yaml`](https://github.com/PolyphonyRequiem/polyphony/blob/main/.conductor/registry/workflows/apex-driver.yaml)
> outer-loop pattern; see also `docs/decisions/apex-driver.md`.

## The problem

Conductor has **no first-class loop primitive.** A workflow that needs to
"keep doing X until a stable terminal condition is reached" cannot say so
directly — there is no `loop:` or `while:` keyword.

Without a pattern, authors reach for one of two anti-patterns:

1. **`max_iterations` brute force** — set a high cap and hope. Brittle: too
   low and you abort mid-loop; too high and a real infinite loop chews
   tokens until the cap fires.
2. **Recursive sub-workflow invocation** — spawns a fresh engine state per
   iteration. Loses checkpoints, accumulates orphaned manifest entries,
   makes resume impossible.

## The pattern (verified in `apex-driver.yaml`)

A graph-cycle-with-conditional-route plus a temp-file iteration counter
side-channel plus priority-ordered `when:` clauses plus a defensive
catch-all M4 terminal.

### Anatomy

```yaml
# Step 1: Initialize the iteration counter on a fresh entry.
outer_loop_init:
  type: script
  command: pwsh
  args:
    - -NoProfile
    - -Command
    - |
      $iterPath = Join-Path ([System.IO.Path]::GetTempPath()) "apex-driver-iter-{{ inputs.apex_id }}"
      Set-Content -Path $iterPath -Value "0" -Encoding ascii
  next:
    - to: build_worklist

# Step 2: The work that may need to repeat.
build_worklist:
  # ... do the actual work ...
  next:
    - to: outer_loop_evaluator

# Step 3: The decision.
outer_loop_evaluator:
  type: script
  command: pwsh
  args:
    - -NoProfile
    - -Command
    - |
      $iterPath = Join-Path ([System.IO.Path]::GetTempPath()) "apex-driver-iter-{{ inputs.apex_id }}"
      $iter = [int](Get-Content $iterPath) + 1
      Set-Content -Path $iterPath -Value $iter -Encoding ascii

      # ... evaluate state, decide one of: complete | cap | blocked | continue ...
      $decision = if ($apexSatisfied) { 'complete' }
                  elseif ($iter -ge 50) { 'cap' }
                  elseif ($noProgress) { 'blocked' }
                  else { 'continue' }

      ConvertTo-Json @{ decision = $decision; iteration = $iter } -Compress
  next:
    # PRIORITY-ORDERED: complete > cap > blocked > continue, then catch-all.
    - to: terminal_apex_satisfied
      when: '{{ outer_loop_evaluator.output.decision == "complete" }}'
    - to: terminal_apex_capped
      when: '{{ outer_loop_evaluator.output.decision == "cap" }}'
    - to: terminal_apex_blocked
      when: '{{ outer_loop_evaluator.output.decision == "blocked" }}'
    - to: build_worklist
      when: '{{ outer_loop_evaluator.output.decision == "continue" }}'
    # CATCH-ALL must NOT loop back to build_worklist.
    - to: terminal_apex_blocked
```

### The four critical pieces

1. **The cycle is a `next:` route, not a recursive sub-workflow call.** The
   workflow graph contains a literal back-edge from `outer_loop_evaluator`
   to `build_worklist`. Conductor handles this without complaint as long
   as `max_iterations` is high enough.

2. **The iteration counter is per-instance.** Use
   `Path.GetTempPath() / apex-driver-iter-{instance_id}`. The `{instance_id}`
   must be the apex / root id, not a hardcoded string — concurrent runs
   would otherwise share a counter and deadlock each other.

3. **`when:` clauses are priority-ordered, with the cycle route LAST.**
   Conductor evaluates `when:` in declaration order and takes the first
   match. Ordering `complete` > `cap` > `blocked` > `continue` ensures a
   terminal decision wins over an in-flight `continue`. **Reverse this and
   the loop never terminates.**

4. **The M4 catch-all goes to a TERMINAL, not back to the cycle.** If the
   evaluator fails to emit a recognized decision (bug, JSON parse failure,
   missing branch in the `if` chain), the catch-all routes to a blocked
   terminal so the run halts visibly. Routing the catch-all back to the
   cycle would mask the bug as an infinite loop.

## Companion concerns

- **Don't share a counter across runs.** The temp-file path **must**
  include a per-instance discriminator. Polyphony uses the apex id;
  generic patterns can use the conductor instance id (available as
  `{{ run.instance_id }}` if exposed).

- **Set `max_iterations:` higher than your worst-case loop count.**
  Conductor's M9 default is 10, which is almost always too low for an
  iterate-until-stable loop. Set `max_iterations: 100` (or higher) on
  the loop step explicitly.

- **Clean up the counter file at terminal entry.** Each terminal step
  should remove the temp file so a re-entry doesn't see a stale count.
  Polyphony's terminals do this in their respective `script:` bodies.

- **Resume re-initializes.** On re-entry (`intent=resume`), the
  `outer_loop_init` step runs again and resets the counter. This is by
  design: the iteration count is "iterations of THIS run", not
  "iterations across the apex's lifetime."

## Anti-patterns to recognize

| Smell                                                | Fix                                                         |
| ---------------------------------------------------- | ----------------------------------------------------------- |
| `max_iterations: 1000` on a loop step                | Iteration counter pattern with explicit decision step       |
| Counter file at hardcoded path                       | Add per-instance discriminator                              |
| `when:` ordering puts `continue` first               | Reverse — terminal decisions first                          |
| No catch-all `next:` after the conditional routes    | Add a catch-all to a terminal, never back to the cycle      |
| Catch-all loops back to the cycle "just in case"     | This **is** the infinite-loop bug. Catch-all must terminate |
| Recursive `sub_workflow:` call from the loop body    | Use the in-graph back-edge instead                          |

## Cross-references

- **Source of truth:** `.conductor/registry/workflows/apex-driver.yaml`,
  `outer_loop_init` and `outer_loop_evaluator` steps (PR #221, SHA `428d818`).
- **ADR:** `docs/decisions/apex-driver.md`.
- **Memory (verified 2026-05):** "conductor-loops" — Conductor has no built-in
  until-stable loop; this is the pattern.
- **Companion:** [M9 — Limits, Retries & Checkpoints](m09-limits-retries-checkpoints.md)
  covers the `max_iterations:` knob this pattern depends on.
