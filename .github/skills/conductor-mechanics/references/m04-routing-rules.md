# M4: Routing Rules — Tables, Carve-Outs, and Empty Defaults

Conductor has **three different routing primitives**, not one. Confusing
them is the core M4 footgun.

| Agent shape | Routing primitive | What "no match" does |
|---|---|---|
| Regular agent (script / LLM) with `routes:` | Table evaluated top-down by `engine/router.py` | `ValueError: No matching route found.` |
| Regular agent with **no** `routes:` table | Default → `$end` (`engine/workflow.py:3311-3313`) | (n/a) |
| `human_gate` agent | Routes via `options[i].route` on the chosen option | (n/a) — `routes:` on a gate is **rejected at validation** |
| `parallel` / `for_each` group | Group-level `routes:` evaluated against `outputs`/`errors`/`count` (M8) | `ValueError` like regular agent |

So the often-cited "you must always have a route" is **half true**. You must
have a route OR rely on the implicit `$end` default. The `ValueError` only
fires when you have a `routes:` table whose `when:` clauses **all** evaluate
falsy.

## Symptom

`ValueError: No matching route found for agent '<X>'...`
(`engine/router.py:108-111`) — sometimes from a state nobody intended to be
reachable (e.g. `phase=error` from a script that was supposed to always
return one of three known phases).

A subtler failure: the workflow truncates silently at an agent because you
forgot the `routes:` table entirely (default `$end`). No error fires; the
workflow just ends with the parent unable to read downstream agents'
outputs.

## The rule

For every `phase` / `verdict` / `action` field your routes branch on:

1. Enumerate **every value the field can produce**, including error /
   unknown / null.
2. Provide a `when:` clause for each one, OR a final unconditional route
   that acts as the catch-all.

## Catch-all forms

### Unconditional terminal route

```yaml
- name: state_detector
  type: script
  routes:
    - to: planning
      when: "{{ state_detector.output.phase == 'needs_planning' }}"
    - to: implementation
      when: "{{ state_detector.output.phase == 'needs_implementation' }}"
    - to: closing
      when: "{{ state_detector.output.phase == 'needs_close_out' }}"
    - to: error_gate                # ← catch-all, no `when:`
```

A route without a `when:` always matches and must be **last** in the table.

### Explicit error gate

```yaml
- to: error_gate
  when: "{{ state_detector.output.phase == 'error' }}"
```

Pair with a `human_gate` or a script that surfaces the failure with the
underlying script's stderr (see related: `detect-state.ps1` swallowing
stderr in AB#2923).

## Don'ts

- ❌ Trust that "the script will always return one of these three values"
  — file IO, shell environment, network, and bugs all conspire to break
  that assumption.
- ❌ Use `when: "true"` as a sentinel catch-all *unless* it's the last
  route — earlier-evaluated `true` routes match first and short-circuit
  the rest.
- ❌ Leave one apex workflow unprotected just because a sub-workflow has
  a catch-all — the apex does its own routing on the sub-workflow's
  `output:` map.
- ❌ Add a `routes:` table to a `human_gate` agent — silently ignored
  (gates route via `options[i].route`).
- ❌ Omit `routes:` and rely on the default `$end` to terminate cleanly —
  always declare an explicit `to: $end` so reviewers can see the intent.
- ❌ Reference `output.<x>` in a parallel/for-each group's `routes:` —
  groups have no `output` key (M8); use `outputs.<name>.<x>` instead.

## Dos

- ✅ Always provide a final unconditional route or an explicit error
  branch covering the script/agent's full output domain.
- ✅ Pester-lint your apex routing tables to detect missing catch-alls
  programmatically (see e.g. `tests/lint-apex-routing.Tests.ps1` in
  `polyphony-conductor-workflows`).
- ✅ When in doubt, route to a `human_gate` with the raw output — the
  human can then triage and decide.

## Discovery
Polyphony AB#2922. The `polyphony-full.yaml` apex routed three known
phases from `state_detector` but nothing handled `phase=error`, so a
real failure surfaced as `ValueError: No matching route found.`
instead of an actionable error.
